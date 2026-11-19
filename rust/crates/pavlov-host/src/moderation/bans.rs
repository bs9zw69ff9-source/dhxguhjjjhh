//! Issuing, enforcing and lifting bans (C# `Moderation/BanService.cs`).
//!
//! THE NAME IS THE TARGET. Shack's Ban, Kick and Unban act on the display name; the account
//! id is accepted on the wire and removes nobody. Every ban the bot issues, the sweep, the
//! reconcile, the ban file and every lift therefore name the same thing by construction. The
//! id is bookkeeping for evasion flags and evidence, and a fallback only when no name exists.

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use futures_util::future::{BoxFuture, join_all};
use pavlov_core::data::SerializedStore;
use pavlov_core::json::Stamp;
use pavlov_core::moderation::{BanRecord, BanRules};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::{IgnoreCaseMap, IgnoreCaseSet};
use pavlov_core::time::Clock;
use pavlov_rcon::RconError;
use pavlov_storage::datasets::{BAN_RECONCILE_STATE, TEMP_BANS, UNBAN_TOMBSTONES};
use serde::{Deserialize, Serialize};

use crate::registry::RconRegistry;

/// After a lift: long enough for anything lingering (a flag, a stale roster) to clear.
pub const LIFT_EXEMPTION: Duration = Duration::from_secs(60 * 60);
/// Native bans persist on the game server across bot restarts; re-issuing them all on every
/// start is only load.
pub const RECONCILE_MINIMUM_INTERVAL: TimeDelta = TimeDelta::minutes(30);
const RECONCILE_PACING: Duration = Duration::from_millis(400);

/// How many servers accepted, and the identifier actually sent.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EnforcementResult {
    pub servers: usize,
    pub target: Option<String>,
}

impl EnforcementResult {
    #[must_use]
    pub fn landed(&self) -> bool {
        self.servers > 0
    }
}

/// The names automation never touches (C# `IMasterNames`).
pub trait MasterNames: Send + Sync {
    fn is_master(&self, name: &str) -> bool;
    fn is_exempt(&self, name: &str) -> bool;
    fn is_protected(&self, name: &str) -> bool;
    fn exempt<'a>(&'a self, name: &'a str, duration: Option<Duration>) -> BoxFuture<'a, ()>;
}

/// Rewriting the game's ban file (C# `IBanFileExport`). `Err` is a failure to rewrite.
pub trait BanFileExport: Send + Sync {
    fn export(&self) -> BoxFuture<'_, Result<usize, String>>;
}

/// The evasion flags a ban created (C# `IBanEvidence`).
pub trait BanEvidence: Send + Sync {
    fn account_id_for(&self, name: &str) -> Option<String>;
    fn clear_flags<'a>(&'a self, account_id: &'a str) -> BoxFuture<'a, Result<(), String>>;
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct ReconcileState {
    #[serde(with = "pavlov_core::json::epoch_ms_opt")]
    last_run: Option<DateTime<Utc>>,
}

fn summarise(reply: &str) -> String {
    let text = reply.split_whitespace().collect::<Vec<_>>().join(" ");
    if text.is_empty() {
        "(no response)".into()
    } else {
        text.chars().take(140).collect()
    }
}

/// The ban service.
pub struct BanService {
    rcon: Arc<RconRegistry>,
    store: Arc<SerializedStore>,
    masters: Arc<dyn MasterNames>,
    clock: Arc<dyn Clock>,
    evidence: Option<Arc<dyn BanEvidence>>,
    ban_file: Option<Arc<dyn BanFileExport>>,
}

impl std::fmt::Debug for BanService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BanService").finish_non_exhaustive()
    }
}

impl BanService {
    #[must_use]
    pub fn new(
        rcon: Arc<RconRegistry>,
        store: Arc<SerializedStore>,
        masters: Arc<dyn MasterNames>,
        clock: Arc<dyn Clock>,
    ) -> Self {
        Self {
            rcon,
            store,
            masters,
            clock,
            evidence: None,
            ban_file: None,
        }
    }

