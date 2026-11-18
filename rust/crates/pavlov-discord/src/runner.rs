//! Running one slash command, start to finish (C# `DiscordGateway.HandleSlashCommandAsync`).
//!
//! The order is the point, and each step says why it sits where it does:
//! 1. the command log, on ARRIVAL, so an attempt that goes nowhere is recorded too;
//! 2. the bar list, BEFORE the deferral, because the refusal must be private;
//! 3. an unknown command (a leftover from an older build) is told so;
//! 4. DEFER, before anything that can be slow: Discord's three seconds do not wait;
//! 5. prime access for a caller who arrived with no roles (a REST call, so after the defer);
//! 6. a concurrency slot, waited for AFTER the deferral, so a burst costs latency, not
//!    "the application did not respond";
//! 7. the handler, under a generous budget that stops a hang, not a slow restart;
//! 8. a failure is recorded where the owner can redeem its id, and the spinner is replaced.

use std::collections::HashMap;
use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::sync::Arc;
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_host::metrics::{MetricLabels, MetricsRegistry};
use pavlov_host::moderation::CommandBlacklist;
use pavlov_host::recent_errors::RecentErrors;
use tokio::sync::Semaphore;
use tracing::Instrument;

use crate::barred::{Asker, refuse_if_barred};
use crate::command_log::{CommandInvocation, CommandLog};
use crate::dispatcher::InteractionDispatcher;
use crate::interaction::{IntegrationOwners, Invocation, Reply, Responder};
use crate::slash::{CommandContext, SlashCommand};

/// How long one command may run before it is abandoned. Far longer than any command should
/// take, deliberately: `/serverswitch` legitimately spends minutes restarting servers, and
/// cutting that off halfway would leave a server down. A backstop against hanging FOREVER.
pub const COMMAND_BUDGET: Duration = Duration::from_secs(5 * 60);

/// Commands running at once. Past this they wait, after their deferral.
pub const MAX_CONCURRENT_COMMANDS: usize = 16;

/// Whether the interaction may only be answered privately: a user-installed app in a server it
/// has not been added to may speak ONLY ephemerally there, and a public deferral is rejected
/// as Discord's own "the application did not respond".
///
/// - A DM is never forced: it is private already.
/// - Owners, when present, decide: no guild install means not installed here.
/// - No owners (older payloads) asks whether the bot is in that guild. `guild_installed` is
///   called ONLY then, since it costs a cache lookup the common case does not need.
pub fn requires_ephemeral(
    guild_id: Option<u64>,
    owners: Option<&IntegrationOwners>,
    guild_installed: impl FnOnce() -> bool,
) -> bool {
    if guild_id.is_none() {
        return false;
    }
    match owners.filter(|o| !o.is_empty()) {
        Some(owners) => owners.guild_install.is_none(),
        None => !guild_installed(),
    }
}

/// Fetches a role-less caller's home-guild roles before the checks read them (C#
/// `HomeGuildMembers.PrimeAsync`). The gateway adapter implements it over REST.
pub trait AccessPrimer: Send + Sync {
    fn prime(&self, user_id: u64) -> BoxFuture<'_, ()>;
}

/// Whether the bot is a member of a guild (the gateway's cache).
pub type GuildInstalled = Arc<dyn Fn(u64) -> bool + Send + Sync>;

/// A new correlation id: eight hex digits, as the C# `NewCorrelationId` (a GUID's first
/// eight). Spread, not secrecy, so `RandomState` is enough.
#[must_use]
pub fn new_correlation_id() -> String {
    format!(
        "{:08x}",
        RandomState::new().build_hasher().finish() & 0xFFFF_FFFF
    )
}

/// What the runner is built from.
pub struct RunnerParts {
    pub commands: Vec<Arc<dyn SlashCommand>>,
    pub blacklist: Option<Arc<CommandBlacklist>>,
    pub command_log: Option<Arc<CommandLog>>,
    pub dispatcher: Arc<InteractionDispatcher>,
    pub metrics: Arc<MetricsRegistry>,
    pub errors: Arc<RecentErrors>,
    pub primer: Option<Arc<dyn AccessPrimer>>,
    pub guild_installed: GuildInstalled,
    pub budget: Duration,
}

impl std::fmt::Debug for RunnerParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RunnerParts")
            .field("commands", &self.commands.len())
            .finish_non_exhaustive()
    }
}

/// The runner.
pub struct CommandRunner {
    commands: HashMap<String, Arc<dyn SlashCommand>>,
    parts: RunnerParts,
    slots: Semaphore,
}

impl std::fmt::Debug for CommandRunner {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CommandRunner")
            .field("commands", &self.commands.len())
            .finish_non_exhaustive()
    }
}

/// How a run ended, for the caller's log and the tests.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RunOutcome {
    Barred,
    Unknown,
    /// The acknowledgement itself failed: nothing more can be said to the caller.
    NotAcknowledged,
    Completed,
    TimedOut {
        correlation_id: String,
    },
    Failed {
        correlation_id: String,
    },
}

impl CommandRunner {
    #[must_use]
    pub fn new(mut parts: RunnerParts) -> Self {
        let commands = std::mem::take(&mut parts.commands)
            .into_iter()
            .map(|c| (c.name().to_owned(), c))
            .collect();
        Self {
            commands,
            parts,
            slots: Semaphore::new(MAX_CONCURRENT_COMMANDS),
        }
    }

