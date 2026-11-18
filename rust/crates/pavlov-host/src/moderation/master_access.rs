//! Master names: on every `mods.txt`, whitelist and roster, and on the `owner` menu tier
//! (`ServerRoles/owner.txt`), re-checked when they join (C# `Moderation/MasterAccess.cs`; the
//! RCON+ `GiveMenu` on join was replaced by the tier file on 2026-11-18).
//!
//! A name REMOVED from `MASTER_NAMES` loses everything it was given, at the next start: the
//! files, the rosters and every menu tier. Dropping the name used to only stop new grants. The
//! grants are recorded (`master_grants`), and a name on every roster counts as a grant too,
//! since nobody but a master is ever put there.
//!
//! Join grants run in the background, after a short delay, so a burst of joins cannot hold up
//! the log pipeline. [`MasterAccess::stop`] waits for them.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError, Weak};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::MenuTier;
use pavlov_core::servers::installs::whitelist_path;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::{IgnoreCaseSet, fold_case};
use pavlov_core::time::Clock;
use pavlov_storage::datasets::MASTER_GRANTS;
use pavlov_storage::whitelist::{WhitelistFile, entry};
use tokio::sync::watch;
use tokio::task::JoinSet;

use super::{MasterNames, StoredMasterNames};
use crate::factions::RosterService;
use crate::logs::{IpTrackingService, PlayerJoined};
use crate::server_labels::ServerLabels;

/// How long after a join the grant is sent: the game is not ready for it the moment they connect.
pub const DEFAULT_GRANT_DELAY: Duration = Duration::from_secs(8);
/// One grant per master per server per window: Pavlov writes several login lines per join.
pub const REGRANT_COOLDOWN: TimeDelta = TimeDelta::minutes(2);

/// `<install>/Pavlov/Saved/Config/mods.txt`.
#[must_use]
pub fn mods_path(install_root: &str) -> String {
    format!(
        "{}/Pavlov/Saved/Config/mods.txt",
        install_root.trim_end_matches('/')
    )
}

/// What the service is wired to. `rosters` and `store` are optional: without a store nothing
/// is recorded, so nothing can be revoked.
pub struct MasterAccessParts {
    pub masters: Arc<StoredMasterNames>,
    pub servers: Arc<ServerLabels>,
    pub installs: Vec<String>,
    pub files: Arc<WhitelistFile>,
    pub clock: Arc<dyn Clock>,
    pub grant_delay: Duration,
    pub rosters: Option<Arc<RosterService>>,
    pub store: Option<Arc<SerializedStore>>,
}

impl std::fmt::Debug for MasterAccessParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MasterAccessParts")
            .field("installs", &self.installs)
            .finish_non_exhaustive()
    }
}

/// The service.
pub struct MasterAccess {
    parts: MasterAccessParts,
    last_grant: Mutex<HashMap<String, DateTime<Utc>>>,
    pending: Mutex<JoinSet<()>>,
    stopping: watch::Sender<bool>,
}

impl std::fmt::Debug for MasterAccess {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MasterAccess")
            .field("installs", &self.parts.installs)
            .finish_non_exhaustive()
    }
}

impl MasterAccess {
    #[must_use]
    pub fn new(parts: MasterAccessParts) -> Arc<Self> {
        Arc::new(Self {
            parts,
            last_grant: Mutex::new(HashMap::new()),
            pending: Mutex::new(JoinSet::new()),
            stopping: watch::channel(false).0,
        })
    }

    fn rosters(&self) -> Option<&RosterService> {
        self.parts.rosters.as_deref().filter(|r| r.enabled())
    }

    fn masters(&self) -> Vec<String> {
        self.parts
            .masters
            .masters()
            .iter()
            .map(str::to_owned)
            .collect()
    }