    #[must_use]
    pub fn with_evidence(mut self, evidence: Arc<dyn BanEvidence>) -> Self {
        self.evidence = Some(evidence);
        self
    }

    #[must_use]
    pub fn with_ban_file(mut self, ban_file: Arc<dyn BanFileExport>) -> Self {
        self.ban_file = Some(ban_file);
        self
    }

    /// Valid records only. A corrupt entry enforces nothing either way; failing the whole
    /// list for it would unban everybody.
    #[must_use]
    pub fn load_bans(&self) -> Vec<BanRecord> {
        let all = match self.store.read::<Vec<BanRecord>>(TEMP_BANS, Vec::new()) {
            Ok(all) => all,
            Err(error) => {
                tracing::error!(%error, "could not read the ban list");
                return Vec::new();
            }
        };
        let total = all.len();
        let valid: Vec<BanRecord> = all.into_iter().filter(BanRecord::is_valid).collect();
        if valid.len() != total {
            tracing::error!(
                bad = total - valid.len(),
                total,
                "ban records are unusable and enforce nothing until repaired"
            );
        }
        valid
    }

    #[must_use]
    pub fn active_bans(&self) -> Vec<BanRecord> {
        let now = self.clock.now();
        self.load_bans()
            .into_iter()
            .filter(|b| b.is_active_at(now))
            .collect()
    }

    #[must_use]
    pub fn server_count(&self) -> usize {
        self.rcon.servers().len()
    }

    /// The sanitized RCON target, or `None` (logged) when nothing usable is left.
    fn target(name: &str, unique_id: Option<&str>, what: &str) -> Option<String> {
        let source = if name.is_empty() {
            unique_id.unwrap_or_default()
        } else {
            name
        };
        let target = Sanitize::id(Some(source));
        if target.is_empty() {
            tracing::error!(
                name,
                unique_id,
                "{what} has no usable RCON target: the identifier sanitized to empty"
            );
            return None;
        }
        if source != target {
            // Loud: the alternative is a ban that appears to succeed.
            tracing::warn!(source, target = %target, "RCON target rewritten (the sanitizer stripped characters) - if this does not land, the mismatch is why");
        }
        Some(target)
    }

    /// Ban and/or kick on every server at once. Only a VERIFIED ban counts as enforced: a
    /// refusal used to count as served, so a ban the server turned down was reported applied.
    pub async fn hard_enforce(
        &self,
        name: &str,
        unique_id: Option<&str>,
        ban: bool,
        kick: bool,
    ) -> EnforcementResult {
        let Some(target) = Self::target(name, unique_id, "enforce") else {
            return EnforcementResult {
                servers: 0,
                target: None,
            };
        };
        let mut verbs = Vec::new();
        if ban {
            verbs.push(format!("Ban {target}"));
        }
        if kick {
            verbs.push(format!("Kick {target}"));
        }
        if verbs.is_empty() {
            return EnforcementResult {
                servers: 0,
                target: Some(target),
            };
        }
        let accepted = AtomicUsize::new(0);
        let servers = self.rcon.servers();
        join_all(servers.iter().map(|server| {
            let (verbs, accepted) = (&verbs, &accepted);
            async move {
                let mut served = false;
                for command in verbs {
                    let is_ban = command.starts_with("Ban ");
                    // The kick is not verified: a refused kick means they are not on that server.
                    let reply = if is_ban {
                        self.rcon.send_verified(server, command).await
                    } else {
                        self.rcon.send(server, command).await
                    };
                    match reply {
                        Ok(reply) => {
                            if !ban || is_ban {
                                served = true;
                            }
                            tracing::info!(server, command, reply = %summarise(&reply), "sent");
                        }
                        Err(error) => tracing::warn!(server, command, %error, "FAILED"),
                    }
                }
                if served {
                    accepted.fetch_add(1, Ordering::SeqCst);
                }
            }
        }))
        .await;
        let accepted = accepted.into_inner();
        if accepted == 0 {
            tracing::error!(target = %target, total = servers.len(), "ENFORCE FAILED - accepted by no server; the player is NOT removed");
        }
        EnforcementResult {
            servers: accepted,
            target: Some(target),
        }
    }

