//! The faction member index: which Discord account is which in-game name, in which faction (C#
//! `Factions/FactionMembers.cs`). The rosters hold in-game names only; this is what ties a
//! Discord user to one, and where the hold-ranks preference lives.

use std::sync::Arc;

use chrono::{DateTime, Utc};
use indexmap::IndexMap;
use pavlov_core::data::{SerializedStore, StoreError};
use pavlov_core::text::case::{IgnoreCaseSet, eq_ignore_case};
use pavlov_storage::datasets::FACTION_MEMBERS;
use serde::{Deserialize, Serialize};

/// One member, keyed in the store by Discord id (as a decimal string).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FactionMember {
    pub faction: String,
    pub name: String,
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub at: DateTime<Utc>,
    pub by: String,
    /// Keep every rank at or below their own (each rank file grants its gear).
    #[serde(default)]
    pub holds_all_ranks: bool,
}

/// The stored shape: Discord id to member, ordinal keys.
pub type MemberIndex = IndexMap<String, FactionMember>;

/// The index.
#[derive(Debug)]
pub struct FactionMembers {
    store: Arc<SerializedStore>,
}

impl FactionMembers {
    #[must_use]
    pub fn new(store: Arc<SerializedStore>) -> Self {
        Self { store }
    }

    fn load(&self) -> MemberIndex {
        self.store
            .read(FACTION_MEMBERS, MemberIndex::new())
            .unwrap_or_else(|error| {
                tracing::error!(%error, "cannot read the faction member index");
                MemberIndex::new()
            })
    }

    #[must_use]
    pub fn of(&self, discord_id: u64) -> Option<FactionMember> {
        self.load().get(&discord_id.to_string()).cloned()
    }

    /// Every member of a faction (ignoring case), by Discord id. Keys that are not ids are skipped.
    #[must_use]
    pub fn in_faction(&self, faction: &str) -> Vec<(u64, FactionMember)> {
        self.load()
            .into_iter()
            .filter(|(_, m)| eq_ignore_case(&m.faction, faction))
            .filter_map(|(k, m)| k.parse::<u64>().ok().map(|id| (id, m)))
            .collect()
    }

    /// Whether the member with this in-game name (ignoring case: the roster writer works in
    /// names read from a file) holds all their ranks.
    #[must_use]
    pub fn holds_all_ranks(&self, name: &str) -> bool {
        !name.is_empty()
            && self
                .load()
                .values()
                .find(|m| eq_ignore_case(&m.name, name))
                .is_some_and(|m| m.holds_all_ranks)
    }

    /// The Discord id that owns this in-game name, ignoring case.
    #[must_use]
    pub fn owner_of(&self, name: &str) -> Option<u64> {
        self.load()
            .iter()
            .find(|(_, m)| eq_ignore_case(&m.name, name))
            .and_then(|(k, _)| k.parse().ok())
    }

    /// Record (or replace) a member.
    ///
    /// # Errors
    /// A store failure.
    pub async fn remember(&self, discord_id: u64, member: FactionMember) -> Result<(), StoreError> {
        let key = discord_id.to_string();
        self.store
            .update(
                FACTION_MEMBERS,
                MemberIndex::new,
                move |mut index: MemberIndex| {
                    index.insert(key, member);
                    Some(index)
                },
            )
            .await
            .map(drop)
    }

    /// Forget one member.
    ///
    /// # Errors
    /// A store failure.
    pub async fn forget(&self, discord_id: u64) -> Result<(), StoreError> {
        let key = discord_id.to_string();
        self.store
            .update(
                FACTION_MEMBERS,
                MemberIndex::new,
                move |mut index: MemberIndex| index.shift_remove(&key).map(|_| index),
            )
            .await
            .map(drop)
    }

    /// Forget every member of a faction (a wipe). How many went.
    ///
    /// # Errors
    /// A store failure.
    pub async fn forget_faction(&self, faction: &str) -> Result<usize, StoreError> {
        self.retain(|m| !eq_ignore_case(&m.faction, faction)).await
    }

    /// Forget every member whose name is no longer on any roster. How many went.
    ///
    /// # Errors
    /// A store failure.
    pub async fn forget_missing(&self, still_whitelisted: &[String]) -> Result<usize, StoreError> {
        let mut live = IgnoreCaseSet::new();
        for name in still_whitelisted {
            live.insert(name.as_str());
        }
        self.retain(|m| live.contains(&m.name)).await
    }

    async fn retain(&self, keep: impl Fn(&FactionMember) -> bool) -> Result<usize, StoreError> {
        let mut dropped = 0;
        self.store
            .update(
                FACTION_MEMBERS,
                MemberIndex::new,
                |mut index: MemberIndex| {
                    let before = index.len();
                    index.retain(|_, m| keep(m));
                    dropped = before - index.len();
                    (dropped > 0).then_some(index)
                },
            )
            .await?;
        Ok(dropped)
    }
}
