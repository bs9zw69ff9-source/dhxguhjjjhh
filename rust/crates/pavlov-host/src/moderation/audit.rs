//! The moderation audit trail (C# `Moderation/AuditLog.cs`), and the one funnel every staff
//! action goes through: the dataset, the timeline, the staff webhook and the staff channel.
//!
//! RECORDED FIRST, POSTED SECOND: the dataset is the record that has to survive, so a dead
//! webhook or channel can never lose an entry from it.

use std::sync::{Arc, Mutex, PoisonError};

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::data::{SerializedStore, StoreError};
use pavlov_core::events::{ServerEvent, mapping};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_core::time::Clock;
use pavlov_storage::datasets::MOD_LOG;
use serde::{Deserialize, Serialize};

use crate::feeds::FeedWebhooks;
use crate::timeline::Timeline;

/// The most entries kept; the oldest go first.
pub const MAX_ENTRIES: usize = 5000;

/// One staff action, in the stored shape the Node bot shares.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModAction {
    pub action: String,
    pub moderator: String,
    pub player: String,
    #[serde(default)]
    pub reason: Option<String>,
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub at: DateTime<Utc>,
}

/// A staff channel posted to by id (the Discord layer implements it).
pub trait StaffLogSink: Send + Sync {
    fn post(&self, action: ModAction) -> BoxFuture<'_, ()>;
}

/// The audit log.
pub struct AuditLog {
    store: Arc<SerializedStore>,
    clock: Arc<dyn Clock>,
    feeds: Option<Arc<FeedWebhooks>>,
    timeline: Option<Arc<dyn Timeline>>,
    sink: Mutex<Option<Arc<dyn StaffLogSink>>>,
}

impl std::fmt::Debug for AuditLog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AuditLog").finish_non_exhaustive()
    }
}

impl AuditLog {
    #[must_use]
    pub fn new(store: Arc<SerializedStore>, clock: Arc<dyn Clock>) -> Self {
        Self {
            store,
            clock,
            feeds: None,
            timeline: None,
            sink: Mutex::new(None),
        }
    }

    #[must_use]
    pub fn with_feeds(mut self, feeds: Arc<FeedWebhooks>) -> Self {
        self.feeds = Some(feeds);
        self
    }

    #[must_use]
    pub fn with_timeline(mut self, timeline: Arc<dyn Timeline>) -> Self {
        self.timeline = Some(timeline);
        self
    }

    /// Post every action to a staff channel too (set once the gateway is up).
    pub fn use_sink(&self, sink: Arc<dyn StaffLogSink>) {
        *self.sink.lock().unwrap_or_else(PoisonError::into_inner) = Some(sink);
    }

    /// Record one action.
    ///
    /// # Errors
    /// When the dataset cannot be written: the only failure that matters.
    pub async fn record(
        &self,
        action: &str,
        moderator: &str,
        player: &str,
        reason: Option<&str>,
    ) -> Result<(), StoreError> {
        let at = self.clock.now();
        // A reason cannot carry a newline into the record (or a forged second entry).
        let entry = ModAction {
            action: action.to_owned(),
            moderator: moderator.to_owned(),
            player: player.to_owned(),
            reason: Some(Sanitize::message(reason)),
            at,
        };
        let stored = entry.clone();
        self.store
            .update(MOD_LOG, Vec::new, move |mut log: Vec<ModAction>| {
                log.push(stored);
                if log.len() > MAX_ENTRIES {
                    let excess = log.len() - MAX_ENTRIES;
                    log.drain(..excess);
                }
                Some(log)
            })
            .await?;

        // The timeline, at the same funnel, so it is complete by construction. Cannot fail.
        if let Some(timeline) = &self.timeline {
            let mut event = ServerEvent::new(
                at,
                mapping::category_of(Some(action)),
                mapping::kind_of(Some(action)),
            );
            event.player = Some(player.to_owned());
            event.actor = Some(moderator.to_owned());
            event.detail = reason.map(str::to_owned);
            timeline.record(event);
        }
        // The staff webhook: queued, never fails.
        if let Some(feeds) = &self.feeds {
            feeds.post_staff(action, moderator, player, reason, at);
        }
        // And a channel by id: the record is written and cannot be un-written by this.
        let sink = self
            .sink
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone();
        if let Some(sink) = sink {
            sink.post(entry).await;
        }
        Ok(())
    }

    /// Everything, as stored (oldest first).
    #[must_use]
    pub fn all(&self) -> Vec<ModAction> {
        self.store.read(MOD_LOG, Vec::new()).unwrap_or_default()
    }

    /// Newest first. Entries in the same millisecond keep their write order, newest first.
    fn newest_first(actions: Vec<ModAction>) -> Vec<ModAction> {
        let mut actions: Vec<ModAction> = actions.into_iter().rev().collect();
        actions.sort_by_key(|a| std::cmp::Reverse(a.at));
        actions
    }

    /// What one staffer did, newest first, optionally since a moment.
    #[must_use]
    pub fn by(&self, moderator: &str, since: Option<DateTime<Utc>>) -> Vec<ModAction> {
        Self::newest_first(self.all())
            .into_iter()
            .filter(|a| eq_ignore_case(&a.moderator, moderator))
            .filter(|a| since.is_none_or(|s| a.at >= s))
            .collect()
    }

    /// Everything done to one player, newest first.
    #[must_use]
    pub fn against(&self, player: &str) -> Vec<ModAction> {
        Self::newest_first(self.all())
            .into_iter()
            .filter(|a| eq_ignore_case(&a.player, player))
            .collect()
    }
}