    /// `Unban <name>` on every server, verified.
    pub async fn unban_everywhere(&self, name: &str, unique_id: Option<&str>) -> EnforcementResult {
        let Some(target) = Self::target(name, unique_id, "unban") else {
            return EnforcementResult {
                servers: 0,
                target: None,
            };
        };
        let mut accepted = 0;
        for server in self.rcon.servers() {
            match self
                .rcon
                .send_verified(&server, &format!("Unban {target}"))
                .await
            {
                Ok(_) => {
                    accepted += 1;
                    tracing::info!(target = %target, server, "unban accepted");
                }
                Err(error) => {
                    tracing::error!(target = %target, server, %error, "unban FAILED - they may still be natively banned there")
                }
            }
        }
        EnforcementResult {
            servers: accepted,
            target: Some(target),
        }
    }

    fn untouchable(&self, name: &str) -> bool {
        self.masters.is_master(name)
            || self.masters.is_protected(name)
            || self.masters.is_exempt(name)
    }

    /// Remove every online player who matches an active ban. The number removed.
    pub async fn enforce_sweep(&self) -> usize {
        let banned: IgnoreCaseSet = self
            .active_bans()
            .into_iter()
            .map(|b| b.player_id)
            .collect();
        let mut handled = IgnoreCaseSet::default();
        let mut removed = 0;
        for server in self.rcon.servers() {
            for player in self.rcon.roster(&server).players {
                let name = player.name.clone();
                if name.is_empty() || self.untouchable(&name) || !handled.insert(name.clone()) {
                    continue;
                }
                if !banned.contains(&name) {
                    continue;
                }
                let uid = (!player.unique_id.is_empty()).then_some(player.unique_id.as_str());
                tracing::warn!(name = %name, uid = uid.unwrap_or("unknown"), server, "ENFORCING - online but matches a ban record");
                let result = self.hard_enforce(&name, uid, true, true).await;
                tracing::info!(name = %name, servers = result.servers, landed = result.landed(), "sweep result");
                if result.landed() {
                    removed += 1;
                }
            }
        }
        removed
    }

    /// Re-issue every active ban to every server, at most every 30 minutes unless forced.
    /// The number of bans reconciled.
    pub async fn reconcile(&self, force: bool) -> usize {
        let now = self.clock.now();
        let state: ReconcileState = self
            .store
            .read(BAN_RECONCILE_STATE, ReconcileState::default())
            .unwrap_or_default();
        if let (false, Some(last)) = (force, state.last_run)
            && now - last < RECONCILE_MINIMUM_INTERVAL
        {
            tracing::info!(
                minutes_ago = (now - last).num_minutes(),
                "skipped ban reconcile - native bans persist on the server on their own"
            );
            return 0;
        }
        let servers = self.rcon.servers();
        let (mut reconciled, mut applied) = (0_usize, 0_usize);
        for ban in self.active_bans() {
            let name = Sanitize::id(Some(&ban.player_id));
            if name.is_empty() || self.untouchable(&ban.player_id) {
                continue;
            }
            // Re-verified at ISSUE time, not from the snapshot: a lift mid-run must win.
            let still = self
                .active_bans()
                .iter()
                .any(|b| BanRules::same_player(Some(&b.player_id), Some(&ban.player_id)));
            if !still {
                continue;
            }
            for server in &servers {
                // THE REPLY IS THE ANSWER: a refusal reaches nobody unless it is logged.
                match self
                    .rcon
                    .send_verified(server, &format!("Ban {name}"))
                    .await
                {
                    Ok(_) => applied += 1,
                    Err(RconError::Rejected { reply, .. }) => {
                        tracing::error!(name = %name, server, reply = %reply, "ban REFUSED - they are NOT banned there")
                    }
                    Err(error) => {
                        tracing::error!(name = %name, server, %error, "ban FAILED - they may NOT be banned there")
                    }
                }
            }
            reconciled += 1;
            tokio::time::sleep(RECONCILE_PACING).await;
        }
        if let Err(error) = self
            .store
            .write(
                BAN_RECONCILE_STATE,
                &ReconcileState {
                    last_run: Some(self.clock.now()),
                },
            )
            .await
        {
            tracing::warn!(%error, "could not record the reconcile time");
        }
        if reconciled > 0 {
            let attempts = reconciled * servers.len();
            tracing::info!(
                reconciled,
                servers = servers.len(),
                applied,
                attempts,
                "reconciled active bans"
            );
            if applied < attempts {
                tracing::warn!(
                    missed = attempts - applied,
                    "ban commands were refused or failed - those players are NOT banned"
                );
            }
        }
        reconciled
    }

