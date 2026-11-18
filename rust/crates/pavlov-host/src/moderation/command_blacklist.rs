//! Users barred from every bot command (C# `Moderation/CommandBlacklist.cs`).
//!
//! Two sources: `BLACKLIST_IDS` in `.env`, and bars set from `/configure` (the `user_blacklist`
//! dataset, an ARRAY of id strings: the Node bot's format). An un-bar record (`user_unbarred`)
//! is the one way to lift a `.env` bar from Discord and outranks both.
//!
//! - An OWNER IS NEVER BARRED, by any source: every other gate lets an owner through, and a bar
//!   that could catch one would lock them out of their own bot.
//! - An un-bar record is written ONLY when `.env` is what bars them: written for a `/configure`
//!   bar, it would silently cancel a `.env` bar somebody adds later.
//! - Builds before 2026-07-31 kept address flags under `user_blacklist`. That object bars
//!   nobody, is not reported as unreadable (this runs on every interaction), and is replaced
//!   only once the flags have been moved to `ip_flags`.

use std::sync::Arc;

use pavlov_core::data::SerializedStore;
use pavlov_storage::datasets::{IP_FLAGS, USER_BLACKLIST, USER_UNBARRED};

/// How a bar or un-bar went.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BarOutcome {
    Barred,
    AlreadyBarred,
    /// Owners cannot be barred.
    Owner,
    Unbarred,
    NotBarred,
    Failed,
}

/// The outcome, with the reason for a failure.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BarResult {
    pub outcome: BarOutcome,
    pub error: Option<String>,
}

impl BarResult {
    fn of(outcome: BarOutcome) -> Self {
        Self {
            outcome,
            error: None,
        }
    }

    fn failed(error: String) -> Self {
        Self {
            outcome: BarOutcome::Failed,
            error: Some(error),
        }
    }
}

/// One barred user, and where the bar came from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BarredUser {
    pub id: String,
    pub from_environment: bool,
}

/// Whether a user id is an owner.
pub type IsOwner = Arc<dyn Fn(u64) -> bool + Send + Sync>;

/// The blacklist.
pub struct CommandBlacklist {
    store: Arc<SerializedStore>,
    is_owner: IsOwner,
    configured: Vec<String>,
}

impl std::fmt::Debug for CommandBlacklist {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CommandBlacklist")
            .field("configured", &self.configured.len())
            .finish_non_exhaustive()
    }
}

fn is_list(raw: Option<&str>) -> bool {
    raw.is_some_and(|r| r.trim_start().starts_with('['))
}

impl CommandBlacklist {
    #[must_use]
    pub fn new(store: Arc<SerializedStore>, is_owner: IsOwner, configured: &[u64]) -> Self {
        let mut ids: Vec<String> = configured.iter().map(u64::to_string).collect();
        ids.sort();
        ids.dedup();
        Self {
            store,
            is_owner,
            configured: ids,
        }
    }

    fn ids(&self, key: &str) -> Vec<String> {
        let raw = self.store.read_raw(key).ok().flatten();
        if is_list(raw.as_deref()) {
            self.store.read(key, Vec::new()).unwrap_or_default()
        } else {
            Vec::new()
        }
    }

    fn stored(&self) -> Vec<String> {
        self.ids(USER_BLACKLIST)
    }

    fn unbarred(&self) -> Vec<String> {
        self.ids(USER_UNBARRED)
    }

    fn owner_key(&self, id: &str) -> bool {
        id.parse::<u64>()
            .is_ok_and(|parsed| (self.is_owner)(parsed))
    }

    /// Whether this user's commands are refused. Never an owner.
    #[must_use]
    pub fn is_barred(&self, user_id: u64) -> bool {
        if (self.is_owner)(user_id) {
            return false;
        }
        let id = user_id.to_string();
        (self.configured.contains(&id) || self.stored().contains(&id))
            && !self.unbarred().contains(&id)
    }

