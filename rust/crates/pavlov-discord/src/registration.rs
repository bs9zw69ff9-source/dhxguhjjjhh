//! Registering the slash commands (C# `DiscordGateway.RegisterCommandsAsync`,
//! `ClearOtherScopeAsync`, `OnFactionReadyAsync` and the joined-guild handlers), over a
//! [`CommandRegistry`] seam so the scope rules are tested without Discord.
//!
//! The rules, each paid for by an outage:
//! - VALIDATED BEFORE SENDING. A bulk overwrite is rejected ATOMICALLY: one malformed command
//!   kept all sixty off the picker. The bad one is dropped and the rest register.
//! - A USER-INSTALLABLE APP REGISTERS GLOBALLY. Integration types exist only on global
//!   commands, so `USER_APP` overrides `GUILD_ID`.
//! - GUILD-SCOPED WHEREVER POSSIBLE. Global commands take up to an hour to propagate, which
//!   reads exactly like the deploy never ran.
//! - A GUILD THAT REFUSES SENDS THE WHOLE SET GLOBAL: slow beats missing.
//! - THE SCOPE NOT USED IS EMPTIED afterwards, both ways round, or every command shows twice.
//!   That cleanup runs OFF the ready handler: it is one REST call per guild, and holding the
//!   gateway's event loop on it once made every command time out.

use futures_util::future::BoxFuture;

use crate::command::{CommandSpec, Contexts, Installs, partition};
use crate::interaction::DiscordError;

/// The commands that move to the whitelist bot when it is on. Registering them on both
/// applications puts two identical entries in the picker, and whichever is clicked answers.
pub const FACTION_COMMANDS: [&str; 5] = [
    "whitelist",
    "promotion",
    "demotion",
    "subclass",
    "subclasses",
];

/// Whether `name` is one of [`FACTION_COMMANDS`] (ordinal, as the C#).
#[must_use]
pub fn is_faction_command(name: &str) -> bool {
    FACTION_COMMANDS.contains(&name)
}

/// Whether the global fallback may be emptied: only when the guild-scoped set is known to
/// cover everywhere, at least one guild took it and none refused (C# `ClearGlobals`).
#[must_use]
pub fn clear_globals(registered: usize, failed: usize) -> bool {
    registered > 0 && failed == 0
}

/// A guild the bot is in.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GuildRef {
    pub id: u64,
    pub name: String,
}

/// The application-command REST calls, for one application.
pub trait CommandRegistry: Send + Sync {
    /// The guilds the client is in (complete once the gateway is ready).
    fn guilds(&self) -> Vec<GuildRef>;
    fn overwrite_guild<'a>(
        &'a self,
        guild_id: u64,
        specs: &'a [CommandSpec],
    ) -> BoxFuture<'a, Result<(), DiscordError>>;
    fn overwrite_global<'a>(
        &'a self,
        specs: &'a [CommandSpec],
    ) -> BoxFuture<'a, Result<(), DiscordError>>;
    fn global_count(&self) -> BoxFuture<'_, Result<usize, DiscordError>>;
    fn guild_count(&self, guild_id: u64) -> BoxFuture<'_, Result<usize, DiscordError>>;
}

/// Which scope a registration used.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Scope {
    Global,
    Guild,
}

/// The settings registration reads.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct RegistrationSettings {
    pub user_app: bool,
    pub guild_id: Option<u64>,
    pub faction_bot_enabled: bool,
}

/// Drop malformed commands, logging each one, and keep the rest.
#[must_use]
pub fn validated(specs: Vec<CommandSpec>, which: &str) -> Vec<CommandSpec> {
    let (valid, rejected) = partition(specs);
    for problem in &rejected {
        tracing::error!(
            command = %problem.command,
            problem = %problem.problem,
            registered = valid.len(),
            "{which} COMMAND IS MALFORMED AND WAS NOT REGISTERED; the others registered normally"
        );
    }
    valid
}

