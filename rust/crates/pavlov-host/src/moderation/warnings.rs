//! Warnings, and the automatic punishment that follows enough of them (C#
//! `Moderation/WarningService.cs`).
//!
//! WARNINGS DECAY BUT ARE NEVER DELETED. Escalation is judged on warnings inside a 30-day window;
//! the full history stays on record for staff to read. The decision and the evidence want
//! different retentions.
//!
//! ONE RULE, NOT A LADDER: three active warnings is a one-day ban. Nothing here ever issues a
//! permanent ban.
//!
//! THE PUNISHMENT IS NOT APPLIED HERE. This decides; the caller enforces, because enforcement
//! needs RCON, the ban list and the game's ban file.

use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::data::{SerializedStore, StoreError};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::{IgnoreCaseMap, eq_ignore_case};
use pavlov_core::time::Clock;
use pavlov_storage::datasets::WARNINGS;
use serde::{Deserialize, Serialize};

use super::AuditLog;

/// How long a warning counts toward escalation. Not a deletion.
pub const DECAY_WINDOW: TimeDelta = TimeDelta::days(30);
/// Active warnings that trigger the automatic ban.
pub const ESCALATION_THRESHOLD: usize = 3;
/// How long the automatic ban lasts.
pub const ESCALATION_BAN: TimeDelta = TimeDelta::days(1);

/// One warning. `id` is short, stable and shown, so one warning can be removed on its own.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerWarning {
    pub id: String,
    pub player: String,
    pub reason: String,
    pub moderator: String,
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub at: DateTime<Utc>,
}

/// What issuing a warning led to.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WarningOutcome {
    pub warning: PlayerWarning,
    /// Warnings inside the decay window: what escalation is judged on.
    pub active: usize,
    /// Every warning on record, decayed ones included.
    pub total: usize,
    /// This warning triggered the automatic ban.
    pub escalated: bool,
}

/// A player's line in the standings.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WarningStanding {
    pub player: String,
    pub active: usize,
    pub total: usize,
}

/// Why a warning could not be issued.
#[derive(Debug)]
pub enum WarningError {
    /// Nothing was left of the name after removing unsafe characters.
    NoPlayer,
    /// The dataset holds data that cannot be read, so it was not written over.
    Refused(String),
    Store(StoreError),
}

impl std::fmt::Display for WarningError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NoPlayer => f.write_str("no player name left after removing unsafe characters"),
            Self::Refused(reason) => f.write_str(reason),
            Self::Store(error) => error.fmt(f),
        }
    }
}

impl std::error::Error for WarningError {}

impl From<StoreError> for WarningError {
    fn from(error: StoreError) -> Self {
        Self::Store(error)
    }
}

/// The stored shape: player name to their warnings. A `null` entry (hand-edited) is skipped.
type Stored = IgnoreCaseMap<Option<Vec<Option<PlayerWarning>>>>;

fn entries(list: Option<&Option<Vec<Option<PlayerWarning>>>>) -> Vec<PlayerWarning> {
    list.and_then(Option::as_ref)
        .map(|l| l.iter().flatten().cloned().collect())
        .unwrap_or_default()
}

/// The service.
pub struct WarningService {
    store: Arc<SerializedStore>,
    audit: Arc<AuditLog>,
    clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for WarningService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WarningService").finish_non_exhaustive()
    }
}

impl WarningService {
    #[must_use]
    pub fn new(store: Arc<SerializedStore>, audit: Arc<AuditLog>, clock: Arc<dyn Clock>) -> Self {
        Self {
            store,
            audit,
            clock,
        }
    }

    fn load(&self) -> Stored {
        match self.store.read(WARNINGS, Stored::new()) {
            Ok(stored) => stored,
            Err(error) => {
                tracing::error!(%error, "cannot read the warnings");
                Stored::new()
            }
        }
    }

    fn cutoff(&self) -> DateTime<Utc> {
        self.clock.now() - DECAY_WINDOW
    }

    /// Every warning on record for a player, oldest first.
    #[must_use]
    pub fn for_player(&self, player: &str) -> Vec<PlayerWarning> {
        let mut list = entries(self.load().get(player.trim()));
        list.sort_by_key(|w| w.at);
        list
    }

    /// The warnings that still count toward escalation.
    #[must_use]
    pub fn active_for(&self, player: &str) -> Vec<PlayerWarning> {
        let cutoff = self.cutoff();
        self.for_player(player)
            .into_iter()
            .filter(|w| w.at > cutoff)
            .collect()
    }