    /// Everyone barred, `/configure` bars first, each once, owners and un-barred left out.
    #[must_use]
    pub fn entries(&self) -> Vec<BarredUser> {
        let unbarred = self.unbarred();
        let mut seen = std::collections::HashSet::new();
        self.stored()
            .into_iter()
            .map(|id| BarredUser {
                id,
                from_environment: false,
            })
            .chain(self.configured.iter().map(|id| BarredUser {
                id: id.clone(),
                from_environment: true,
            }))
            .filter(|u| !unbarred.contains(&u.id) && !self.owner_key(&u.id))
            .filter(|u| seen.insert(u.id.clone()))
            .collect()
    }

    /// Bar a user from `/configure`. Saved even when `.env` already names them, so tidying
    /// `.env` later does not quietly lift it.
    pub async fn bar(&self, user_id: u64) -> BarResult {
        if (self.is_owner)(user_id) {
            return BarResult::of(BarOutcome::Owner);
        }
        let id = user_id.to_string();
        let unbarred = self.unbarred().contains(&id);
        if !unbarred && self.stored().contains(&id) {
            return BarResult::of(BarOutcome::AlreadyBarred);
        }
        // THE STALE UN-BAR GOES FIRST: it outranks the list, so adding the id while it stood
        // would report success and bar nobody.
        if unbarred {
            if let Some(error) = self.change(USER_UNBARRED, |ids| remove(ids, &id)).await {
                return BarResult::failed(error);
            }
        }
        match self.change(USER_BLACKLIST, |ids| add(ids, &id)).await {
            None => BarResult::of(BarOutcome::Barred),
            Some(error) => BarResult::failed(error),
        }
    }

    /// Lift a bar. A `.env` bar is lifted by an un-bar record, which outranks `.env` for good.
    pub async fn unbar(&self, user_id: u64) -> BarResult {
        if !self.is_barred(user_id) {
            return BarResult::of(BarOutcome::NotBarred);
        }
        let id = user_id.to_string();
        let from_environment = self.configured.contains(&id);
        if from_environment {
            if let Some(error) = self.change(USER_UNBARRED, |ids| add(ids, &id)).await {
                return BarResult::failed(error);
            }
        }
        match self.change(USER_BLACKLIST, |ids| remove(ids, &id)).await {
            Some(error) if !from_environment => BarResult::failed(error),
            _ => BarResult::of(BarOutcome::Unbarred),
        }
    }

    /// Apply `change` to the id list under `key`. `None` on success (or nothing to change).
    async fn change(
        &self,
        key: &str,
        change: impl FnOnce(&mut Vec<String>) -> bool,
    ) -> Option<String> {
        let raw = self.store.read_raw(key).ok().flatten();
        if let Some(text) = raw.as_deref().filter(|r| !is_list(Some(r))) {
            let trimmed = text.trim();
            if !trimmed.is_empty() && trimmed != "null" {
                return self.replace_old_flags(key, change).await;
            }
        }
        match self
            .store
            .update(key, Vec::new, |mut ids: Vec<String>| {
                change(&mut ids).then_some(ids)
            })
            .await
        {
            Ok(r) if r.ok || r.is_vetoed() => None,
            Ok(r) => r.error,
            Err(error) => Some(error.to_string()),
        }
    }

    /// The old flag object under this key: replaced only once the flags live in `ip_flags`,
    /// or every banned address not yet moved would be unflagged.
    async fn replace_old_flags(
        &self,
        key: &str,
        change: impl FnOnce(&mut Vec<String>) -> bool,
    ) -> Option<String> {
        let moved = self
            .store
            .read_raw(IP_FLAGS)
            .ok()
            .flatten()
            .is_some_and(|r| !r.trim().is_empty());
        if !moved {
            return Some(format!(
                "\"{key}\" still holds address flags from an older build that have not been moved yet. \
                 Blacklist and then clear any address in /configure to move them, then try again."
            ));
        }
        let mut ids = Vec::new();
        change(&mut ids);
        match self.store.write(key, &ids).await {
            Ok(()) => None,
            Err(_) => Some(format!("\"{key}\" could not be written")),
        }
    }
}

fn add(ids: &mut Vec<String>, id: &str) -> bool {
    if ids.iter().any(|i| i == id) {
        return false;
    }
    ids.push(id.to_owned());
    true
}

fn remove(ids: &mut Vec<String>, id: &str) -> bool {
    let before = ids.len();
    ids.retain(|i| i != id);
    ids.len() < before
}