    /// End a ban: the ONE path both /unban and expiry take, so they do the same four things -
    /// remove every record, clear the evasion flags, exempt briefly, and stop it coming back
    /// (a tombstone for the importer, a rewritten file for the game).
    pub async fn lift(&self, name: &str, unique_id: Option<&str>) -> EnforcementResult {
        if let Err(error) = self
            .store
            .update(TEMP_BANS, Vec::new, |mut bans: Vec<BanRecord>| {
                // EVERY record: a second one would re-catch them on the next sweep.
                let before = bans.len();
                bans.retain(|b| !BanRules::same_player(Some(&b.player_id), Some(name)));
                (bans.len() != before).then_some(bans)
            })
            .await
        {
            tracing::error!(name, %error, "could not remove the ban record");
        }

        let account = unique_id
            .map(str::to_owned)
            .or_else(|| self.evidence.as_ref().and_then(|e| e.account_id_for(name)));
        if let (Some(account), Some(evidence)) = (&account, &self.evidence) {
            /* LOUD, and it does not abort the lift: the native unban is what the player waits
            on. But surviving flags WILL re-ban them, so this line is the only warning. */
            if let Err(error) = evidence.clear_flags(account).await {
                tracing::error!(name, account, %error, "could not clear the evasion flags - they may be auto-banned again on their next connection; clear them by hand with /configure");
            }
        }

        self.masters.exempt(name, Some(LIFT_EXEMPTION)).await;

        let now = self.clock.now();
        if let Err(error) = self
            .store
            .update(
                UNBAN_TOMBSTONES,
                IgnoreCaseMap::default,
                |mut t: IgnoreCaseMap<Stamp>| {
                    t.insert(name.trim(), Stamp(now));
                    Some(t)
                },
            )
            .await
        {
            tracing::error!(name, %error, "could not record the unban tombstone - the importer may resurrect this ban");
        }

        let result = self.unban_everywhere(name, account.as_deref()).await;

        if let Some(ban_file) = &self.ban_file {
            /* NEVER fatal, and the tombstone is why that is safe: the record is gone and the
            unban was sent; the game may keep refusing them until the next sync. */
            if let Err(error) = ban_file.export().await {
                tracing::error!(name, %error, "lifted the ban but could not rewrite the game's ban file - the server may refuse them until the next banlist sync");
            }
        }
        result
    }

    /// Lift every expired temp ban through [`lift`](Self::lift). The bans lifted.
    pub async fn process_expired(&self) -> Vec<BanRecord> {
        let now = self.clock.now();
        let expired: Vec<BanRecord> = self
            .load_bans()
            .into_iter()
            .filter(|b| !b.permanent && b.expires.is_some_and(|e| e <= now))
            .collect();
        for ban in &expired {
            let result = self.lift(&ban.player_id, ban.unique_id.as_deref()).await;
            tracing::info!(player = %ban.player_id, servers = result.servers, "expired ban lifted");
        }
        expired
    }
}
