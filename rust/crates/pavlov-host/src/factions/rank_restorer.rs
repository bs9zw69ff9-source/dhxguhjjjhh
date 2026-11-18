//! Putting a suspended member back at their rank once the suspension is served (C#
//! `Factions/RankRestorer.cs`).
//!
//! RESTORED TO THE RECORDED RANK, not by a number of steps: a member re-whitelisted at Corporal
//! during the suspension used to be promoted Sergeant's worth of steps FROM Corporal. A member
//! already at or above the rank is left alone. A suspension is cleared once SETTLED, so a later
//! run cannot replay it; only a roster that is unavailable or unwritable is retried.

use std::sync::Arc;

use chrono::{DateTime, Utc};
use pavlov_core::data::SerializedStore;
use pavlov_core::factions::{FactionDefinition, MembershipDecision, MembershipOutcome};
use pavlov_core::text::case::{IgnoreCaseMap, eq_ignore_case};
use pavlov_storage::datasets::RANK_SUSPENSIONS;
use serde::{Deserialize, Serialize};

use super::RosterService;

/// A rank pulled for a set time. `restore_to` is where to put them back: without it a
/// suspension is just a demotion, and a sweep to the default rank would strip what they earned.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RankSuspension {
    pub player: String,
    pub faction: String,
    pub restore_to: String,
    pub suspended_by: String,
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub until: DateTime<Utc>,
}

/// The stored shape: player to suspension.
pub type Suspensions = IgnoreCaseMap<RankSuspension>;

/// The restorer.
#[derive(Debug)]
pub struct RankRestorer {
    store: Arc<SerializedStore>,
    rosters: Arc<RosterService>,
}

impl RankRestorer {
    #[must_use]
    pub fn new(store: Arc<SerializedStore>, rosters: Arc<RosterService>) -> Self {
        Self { store, rosters }
    }

    /// Settle every suspension that has run out by `now`. How many were settled.
    pub async fn restore_expired(&self, now: DateTime<Utc>) -> usize {
        let suspensions = match self.store.read(RANK_SUSPENSIONS, Suspensions::new()) {
            Ok(s) => s,
            Err(error) => {
                tracing::warn!(%error, "cannot read the rank suspensions - will retry");
                return 0;
            }
        };
        let due: Vec<RankSuspension> = suspensions
            .values()
            .filter(|s| s.until <= now)
            .cloned()
            .collect();

        let mut settled = 0;
        for suspension in due {
            if !self.restore(&suspension).await {
                continue;
            }
            if self.clear(&suspension).await {
                settled += 1;
            }
        }
        settled
    }

    /// True when the suspension is settled (restored, or nothing left to do).
    async fn restore(&self, s: &RankSuspension) -> bool {
        let Some(faction) = self.rosters.factions().get(Some(&s.faction)).cloned() else {
            tracing::warn!(player = %s.player, faction = %s.faction, "rank suspension for a faction that no longer exists - dropped");
            return true;
        };
        let membership = self.rosters.find(&s.player);
        if let Some(m) = membership
            .as_ref()
            .filter(|m| !eq_ignore_case(&m.faction, &faction.name))
        {
            tracing::warn!(
                player = %s.player,
                other = %m.faction,
                faction = %faction.name,
                "rank suspension served, but the player is in another faction now - NOT restored; move them back by hand if they should be"
            );
            return true;
        }

        let current = match membership {
            Some(m) => m.rank,
            None => {
                let joined = self.rosters.join(&faction, &s.player).await;
                if !joined.is_allowed() {
                    return settle(&joined, s);
                }
                joined.rank.unwrap_or_else(|| faction.default.clone())
            }
        };

        if at_or_above(&faction, &current, &s.restore_to) {
            tracing::info!(player = %s.player, rank = %current, "rank suspension served: already at that rank, left as is");
            return true;
        }
        let set = self
            .rosters
            .set_rank(&faction, &s.player, &s.restore_to)
            .await;
        if !set.is_allowed() {
            return settle(&set, s);
        }
        tracing::info!(player = %s.player, rank = %s.restore_to, "rank suspension served: restored");
        true
    }

    /// Remove the suspension, unless it was replaced meanwhile (a different `until`).
    async fn clear(&self, s: &RankSuspension) -> bool {
        let player = s.player.clone();
        let until = s.until;
        let result = self
            .store
            .update(
                RANK_SUSPENSIONS,
                Suspensions::new,
                move |mut current: Suspensions| {
                    if current
                        .get(&player)
                        .is_some_and(|stored| stored.until == until)
                    {
                        current.remove(&player);
                    }
                    Some(current)
                },
            )
            .await;
        match result {
            Ok(r) if r.ok => true,
            Ok(r) => {
                tracing::warn!(player = %s.player, error = r.error.as_deref().unwrap_or(""), "could not clear a served rank suspension - will retry");
                false
            }
            Err(error) => {
                tracing::warn!(player = %s.player, %error, "could not clear a served rank suspension - will retry");
                false
            }
        }
    }
}

/// `index_of(current) >= index_of(target)`, with an unknown rank counting as below everything
/// (the C# `-1`).
fn at_or_above(faction: &FactionDefinition, current: &str, target: &str) -> bool {
    let index = |rank: &str| {
        faction
            .index_of(Some(rank))
            .map_or(-1, |i| i64::try_from(i).unwrap_or(i64::MAX))
    };
    index(current) >= index(target)
}

/// Whether a refused change settles the suspension: an unavailable or unwritable roster is
/// retried; anything else needs a person.
fn settle(decision: &MembershipDecision, s: &RankSuspension) -> bool {
    if matches!(
        decision.outcome,
        MembershipOutcome::RosterUnavailable | MembershipOutcome::WriteFailed
    ) {
        tracing::warn!(player = %s.player, rank = %s.restore_to, outcome = ?decision.outcome, "could not restore the rank - will retry");
        return false;
    }
    tracing::warn!(player = %s.player, rank = %s.restore_to, outcome = ?decision.outcome, "could not restore the rank - needs a manual promotion");
    true
}