    fn track(&self, work: impl std::future::Future<Output = ()> + Send + 'static) {
        let mut pending = self.pending.lock().unwrap_or_else(PoisonError::into_inner);
        // Reap what has finished, so the set does not grow for the life of the bot.
        while pending.try_join_next().is_some() {}
        pending.spawn(work);
    }

    /// Revoke removed masters, put the current ones in every file, and (with a tracker) grant
    /// on join from now on.
    pub async fn start(self: &Arc<Self>, tracking: Option<&IpTrackingService>) {
        if let Some(tracking) = tracking {
            let weak: Weak<Self> = Arc::downgrade(self);
            tracking.joined.subscribe(move |join| {
                if let Some(access) = weak.upgrade() {
                    access.on_joined(&join);
                }
                Box::pin(async {})
            });
        }
        self.revoke_removed().await;
        self.ensure_files().await;
    }

    /// Stop granting, and wait up to `wait` for grants and revokes already under way.
    pub async fn stop(&self, wait: Duration) {
        self.stopping.send_replace(true);
        let mut pending =
            std::mem::take(&mut *self.pending.lock().unwrap_or_else(PoisonError::into_inner));
        let drained =
            tokio::time::timeout(wait, async { while pending.join_next().await.is_some() {} })
                .await;
        if drained.is_err() {
            tracing::warn!(
                left = pending.len(),
                "stopped with master grants still pending"
            );
            pending.abort_all();
        }
    }

    /// Take back what every former master was given. The record is rewritten as the current
    /// masters plus any revoke that did not finish, so that one is retried next start.
    pub async fn revoke_removed(self: &Arc<Self>) {
        let Some(store) = &self.parts.store else {
            return;
        };
        let current = self.parts.masters.masters().clone();
        let granted: Vec<String> = store.read(MASTER_GRANTS, Vec::new()).unwrap_or_else(|error| {
            tracing::error!(%error, "cannot read the master grants - nothing revoked this start");
            Vec::new()
        });
        let leftovers = self
            .rosters()
            .map(RosterService::names_on_every_roster)
            .unwrap_or_default();

        let mut seen = IgnoreCaseSet::new();
        let removed: Vec<String> = granted
            .into_iter()
            .chain(leftovers)
            .filter(|n| !current.contains(n))
            .filter(|n| seen.insert(n.as_str()))
            .collect();

        let mut unfinished = Vec::new();
        for name in removed {
            if !self.revoke(&name).await {
                unfinished.push(name);
            }
        }
        let record: Vec<String> = current
            .iter()
            .map(str::to_owned)
            .chain(unfinished)
            .collect();
        if let Err(error) = store.write(MASTER_GRANTS, &record).await {
            tracing::error!(%error, "cannot record the master grants - a removal may not be revoked");
        }
    }

    /// True when everything was taken back.
    async fn revoke(self: &Arc<Self>, name: &str) -> bool {
        let entry = entry(Some(name));
        let mut complete = true;
        for install in &self.parts.installs {
            for path in [mods_path(install), whitelist_path(install)] {
                let result = self.parts.files.remove(&path, &entry).await;
                if !result.ok {
                    complete = false;
                    tracing::warn!(name = %entry, path = %result.path, error = result.error.as_deref().unwrap_or(""), "could not remove a former master");
                }
            }
        }
        if let Some(rosters) = self.rosters() {
            let (_, failed) = rosters
                .remove_from_every_roster(std::slice::from_ref(&entry))
                .await;
            if !failed.is_empty() {
                complete = false;
                tracing::warn!(name = %entry, files = %failed.join(", "), "could not take a former master off the rosters");
            }
        }
        if let Some(rosters) = self.rosters() {
            for tier in MenuTier::ALL {
                if rosters
                    .remove_names(&tier.file(), std::slice::from_ref(&entry))
                    .await
                    .is_none()
                {
                    complete = false;
                    tracing::warn!(name = %entry, file = %tier.file(), "could not take a former master off a menu tier");
                }
            }
        }
        tracing::warn!(
            name = %entry,
            partial = !complete,
            "MASTER REMOVED - no longer in MASTER_NAMES; mods.txt, whitelists, rosters and menu tiers revoked"
        );
        complete
    }

