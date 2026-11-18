//! A guild directory that records direct messages and never reaches Discord.

use std::sync::Mutex;
use std::time::Duration;

use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_discord::guild_directory::{GuildDirectory, GuildInvite};
use pavlov_discord::interaction::DiscordError;

/// `delivers` is what Discord does with the DM: false is a closed inbox.
#[derive(Debug, Default)]
pub struct FakeDirectory {
    pub delivers: bool,
    pub attempted: Mutex<Vec<u64>>,
    pub invites: Vec<GuildInvite>,
}

impl FakeDirectory {
    pub fn delivering(delivers: bool) -> Self {
        Self {
            delivers,
            ..Self::default()
        }
    }

    pub fn attempted(&self) -> Vec<u64> {
        self.attempted.lock().unwrap().clone()
    }
}

impl GuildDirectory for FakeDirectory {
    fn invite_to_every_guild(&self, _: Duration, _: u32) -> BoxFuture<'_, Vec<GuildInvite>> {
        let invites = self.invites.clone();
        async move { invites }.boxed()
    }

    fn send_direct_message(
        &self,
        user_id: u64,
        _: Embed,
    ) -> BoxFuture<'_, Result<bool, DiscordError>> {
        self.attempted.lock().unwrap().push(user_id);
        let delivers = self.delivers;
        async move { Ok(delivers) }.boxed()
    }
}
