//! One Discord application's connection: build it, hand its REST and cache to the
//! composition root, wire the interactions in, run it, stop it.

use std::sync::Arc;
use std::sync::atomic::Ordering;

use pavlov_discord::guild_directory::GuildDirectory;
use pavlov_discord::home_guild::MemberSource;
use pavlov_discord::interaction::DiscordError;
use pavlov_discord::player_count::ChannelRenamer;
use pavlov_discord::post::ChannelPoster;
use pavlov_discord::runner::GuildInstalled;
use serenity::Client;
use serenity::all::{Cache, GatewayIntents, GuildId, Http, ShardManager};

use crate::handler::{Handler, HandlerState, Role, Wiring};
use crate::rest::{RestDirectory, RestMembers, RestPoster, RestRenamer};

/// The intents. Guilds only, as the C#: interactions need no intent, and the bot reads no
/// message content and no member list.
#[must_use]
pub fn intents() -> GatewayIntents {
    GatewayIntents::GUILDS
}

/// A built, not yet running, client.
pub struct DiscordClient {
    client: Client,
    state: Arc<HandlerState>,
}

impl std::fmt::Debug for DiscordClient {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DiscordClient").finish_non_exhaustive()
    }
}

/// Stops a running client from outside it.
#[derive(Clone)]
pub struct Shutdown(Arc<ShardManager>);

impl std::fmt::Debug for Shutdown {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Shutdown")
    }
}

impl Shutdown {
    pub async fn stop(&self) {
        self.0.shutdown_all().await;
    }
}

impl DiscordClient {
    /// Build the client. Connects nothing yet.
    ///
    /// # Errors
    /// When serenity refuses the token's shape.
    pub async fn build(token: &str, role: Role) -> Result<Self, DiscordError> {
        let state = Arc::new(HandlerState::default());
        let client = Client::builder(token, intents())
            .event_handler(Handler::new(role, Arc::clone(&state)))
            .await
            .map_err(|e| DiscordError(e.to_string()))?;
        Ok(Self { client, state })
    }

    /// The REST handle (shared with the gateway's rate limiter).
    #[must_use]
    pub fn http(&self) -> Arc<Http> {
        Arc::clone(&self.client.http)
    }

    /// The cache.
    #[must_use]
    pub fn cache(&self) -> Arc<Cache> {
        Arc::clone(&self.client.cache)
    }

    #[must_use]
    pub fn poster(&self) -> Arc<dyn ChannelPoster> {
        Arc::new(RestPoster::new(self.http()))
    }

    #[must_use]
    pub fn renamer(&self) -> Arc<dyn ChannelRenamer> {
        Arc::new(RestRenamer::new(self.http()))
    }

    #[must_use]
    pub fn directory(&self) -> Arc<dyn GuildDirectory> {
        Arc::new(RestDirectory::new(self.http(), self.cache()))
    }

    #[must_use]
    pub fn members(&self) -> Arc<dyn MemberSource> {
        Arc::new(RestMembers::new(self.http(), self.cache()))
    }

    /// Whether the bot is in a guild, from the cache (the user-app ephemeral rule).
    #[must_use]
    pub fn guild_installed(&self) -> GuildInstalled {
        let cache = self.cache();
        Arc::new(move |guild_id| cache.guild(GuildId::new(guild_id)).is_some())
    }

    /// Whether READY was handled and the commands registered.
    #[must_use]
    pub fn ready_flag(&self) -> Arc<dyn Fn() -> bool + Send + Sync> {
        let state = Arc::clone(&self.state);
        Arc::new(move || state.ready.load(Ordering::SeqCst))
    }

    /// Hand interactions to the runner and router. Set once; a second call is ignored and
    /// logged, since swapping the wiring of a live client would be a bug.
    pub fn wire(&self, wiring: Wiring) {
        if self.state.wiring.set(Arc::new(wiring)).is_err() {
            tracing::error!("the gateway was wired twice; the first wiring is kept");
        }
    }

    #[must_use]
    pub fn shutdown(&self) -> Shutdown {
        Shutdown(Arc::clone(&self.client.shard_manager))
    }

    /// Connect and run until stopped. Serenity reconnects on its own; this returns when the
    /// shards are shut down or the connection fails for good (a bad token, say).
    ///
    /// # Errors
    /// When the gateway cannot be reached or refuses the login.
    pub async fn run(mut self) -> Result<(), DiscordError> {
        self.client
            .start()
            .await
            .map_err(|e| DiscordError(e.to_string()))
    }
}
