//! The serenity event handler: registration on READY, joined guilds, and every interaction
//! handed to `pavlov-discord`'s runner, router and autocomplete. Nothing here decides
//! anything; it converts and forwards.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock, PoisonError};

use pavlov_discord::command::{Choice, CommandSpec};
use pavlov_discord::component_router::ComponentRouter;
use pavlov_discord::dispatcher::InteractionDispatcher;
use pavlov_discord::registration::{
    GuildRef, RegistrationSettings, Scope, clear_other_scope, register_faction,
    register_in_joined_guild, register_main,
};
use pavlov_discord::runner::CommandRunner;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::CommandBlacklist;
use serenity::all::{
    Cache, CommandInteraction, Context, EventHandler, Guild, Interaction, Ready, UnavailableGuild,
};
use serenity::async_trait;

use crate::convert;
use crate::respond::InteractionHandle;
use crate::rest::{KnownGuilds, RestRegistry};

/// Player and rank suggestions: `(focused, typed, faction)`.
pub type Suggest = Arc<dyn Fn(&str, &str, Option<&str>) -> Vec<Choice> + Send + Sync>;

/// Which application this client is, and so what it registers.
#[derive(Debug, Clone)]
pub enum Role {
    /// The main bot.
    Main {
        settings: RegistrationSettings,
        commands: Vec<CommandSpec>,
    },
    /// The whitelist bot: only the faction commands, guild-scoped everywhere it is.
    Faction { commands: Vec<CommandSpec> },
}

/// What interactions are handed to, set once the composition root has built it (after the
/// client exists, because the commands need the client's REST handle and cache).
pub struct Wiring {
    /// What this application registers.
    pub role: Role,
    pub runner: Arc<CommandRunner>,
    pub router: Arc<ComponentRouter>,
    pub suggest: Suggest,
    pub blacklist: Option<Arc<CommandBlacklist>>,
    pub metrics: Arc<MetricsRegistry>,
    pub dispatcher: Arc<InteractionDispatcher>,
}

impl std::fmt::Debug for Wiring {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Wiring").finish_non_exhaustive()
    }
}

/// The handler's shared state, also read by the client wrapper.
#[derive(Debug, Default)]
pub struct HandlerState {
    pub wiring: OnceLock<Arc<Wiring>>,
    pub cache: OnceLock<Arc<Cache>>,
    pub guilds: Arc<KnownGuilds>,
    /// READY handled and the commands registered.
    pub ready: AtomicBool,
    scope: Mutex<Option<Scope>>,
}

impl HandlerState {
    fn scope(&self) -> Option<Scope> {
        *self.scope.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn set_scope(&self, scope: Scope) {
        *self.scope.lock().unwrap_or_else(PoisonError::into_inner) = Some(scope);
    }
}

/// The handler. Its role and everything it forwards to arrive with the [`Wiring`].
pub struct Handler {
    state: Arc<HandlerState>,
}

impl std::fmt::Debug for Handler {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Handler").finish_non_exhaustive()
    }
}

impl Handler {
    #[must_use]
    pub fn new(state: Arc<HandlerState>) -> Self {
        Self { state }
    }

    fn role(&self) -> Option<&Role> {
        self.state.wiring.get().map(|w| &w.role)
    }

    fn guild_name(cache: &Cache, guild: &UnavailableGuild) -> String {
        cache
            .guild(guild.id)
            .map(|g| g.name.clone())
            .unwrap_or_else(|| guild.id.to_string())
    }

    async fn register(&self, ctx: &Context) {
        let registry = RestRegistry::new(Arc::clone(&ctx.http), Arc::clone(&self.state.guilds));
        let Some(role) = self.role() else {
            tracing::error!("READY arrived before the gateway was wired; nothing registered");
            return;
        };
        match role {
            Role::Main { settings, commands } => {
                match register_main(&registry, commands, *settings).await {
                    Ok(scope) => {
                        self.state.set_scope(scope);
                        // OFF THIS HANDLER: one REST call per guild, and holding READY on it
                        // once queued every interaction past Discord's three seconds.
                        let http = Arc::clone(&ctx.http);
                        let guilds = Arc::clone(&self.state.guilds);
                        let cleanup = async move {
                            let registry = RestRegistry::new(http, guilds);
                            clear_other_scope(&registry, scope).await;
                        };
                        match self.state.wiring.get() {
                            Some(wiring) => wiring.dispatcher.run("command scope cleanup", cleanup),
                            None => {
                                tokio::spawn(cleanup);
                            }
                        }
                    }
                    // Never fatal: the bot stays connected and /health says degraded.
                    Err(error) => tracing::error!(%error, "slash command registration failed"),
                }
            }
            Role::Faction { commands } => {
                register_faction(&registry, commands).await;
            }
        }
    }
}