    /// Record a warning and report what it triggered.
    ///
    /// The escalation is decided from the list AS WRITTEN, inside the same update, so two
    /// moderators warning at once cannot both read "2 active" and both decide nothing happens.
    ///
    /// # Errors
    /// An empty name after sanitising, or a store failure.
    pub async fn warn(
        &self,
        player: &str,
        reason: &str,
        moderator: &str,
    ) -> Result<WarningOutcome, WarningError> {
        let name = Sanitize::id(Some(player));
        if name.trim().is_empty() {
            return Err(WarningError::NoPlayer);
        }
        let now = self.clock.now();
        let warning = PlayerWarning {
            id: new_id(),
            player: name.clone(),
            reason: Sanitize::message(Some(reason)),
            moderator: Sanitize::message(Some(moderator)),
            at: now,
        };
        let cutoff = now - DECAY_WINDOW;
        let added = warning.clone();
        let key = name.clone();
        let result = self
            .store
            .update(WARNINGS, Stored::new, move |mut stored: Stored| {
                let mut list = entries(stored.get(&key));
                list.push(added);
                stored.insert(key, Some(list.into_iter().map(Some).collect()));
                Some(stored)
            })
            .await?;
        if !result.ok {
            // The C# ignored this and reported "0 active"; a refused write is not a warning.
            return Err(WarningError::Refused(result.error.unwrap_or_default()));
        }
        let written = entries(result.value.get(&name));
        let active = written.iter().filter(|w| w.at > cutoff).count();
        let total = written.len();

        // FIRES ON THE CROSSING, not on every warning past it: `>=` would re-ban for a day on
        // every later warning, a permanent ban assembled one day at a time.
        let escalated = active == ESCALATION_THRESHOLD;

        self.audit
            .record(
                "warn",
                moderator,
                &name,
                Some(&format!(
                    "{} (warning {active} of {ESCALATION_THRESHOLD})",
                    warning.reason
                )),
            )
            .await?;
        if escalated {
            tracing::warn!(
                player = %name,
                active,
                hours = ESCALATION_BAN.num_hours(),
                "player reached the warning threshold - escalating to a ban"
            );
        }
        Ok(WarningOutcome {
            warning,
            active,
            total,
            escalated,
        })
    }

    /// Remove ONE warning by id. False when that player has no warning with that id.
    ///
    /// # Errors
    /// A store failure.
    pub async fn remove(&self, player: &str, id: &str) -> Result<bool, StoreError> {
        let name = Sanitize::id(Some(player));
        let key = name.clone();
        let id_owned = id.to_owned();
        let result = self
            .store
            .update(WARNINGS, Stored::new, move |mut stored: Stored| {
                let list = entries(stored.get(&key));
                let kept: Vec<PlayerWarning> = list
                    .iter()
                    .filter(|w| !eq_ignore_case(&w.id, &id_owned))
                    .cloned()
                    .collect();
                if kept.len() == list.len() {
                    return None; // veto: nothing to write
                }
                if kept.is_empty() {
                    stored.remove(&key);
                } else {
                    stored.insert(key, Some(kept.into_iter().map(Some).collect()));
                }
                Some(stored)
            })
            .await?;
        if result.ok {
            self.audit
                .record(
                    "warn-remove",
                    "staff",
                    &name,
                    Some(&format!("warning {id}")),
                )
                .await?;
        }
        Ok(result.ok)
    }

    /// Clear a player's whole record. How many went.
    ///
    /// # Errors
    /// A store failure.
    pub async fn clear(&self, player: &str, moderator: &str) -> Result<usize, StoreError> {
        let name = Sanitize::id(Some(player));
        let key = name.clone();
        let cleared = Arc::new(AtomicU64::new(0));
        let count = Arc::clone(&cleared);
        self.store
            .update(WARNINGS, Stored::new, move |mut stored: Stored| {
                let n = stored
                    .get(&key)
                    .and_then(Option::as_ref)
                    .map_or(0, Vec::len);
                if n == 0 {
                    return None;
                }
                count.store(n as u64, Ordering::Relaxed);
                stored.remove(&key);
                Some(stored)
            })
            .await?;
        let cleared = usize::try_from(cleared.load(Ordering::Relaxed)).unwrap_or(usize::MAX);
        if cleared > 0 {
            self.audit
                .record(
                    "warn-clear",
                    moderator,
                    &name,
                    Some(&format!("{cleared} warning(s)")),
                )
                .await?;
        }
        Ok(cleared)
    }

    /// Players with at least one active warning, most first (then most on record).
    #[must_use]
    pub fn standings(&self) -> Vec<WarningStanding> {
        let cutoff = self.cutoff();
        let mut rows: Vec<WarningStanding> = self
            .load()
            .iter()
            .map(|(player, list)| {
                let list = entries(Some(list));
                WarningStanding {
                    player: player.to_owned(),
                    active: list.iter().filter(|w| w.at > cutoff).count(),
                    total: list.len(),
                }
            })
            .filter(|r| r.active > 0)
            .collect();
        rows.sort_by(|a, b| b.active.cmp(&a.active).then(b.total.cmp(&a.total)));
        rows
    }
}

/// Warnings still to go before the ban, or `None` once they are past it. Shown on the reply,
/// so the moderator can see they are one away before they press it.
#[must_use]
pub fn warnings_until_ban(active: usize) -> Option<usize> {
    (active < ESCALATION_THRESHOLD).then(|| ESCALATION_THRESHOLD - active)
}

/// Six hex characters: short enough to type back in a command, wide enough not to collide
/// within one player's record. Spread, not secrecy, so `RandomState` is enough.
/// Every `RandomState::new()` is keyed afresh, so each call hashes to a different value.
fn new_id() -> String {
    format!(
        "{:06x}",
        RandomState::new().build_hasher().finish() & 0x00FF_FFFF
    )
}
