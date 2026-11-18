//! Kills, deaths and suicides per player (C# `Stats/KillStats.cs`).
//!
//! Recorded into a buffer, which cannot fail on the log path, and flushed on a timer. A failed
//! flush PUTS THE COUNTS BACK rather than dropping them.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};

use chrono::{DateTime, Utc};
use pavlov_core::data::{SerializedStore, StoreError};
use pavlov_core::logs::KillEvent;
use pavlov_core::text::case::{IgnoreCaseMap, eq_ignore_case};
use pavlov_storage::datasets::KILL_STATS;
use serde::{Deserialize, Serialize};

/// One player's totals, in the stored shape.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PlayerKills {
    pub player: String,
    pub kills: i64,
    pub deaths: i64,
    pub suicides: i64,
    #[serde(with = "pavlov_core::json::epoch_ms_opt")]
    pub last_at: Option<DateTime<Utc>>,
}

impl PlayerKills {
    #[must_use]
    pub fn empty(player: &str) -> Self {
        Self {
            player: player.to_owned(),
            ..Self::default()
        }
    }

    /// Kills per death, a death-free player counted as one death.
    #[must_use]
    #[allow(clippy::cast_precision_loss)]
    pub fn ratio(&self) -> f64 {
        self.kills as f64 / self.deaths.max(1) as f64
    }

    fn plus(&self, delta: &Delta, fallback_name: &str) -> Self {
        Self {
            player: if self.player.is_empty() {
                fallback_name.to_owned()
            } else {
                self.player.clone()
            },
            kills: self.kills + delta.kills,
            deaths: self.deaths + delta.deaths,
            suicides: self.suicides + delta.suicides,
            last_at: match (delta.last_at, self.last_at) {
                (Some(d), Some(s)) => Some(d.max(s)),
                (d, s) => d.or(s),
            },
        }
    }
}

#[derive(Debug, Clone, Copy, Default)]
struct Delta {
    kills: i64,
    deaths: i64,
    suicides: i64,
    last_at: Option<DateTime<Utc>>,
}

impl Delta {
    fn merge(&mut self, other: &Delta) {
        self.kills += other.kills;
        self.deaths += other.deaths;
        self.suicides += other.suicides;
        self.last_at = match (self.last_at, other.last_at) {
            (Some(a), Some(b)) => Some(a.max(b)),
            (a, b) => a.or(b),
        };
    }
}

type Totals = IgnoreCaseMap<PlayerKills>;

/// The kill counter.
pub struct KillStats {
    store: Arc<SerializedStore>,
    pending: Mutex<HashMap<String, (String, Delta)>>,
}

impl std::fmt::Debug for KillStats {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("KillStats").finish_non_exhaustive()
    }
}

impl KillStats {
    #[must_use]
    pub fn new(store: Arc<SerializedStore>) -> Self {
        Self {
            store,
            pending: Mutex::new(HashMap::new()),
        }
    }

    fn stored(&self) -> Totals {
        self.store
            .read(KILL_STATS, Totals::default())
            .unwrap_or_default()
    }

    fn add(&self, player: &str, delta: Delta) {
        let mut pending = self.pending.lock().unwrap_or_else(PoisonError::into_inner);
        pending
            .entry(player.to_lowercase())
            .or_insert_with(|| (player.to_owned(), Delta::default()))
            .1
            .merge(&delta);
    }

    /// Buffer one kill. A world kill has no killer; a self-kill is a suicide and a death.
    pub fn record(&self, kill: &KillEvent, at: DateTime<Utc>) {
        let Some(killed) = kill.killed.as_deref().filter(|k| !k.is_empty()) else {
            return;
        };
        let killer = kill.killer.as_deref().filter(|k| !k.is_empty());
        let suicide = killer.is_some_and(|k| eq_ignore_case(k, killed));
        self.add(
            killed,
            Delta {
                deaths: 1,
                suicides: i64::from(suicide),
                last_at: Some(at),
                ..Delta::default()
            },
        );
        if let (false, Some(killer)) = (suicide, killer) {
            self.add(
                killer,
                Delta {
                    kills: 1,
                    last_at: Some(at),
                    ..Delta::default()
                },
            );
        }
    }

    /// One player's totals, stored plus buffered.
    #[must_use]
    pub fn of(&self, player: &str) -> PlayerKills {
        if player.trim().is_empty() {
            return PlayerKills::empty(player);
        }
        let stored = self
            .stored()
            .get(player)
            .cloned()
            .unwrap_or_else(|| PlayerKills::empty(player));
        let pending = self.pending.lock().unwrap_or_else(PoisonError::into_inner);
        match pending.get(&player.to_lowercase()) {
            Some((_, delta)) => stored.plus(delta, player),
            None => stored,
        }
    }

    /// Everybody, most kills first.
    #[must_use]
    pub fn all(&self) -> Vec<PlayerKills> {
        let mut totals = self.stored();
        let pending: Vec<(String, Delta)> = self
            .pending
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .values()
            .map(|(name, d)| (name.clone(), *d))
            .collect();
        for (name, delta) in pending {
            let current = totals
                .get(&name)
                .cloned()
                .unwrap_or_else(|| PlayerKills::empty(&name));
            totals.insert(name.clone(), current.plus(&delta, &name));
        }
        let mut all: Vec<PlayerKills> = totals.iter().map(|(_, p)| p.clone()).collect();
        all.sort_by_key(|p| std::cmp::Reverse(p.kills));
        all
    }

    /// Write the buffer. On failure the counts go back into the buffer for the next flush.
    ///
    /// # Errors
    /// When the store fails (the counts are kept).
    pub async fn flush(&self) -> Result<(), StoreError> {
        let taken: Vec<(String, Delta)> =
            std::mem::take(&mut *self.pending.lock().unwrap_or_else(PoisonError::into_inner))
                .into_values()
                .collect();
        if taken.is_empty() {
            return Ok(());
        }
        tracing::info!(
            players = taken.len(),
            kills = taken.iter().map(|t| t.1.kills).sum::<i64>(),
            deaths = taken.iter().map(|t| t.1.deaths).sum::<i64>(),
            "kill stats recorded"
        );
        let write = self
            .store
            .update(KILL_STATS, Totals::default, |mut totals: Totals| {
                for (player, delta) in &taken {
                    let current = totals
                        .get(player)
                        .cloned()
                        .unwrap_or_else(|| PlayerKills::empty(player));
                    totals.insert(player.clone(), current.plus(delta, player));
                }
                Some(totals)
            })
            .await;
        if let Err(error) = write {
            let held = taken.len();
            for (player, delta) in taken {
                self.add(&player, delta);
            }
            tracing::warn!(%error, held, "could not write kill stats - held for the next flush");
            return Err(error);
        }
        Ok(())
    }
}