    /// The commands this runner will run, after `COMMANDS_DISABLED`.
    #[must_use]
    pub fn command_names(&self) -> Vec<&str> {
        let mut names: Vec<&str> = self.commands.keys().map(String::as_str).collect();
        names.sort_unstable();
        names
    }

    /// Run one command. Never fails: every failure is answered and logged here.
    pub async fn run(&self, invocation: &Invocation, responder: &dyn Responder) -> RunOutcome {
        let correlation_id = new_correlation_id();
        // ONE SCOPE, SET ONCE: every line this command causes, however deep, carries the
        // command, the guild, the caller and the correlation id.
        let span = tracing::info_span!(
            "interaction",
            operation = %format!("/{}", invocation.command),
            correlation = %correlation_id,
            guild = ?invocation.guild_id,
            user = invocation.caller.id
        );
        self.run_in_scope(invocation, responder, correlation_id)
            .instrument(span)
            .await
    }

    async fn run_in_scope(
        &self,
        invocation: &Invocation,
        responder: &dyn Responder,
        correlation_id: String,
    ) -> RunOutcome {
        let name = invocation.command.as_str();

        if let Some(log) = &self.parts.command_log {
            let captured = CommandInvocation::capture(invocation, chrono::Utc::now());
            let log = Arc::clone(log);
            self.parts
                .dispatcher
                .run("command log", async move { log.post(&captured).await });
        }

        if let Some(blacklist) = &self.parts.blacklist {
            let what = format!("/{name}");
            if refuse_if_barred(
                blacklist,
                invocation.caller.id,
                &invocation.username,
                Asker::Interaction(responder),
                &what,
                &self.parts.metrics,
            )
            .await
            {
                return RunOutcome::Barred;
            }
        }

        let Some(command) = self.commands.get(name) else {
            tracing::warn!(name, "no handler for this command");
            if let Err(error) = responder
                .respond(Reply::text("That command is no longer available."), true)
                .await
            {
                tracing::warn!(%error, "could not answer an unknown command");
            }
            return RunOutcome::Unknown;
        };

        let ephemeral = command.ephemeral()
            || requires_ephemeral(invocation.guild_id, Some(&invocation.owners), || {
                invocation
                    .guild_id
                    .is_some_and(|g| (self.parts.guild_installed)(g))
            });
        if let Err(error) = responder.defer(ephemeral).await {
            tracing::warn!(name, %error, "could not acknowledge in time");
            return RunOutcome::NotAcknowledged;
        }

        let work = async {
            self.prime(invocation).await;
            let Ok(_slot) = self.slots.acquire().await else {
                return Ok(());
            };
            let labels = MetricLabels::of("command", name);
            self.parts
                .metrics
                .time(
                    "command_duration_ms",
                    &labels,
                    Some("Slash command duration in milliseconds"),
                    command.handle(CommandContext {
                        invocation,
                        responder,
                    }),
                )
                .await
        };

        match tokio::time::timeout(self.parts.budget, work).await {
            Ok(Ok(())) => RunOutcome::Completed,
            Err(_) => {
                tracing::error!(
                    name,
                    budget_seconds = self.parts.budget.as_secs(),
                    "exceeded its budget and was abandoned"
                );
                self.count_error(name, None);
                self.tell(
                    responder,
                    name,
                    format!(
                        "That took too long and was stopped (`{correlation_id}`). \
                         Nothing further will happen - check `/health`."
                    ),
                )
                .await;
                RunOutcome::TimedOut { correlation_id }
            }
            Ok(Err(error)) => {
                tracing::error!(name, %error, "command failed");
                self.count_error(name, Some("Slash commands that threw"));
                self.parts.errors.record(
                    &correlation_id,
                    &format!("/{name}"),
                    invocation.caller.id,
                    &error,
                );
                self.tell(
                    responder,
                    name,
                    format!("That didn't work. The error has been logged (`{correlation_id}`)."),
                )
                .await;
                RunOutcome::Failed { correlation_id }
            }
        }
    }

    /// Prime only a caller with no member, or a member with no roles: a user-installed app
    /// in a guild is handed a role-less member and would otherwise read as PUBLIC.
    async fn prime(&self, invocation: &Invocation) {
        let Some(primer) = &self.parts.primer else {
            return;
        };
        let has_roles = invocation
            .caller
            .member
            .as_ref()
            .is_some_and(|m| !m.role_ids.is_empty());
        if !has_roles {
            primer.prime(invocation.caller.id).await;
        }
    }

    fn count_error(&self, name: &str, help: Option<&str>) {
        self.parts.metrics.increment(
            "command_errors_total",
            &MetricLabels::of("command", name),
            1.0,
            help,
        );
    }

    /// Replace the spinner. A failure to do so is logged: a spinner nobody could replace
    /// looks exactly like the bot hanging.
    async fn tell(&self, responder: &dyn Responder, name: &str, text: String) {
        if let Err(error) = responder.edit_original(Reply::text(text)).await {
            tracing::warn!(name, %error, "could not tell the caller what happened");
        }
    }
}