/// The main application's set: everything, less the faction commands when the whitelist bot
/// carries them.
#[must_use]
pub fn main_set(built: Vec<CommandSpec>, faction_bot_enabled: bool) -> Vec<CommandSpec> {
    built
        .into_iter()
        .filter(|c| !faction_bot_enabled || !is_faction_command(&c.name))
        .collect()
}

/// The whitelist bot's set.
#[must_use]
pub fn faction_set(built: Vec<CommandSpec>) -> Vec<CommandSpec> {
    built
        .into_iter()
        .filter(|c| is_faction_command(&c.name))
        .collect()
}

/// Every install and every context, for `USER_APP`. Left wide rather than per command: a
/// context list that disagreed with a command's needs would be a second, silent gate.
#[must_use]
pub fn as_user_app(specs: Vec<CommandSpec>) -> Vec<CommandSpec> {
    specs
        .into_iter()
        .map(|mut c| {
            c.installs = Some(Installs {
                guild: true,
                user: true,
            });
            c.contexts = Some(Contexts {
                guild: true,
                bot_dm: true,
                private_channel: true,
            });
            c
        })
        .collect()
}

/// Register the main set. Returns the scope used, which decides the cleanup and whether a
/// newly joined guild needs its own copy.
///
/// # Errors
/// When the one call that matters fails (the global overwrite, or the configured guild's).
/// Per-guild failures without `GUILD_ID` are not errors: they send the set global.
pub async fn register_main(
    registry: &dyn CommandRegistry,
    specs: &[CommandSpec],
    settings: RegistrationSettings,
) -> Result<Scope, DiscordError> {
    if settings.user_app {
        registry.overwrite_global(specs).await?;
        tracing::info!(
            count = specs.len(),
            "registered as a USER-INSTALLABLE app (USER_APP is on): global, so propagation can \
             take up to an hour, and GUILD_ID is ignored. User Install must also be enabled in \
             the Discord Developer Portal"
        );
        tracing::info!(
            "outside a server, roles are read from the staff guild (HOME_GUILD_ID). Owners \
             work regardless: OWNER_IDS and SUPER_OWNER_IDS are user ids"
        );
        return Ok(Scope::Global);
    }

    let guilds = registry.guilds();
    if let Some(guild_id) = settings.guild_id {
        let Some(guild) = guilds.iter().find(|g| g.id == guild_id) else {
            tracing::warn!(
                guild_id,
                "GUILD_ID is not a guild this bot is in; falling back to global commands"
            );
            registry.overwrite_global(specs).await?;
            return Ok(Scope::Global);
        };
        registry.overwrite_guild(guild_id, specs).await?;
        tracing::info!(count = specs.len(), guild = %guild.name, "registered commands in guild");
        return Ok(Scope::Guild);
    }

    if !guilds.is_empty() {
        let mut failures = 0usize;
        for guild in &guilds {
            if let Err(error) = registry.overwrite_guild(guild.id, specs).await {
                failures += 1;
                tracing::error!(guild = %guild.name, guild_id = guild.id, %error,
                    "could not register commands in guild");
            }
        }
        if clear_globals(guilds.len() - failures, failures) {
            tracing::info!(
                count = specs.len(),
                guilds = guilds.len(),
                "registered commands in every guild; they appear immediately. Set GUILD_ID to \
                 pin registration to one server instead"
            );
            return Ok(Scope::Guild);
        }
        tracing::warn!(
            failures,
            guilds = guilds.len(),
            count = specs.len(),
            "guilds refused the registration, so the whole set is being registered GLOBALLY \
             instead: propagation can take up to an hour, but every server gets them"
        );
    }

    registry.overwrite_global(specs).await?;
    tracing::info!(
        count = specs.len(),
        "registered global commands; propagation can take up to an hour"
    );
    Ok(Scope::Global)
}

