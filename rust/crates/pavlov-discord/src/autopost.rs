//! A board that lives as ONE message and is edited in place (C# `AutoPost.cs`).
//!
//! Editing rather than re-posting is the whole point: a board that deletes and re-sends loses
//! its place in the channel and its reactions, and is simply gone in between.
//!
//! - TWO OVERLAPPING CALLS USED TO BOTH SEND: both read the stored id, found it empty and
//!   posted. Each board's posts are serialised, so the second call edits what the first stored.
//! - AN EDIT THAT FAILED IS NOT A DELETED MESSAGE. Only [`PostEdit::Missing`] earns a re-post;
//!   [`PostEdit::Failed`] waits a cycle. Guessing costs a duplicate that never goes away.
//! - Buttons are re-applied on EVERY edit: Discord strips components from an edit without them.

use std::collections::HashMap;
use std::fmt::Display;
use std::future::Future;
use std::sync::{Arc, Mutex, PoisonError};

use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_storage::datasets::AUTOPOST_STATE;

use crate::component::ActionRow;
use crate::post::{ChannelPoster, PostEdit};

type State = HashMap<String, u64>;

/// The autoposter.
pub struct AutoPost {
    target: Arc<dyn ChannelPoster>,
    store: Arc<SerializedStore>,
    /// One gate per board, so unrelated boards never wait on each other.
    gates: Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
}

impl std::fmt::Debug for AutoPost {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AutoPost").finish_non_exhaustive()
    }
}

impl AutoPost {
    #[must_use]
    pub fn new(target: Arc<dyn ChannelPoster>, store: Arc<SerializedStore>) -> Self {
        Self {
            target,
            store,
            gates: Mutex::new(HashMap::new()),
        }
    }

    fn gate(&self, key: &str) -> Arc<tokio::sync::Mutex<()>> {
        let mut gates = self.gates.lock().unwrap_or_else(PoisonError::into_inner);
        Arc::clone(gates.entry(key.to_owned()).or_default())
    }

    fn stored(&self, key: &str) -> Option<u64> {
        match self.store.read::<State>(AUTOPOST_STATE, State::new()) {
            Ok(state) => state.get(key).copied().filter(|id| *id != 0),
            Err(error) => {
                tracing::warn!(key, %error, "could not read the board state");
                None
            }
        }
    }

    /// Post or refresh board `key` in `channel`.
    ///
    /// `build` returning `Ok(None)` skips this cycle WITHOUT clearing the board already there
    /// (nothing to show yet); an `Err` leaves it alone too, since stale numbers beat none.
    /// `components` are the buttons to carry; empty for a plain board.
    pub async fn post<F, E>(
        &self,
        key: &str,
        channel: Option<u64>,
        build: F,
        components: Vec<ActionRow>,
    ) where
        F: Future<Output = Result<Option<Embed>, E>>,
        E: Display,
    {
        let Some(channel) = channel else {
            return;
        };
        let gate = self.gate(key);
        let _held = gate.lock().await;

        let embed = match build.await {
            Ok(Some(embed)) => embed,
            Ok(None) => return,
            Err(error) => {
                tracing::warn!(key, %error, "board build failed - leaving the existing post alone");
                return;
            }
        };

        let existing = self.stored(key);
        if let Some(message) = existing {
            match self
                .target
                .edit(channel, message, embed.clone(), components.clone())
                .await
            {
                PostEdit::Edited => return,
                PostEdit::Failed => {
                    tracing::warn!(
                        key,
                        "board could not be updated this cycle and has been left alone; \
                         re-posting it without knowing the original is gone would duplicate it"
                    );
                    return;
                }
                PostEdit::Missing => {}
            }
        }

        // Known gone, or never posted. A fresh one is the recovery.
        let posted = match self.target.send(channel, embed, components).await {
            Ok(Some(id)) => id,
            Ok(None) => {
                tracing::warn!(key, channel, "board could not be posted to its channel");
                return;
            }
            Err(error) => {
                tracing::warn!(key, channel, %error, "board could not be posted to its channel");
                return;
            }
        };
        let key_owned = key.to_owned();
        if let Err(error) = self
            .store
            .update(AUTOPOST_STATE, State::new, |mut state: State| {
                state.insert(key_owned, posted);
                Some(state)
            })
            .await
        {
            tracing::warn!(key, %error, "board posted but its id could not be saved");
        }
        if existing.is_some() {
            tracing::info!(key, "board was missing and has been re-posted");
        }
    }

    /// Forget a board's message id, so the next post creates a new one.
    ///
    /// # Errors
    /// When the state cannot be written.
    pub async fn forget(&self, key: &str) -> Result<(), pavlov_core::data::StoreError> {
        self.store
            .update(AUTOPOST_STATE, State::new, |mut state: State| {
                state.remove(key).map(|_| state)
            })
            .await
            .map(|_| ())
    }
}