#[async_trait]
impl EventHandler for Handler {
    async fn ready(&self, ctx: Context, ready: Ready) {
        let _ = self.state.cache.set(Arc::clone(&ctx.cache));
        self.state.guilds.replace(
            ready
                .guilds
                .iter()
                .map(|g| GuildRef {
                    id: g.id.get(),
                    name: Self::guild_name(&ctx.cache, g),
                })
                .collect(),
        );
        self.register(&ctx).await;
        self.state.ready.store(true, Ordering::SeqCst);
        tracing::info!(user = %ready.user.name, guilds = ready.guilds.len(), "logged in");
    }

    async fn guild_create(&self, ctx: Context, guild: Guild, is_new: Option<bool>) {
        let known = GuildRef {
            id: guild.id.get(),
            name: guild.name.clone(),
        };
        let added = self.state.guilds.upsert(known.clone());
        if is_new != Some(true) && !added {
            return;
        }
        if !self.state.ready.load(Ordering::SeqCst) {
            return; // READY's registration covers it.
        }
        let registry = RestRegistry::new(Arc::clone(&ctx.http), Arc::clone(&self.state.guilds));
        let Some(role) = self.role() else {
            return;
        };
        match role {
            Role::Main { settings, commands } => {
                let Some(scope) = self.state.scope() else {
                    return;
                };
                register_in_joined_guild(
                    &registry,
                    &known,
                    commands,
                    scope,
                    settings.guild_id.is_some(),
                )
                .await;
            }
            Role::Faction { commands } => {
                use pavlov_discord::registration::CommandRegistry;
                match registry.overwrite_guild(known.id, commands).await {
                    Ok(()) => tracing::info!(guild = %known.name,
                        "whitelist bot registered its commands in a guild it just joined"),
                    Err(error) => tracing::error!(guild = %known.name, %error,
                        "whitelist bot joined a guild but could not register there; restart to retry"),
                }
            }
        }
    }

    async fn interaction_create(&self, ctx: Context, interaction: Interaction) {
        let Some(wiring) = self.state.wiring.get().cloned() else {
            tracing::warn!("an interaction arrived before the bot finished starting; ignored");
            return;
        };
        match interaction {
            Interaction::Command(command) => slash(&ctx, wiring, command),
            Interaction::Autocomplete(command) => autocomplete(&ctx, &wiring, &command).await,
            Interaction::Component(component) => {
                let invocation = convert::component_invocation(&component);
                let handle = InteractionHandle::new(
                    Arc::clone(&ctx.http),
                    component.id,
                    component.token.clone(),
                );
                let router = Arc::clone(&wiring.router);
                wiring.dispatcher.run("component", async move {
                    router.route(&invocation, &handle).await;
                });
            }
            Interaction::Modal(modal) => {
                let invocation = convert::modal_invocation(&modal);
                let handle =
                    InteractionHandle::new(Arc::clone(&ctx.http), modal.id, modal.token.clone());
                let router = Arc::clone(&wiring.router);
                wiring.dispatcher.run("modal", async move {
                    router.route(&invocation, &handle).await;
                });
            }
            _ => {}
        }
    }
}

fn slash(ctx: &Context, wiring: Arc<Wiring>, command: CommandInteraction) {
    let guild_name = command
        .guild_id
        .and_then(|g| ctx.cache.guild(g).map(|g| g.name.clone()));
    let invocation = convert::invocation(&command, guild_name);
    let handle = InteractionHandle::new(Arc::clone(&ctx.http), command.id, command.token);
    let runner = Arc::clone(&wiring.runner);
    wiring.dispatcher.run("slash command", async move {
        runner.run(&invocation, &handle).await;
    });
}

/// Inline, not dispatched: an autocomplete has three seconds and reads only memory.
async fn autocomplete(ctx: &Context, wiring: &Wiring, command: &CommandInteraction) {
    let handle = InteractionHandle::new(Arc::clone(&ctx.http), command.id, command.token.clone());
    let Some(request) = convert::autocomplete_request(command) else {
        return;
    };
    let suggest = Arc::clone(&wiring.suggest);
    pavlov_discord::autocomplete::answer(
        &request,
        wiring.blacklist.as_deref(),
        &wiring.metrics,
        &handle,
        |focused, typed, faction| suggest(focused, typed, faction),
    )
    .await;
}