/// Empty whichever scope this run did NOT register into, and return how many leftover
/// commands were removed. NEVER FATAL: the commands that matter are live already, and a
/// cosmetic cleanup must not stop the bot. EVERY guild, not just `GUILD_ID`: the setting may
/// have changed since the registration that needs undoing.
pub async fn clear_other_scope(registry: &dyn CommandRegistry, registered: Scope) -> usize {
    match registered {
        Scope::Guild => match registry.global_count().await {
            Ok(0) => 0,
            Ok(stale) => match registry.overwrite_global(&[]).await {
                Ok(()) => {
                    tracing::warn!(
                        stale,
                        "removed leftover GLOBAL commands from an earlier run without GUILD_ID \
                         (every command was showing twice). One-off cleanup"
                    );
                    stale
                }
                Err(error) => {
                    warn_cleanup(&error);
                    0
                }
            },
            Err(error) => {
                warn_cleanup(&error);
                0
            }
        },
        Scope::Global => {
            let mut removed = 0;
            for guild in registry.guilds() {
                let stale = match registry.guild_count(guild.id).await {
                    Ok(stale) => stale,
                    Err(error) => {
                        warn_cleanup(&error);
                        return removed;
                    }
                };
                if stale == 0 {
                    continue;
                }
                if let Err(error) = registry.overwrite_guild(guild.id, &[]).await {
                    warn_cleanup(&error);
                    return removed;
                }
                tracing::warn!(stale, guild = %guild.name,
                    "removed leftover GUILD commands from an earlier GUILD_ID run (every \
                     command was showing twice). One-off cleanup");
                removed += stale;
            }
            removed
        }
    }
}

fn warn_cleanup(error: &DiscordError) {
    tracing::warn!(%error,
        "could not clear the leftover commands in the other scope; any duplicates in the \
         picker remain until the next start");
}

/// A guild joined while running gets the commands at once, unless the set is global (it is
/// there already) or `GUILD_ID` pins one server on purpose. Returns whether it registered.
pub async fn register_in_joined_guild(
    registry: &dyn CommandRegistry,
    guild: &GuildRef,
    specs: &[CommandSpec],
    scope: Scope,
    guild_id_configured: bool,
) -> bool {
    if scope == Scope::Global || guild_id_configured {
        return false;
    }
    match registry.overwrite_guild(guild.id, specs).await {
        Ok(()) => {
            tracing::info!(count = specs.len(), guild = %guild.name,
                "registered commands in a guild the bot just joined");
            true
        }
        Err(error) => {
            tracing::error!(guild = %guild.name, %error,
                "joined a guild but could not register commands there; restart to retry");
            false
        }
    }
}

/// What the whitelist bot's registration did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FactionRegistration {
    pub registered: usize,
    pub failed: usize,
    pub guilds: usize,
    /// The set went global (the bot is in no guild it can see).
    pub global: bool,
}

/// Register the whitelist bot's commands: guild-scoped in every guild, the global set
/// emptied only when every guild took it, and global only when there is no guild at all.
/// Failures are logged; a guild that refused keeps whatever it had.
pub async fn register_faction(
    registry: &dyn CommandRegistry,
    specs: &[CommandSpec],
) -> FactionRegistration {
    let guilds = registry.guilds();
    let mut registered = 0;
    let mut failed = 0;
    for guild in &guilds {
        match registry.overwrite_guild(guild.id, specs).await {
            Ok(()) => registered += 1,
            Err(error) => {
                failed += 1;
                tracing::error!(guild = %guild.name, guild_id = guild.id, %error,
                    "whitelist bot could not register its commands; that server keeps what it had");
            }
        }
    }
    let mut global = false;
    let result = if clear_globals(registered, failed) {
        registry.overwrite_global(&[]).await
    } else if registered == 0 {
        global = true;
        registry.overwrite_global(specs).await
    } else {
        Ok(())
    };
    if let Err(error) = result {
        tracing::error!(%error, "whitelist bot command registration failed");
    }
    tracing::info!(
        count = specs.len(),
        registered,
        guilds = guilds.len(),
        global,
        "whitelist bot registered its commands"
    );
    FactionRegistration {
        registered,
        failed,
        guilds: guilds.len(),
        global,
    }
}
