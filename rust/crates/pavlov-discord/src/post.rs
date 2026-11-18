//! Posting to a channel outside an interaction (C# `IAutoPostTarget`): the command log, the
//! boards and the feeds all go through this, so the gateway adapter is the only part that
//! makes the REST call and the tests record what would have been sent.

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;

use crate::component::ActionRow;
use crate::interaction::DiscordError;

/// How an edit in place went.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PostEdit {
    /// Updated in place. Nothing else to do.
    Edited,
    /// The message is definitely gone. A fresh one is the recovery.
    Missing,
    /// Could not tell. Leave the existing post alone and try again next cycle.
    Failed,
}

/// Where channel posts go.
pub trait ChannelPoster: Send + Sync {
    /// Edit an existing message. `components` are the buttons to keep; empty leaves it plain.
    fn edit(
        &self,
        channel_id: u64,
        message_id: u64,
        embed: Embed,
        components: Vec<ActionRow>,
    ) -> BoxFuture<'_, PostEdit>;

    /// Post a new message and return its id; `Ok(None)` when the channel is unusable.
    fn send(
        &self,
        channel_id: u64,
        embed: Embed,
        components: Vec<ActionRow>,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>>;
}