    /// Every current master in every install's `mods.txt` and whitelist, and on every roster.
    pub async fn ensure_files(&self) {
        let masters = self.masters();
        for install in &self.parts.installs {
            for master in &masters {
                self.ensure_install_files(install, master).await;
            }
        }
        self.ensure_rosters(&masters).await;
    }

    async fn ensure_install_files(&self, install: &str, name: &str) {
        for path in [mods_path(install), whitelist_path(install)] {
            let entry = entry(Some(name));
            let result = self.parts.files.add(&path, &entry).await;
            if !result.ok {
                tracing::warn!(name = %entry, path = %result.path, error = result.error.as_deref().unwrap_or(""), "could not put a master in a file");
            } else if result.changed {
                tracing::info!(name = %entry, path = %result.path, "added a master");
            }
        }
    }

    async fn ensure_rosters(&self, names: &[String]) {
        let Some(rosters) = self.rosters() else {
            return;
        };
        let entries: Vec<String> = names
            .iter()
            .map(|n| entry(Some(n)))
            .filter(|e| !e.is_empty())
            .collect();
        let (added, failed) = rosters.ensure_on_every_roster(&entries).await;
        if added > 0 {
            tracing::info!(added, "added master names to the faction rosters");
        }
        if !failed.is_empty() {
            tracing::warn!(files = %failed.join(", "), "could not put the master names on some rosters");
        }
        // The full in-game menu: the owner tier.
        let owner = MenuTier::Owner.file();
        match rosters.add_names(&owner, &entries).await {
            Some(0) => {}
            Some(added) => {
                tracing::info!(added, file = %owner, "added master names to the owner menu")
            }
            None => {
                tracing::warn!(file = %owner, "could not put the master names on the owner menu")
            }
        }
    }

    /// A join: if it is a master on a numbered server, grant after the delay, once per window.
    pub fn on_joined(self: &Arc<Self>, join: &PlayerJoined) {
        let Some(name) = self.master_of(join) else {
            return;
        };
        let Some(number) = self.parts.servers.number_of(&join.file) else {
            tracing::warn!(%name, file = %join.file, "a master joined through a log that is not a numbered server - no grant sent");
            return;
        };
        let key = format!("{number}|{}", fold_case(&name));
        let now = self.parts.clock.now();
        {
            let mut last = self
                .last_grant
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            if last
                .get(&key)
                .is_some_and(|at| now - *at < REGRANT_COOLDOWN)
            {
                return;
            }
            last.insert(key, now);
        }
        let access = Arc::clone(self);
        let mut stopping = self.stopping.subscribe();
        self.track(async move {
            tokio::select! {
                () = tokio::time::sleep(access.parts.grant_delay) => {}
                _ = stopping.wait_for(|stopped| *stopped) => return, // shutting down: the next join grants
            }
            access.grant(number, &name).await;
        });
    }

    fn master_of(&self, join: &PlayerJoined) -> Option<String> {
        [join.name.as_deref(), Some(join.account_id.as_str())]
            .into_iter()
            .flatten()
            .filter(|c| !c.trim().is_empty())
            .find(|c| self.parts.masters.is_master(c))
            .map(|c| Sanitize::id(Some(c)))
            .filter(|id| !id.is_empty())
    }

    /// Put the master back in that server's files, the rosters and the owner menu tier.
    pub async fn grant(&self, number: usize, name: &str) {
        if let Some(install) = number
            .checked_sub(1)
            .and_then(|i| self.parts.installs.get(i))
        {
            self.ensure_install_files(install, name).await;
        }
        // Every master: the rosters and tiers are shared, and the join name is the safe form.
        self.ensure_rosters(&self.masters()).await;
        tracing::info!(%name, number, "master access re-checked on join");
    }
}
