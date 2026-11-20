//! `/kick`, `/announce`, `/health` and `/help` (C# `UtilityCommands.cs`).

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::{Embed, palette};
use pavlov_core::skin::{police, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_host::health::{HealthRegistry, HealthReport, HealthStatus};
use pavlov_host::moderation::AuditLog;
use pavlov_host::moderation::bans::BanService;
use pavlov_host::registry::RconRegistry;
use pavlov_host::services::registry::{ServiceRegistry, ServiceState, ServiceStatus};

use crate::access::{Access, RequiredAccess};
use crate::catalog::CommandCatalog;
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Discord's limit on a field.
const MAX_FIELD: usize = 1024;
/// Room kept for the "and N more" line.
const MORE_ROOM: usize = 40;
/// The first line of an error shown per failure.
const ERROR_LINE: usize = 160;

fn refuse(
    access: &Access,
    context: &CommandContext<'_>,
    required: RequiredAccess,
) -> Option<Embed> {
    let caller = &context.invocation.caller;
    (!access.allows(required, caller)).then(|| {
        theme::denied(
            "Not allowed",
            Some(&access.explain_refusal(caller, required)),
        )
    })
}

// ---- /kick ----

/// `/kick`: by NAME, because Shack keys Kick on the display name; the account id is only
/// recorded. A kick is never a ban: a native ban here would be one nothing in the bot lifts.
pub struct KickCommand {
    pub rcon: Arc<RconRegistry>,
    pub bans: Arc<BanService>,
    pub audit: Arc<AuditLog>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for KickCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("KickCommand").finish_non_exhaustive()
    }
}

impl KickCommand {
    async fn run(&self, context: &CommandContext<'_>) -> Result<Embed, CommandError> {
        let invocation = context.invocation;
        let player = Sanitize::id(invocation.string("playerid"));
        let reason = Sanitize::message(invocation.string("reason"));
        // The live roster tells "not online" from "the kick failed".
        let online = self.rcon.servers().into_iter().find_map(|s| {
            self.rcon
                .roster(&s)
                .players
                .into_iter()
                .find(|p| eq_ignore_case(&p.name, &player))
        });
        let unique_id = online
            .as_ref()
            .map(|p| p.unique_id.as_str())
            .filter(|id| !id.is_empty());
        let result = self
            .bans
            .hard_enforce(&player, unique_id, false, true)
            .await;
        self.audit
            .record("kick", &invocation.username, &player, Some(&reason))
            .await?;
        tracing::info!(player = %player, by = %invocation.username, accepted = result.servers, reason = %reason, "kick");
        if result.landed() {
            let because = if reason.is_empty() {
                String::new()
            } else {
                format!(" — {}", Sanitize::code(Some(&reason)))
            };
            return Ok(theme::success(
                police().words.kicked,
                Some(&format!("**{}**{because}", Sanitize::code(Some(&player)))),
            )
            .field("By", invocation.username.clone(), true));
        }
        let hint = if unique_id.is_none() {
            "They may not be online."
        } else {
            ""
        };
        Ok(theme::failure(
            "Not kicked",
            Some(&format!("No server accepted the command. {hint}")),
        ))
    }
}

impl SlashCommand for KickCommand {
    fn name(&self) -> &str {
        "kick"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("kick", "Mod - Kick a player")
            .option(
                OptionSpec::new(OptionKind::String, "playerid", "Player ID or username")
                    .required()
                    .autocomplete(),
            )
            .option(OptionSpec::new(OptionKind::String, "reason", "Why"))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = match refuse(&self.access, &context, RequiredAccess::Mod) {
                Some(refusal) => refusal,
                None => self.run(&context).await?,
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /announce ----

/// The exact broadcast sent. `Notify All`: without the target the server takes the first
/// word as a player name, matches nobody and refuses.
#[must_use]
pub fn broadcast_command(message: &str) -> String {
    format!("Notify All {message}")
}

/// `/announce`. The message goes through `Sanitize::message`: the protocol is line-based, so
/// a newline would split one command into two, the second being whatever was typed.
pub struct AnnounceCommand {
    pub rcon: Arc<RconRegistry>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for AnnounceCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AnnounceCommand").finish_non_exhaustive()
    }
}

impl AnnounceCommand {
    async fn run(&self, context: &CommandContext<'_>) -> Embed {
        let message = Sanitize::message(context.invocation.string("message"));
        if message.is_empty() {
            return theme::failure("Empty message", None);
        }
        let command = broadcast_command(&message);
        let mut delivered = 0;
        for server in self.rcon.servers() {
            match self.rcon.send_verified(&server, &command).await {
                Ok(_) => delivered += 1,
                Err(error) => tracing::warn!(%server, %error, "announce failed"),
            }
        }
        tracing::info!(by = %context.invocation.username, servers = delivered, message = %message, "announce");
        if delivered > 0 {
            theme::success(
                "Broadcast sent",
                Some(&format!("> {}", Sanitize::code(Some(&message)))),
            )
            .field("Servers", delivered.to_string(), true)
        } else {
            theme::failure("Not broadcast", Some("No server accepted the command."))
        }
    }
}

impl SlashCommand for AnnounceCommand {
    fn name(&self) -> &str {
        "announce"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("announce", "Mod - Broadcast a message to the server")
            .option(OptionSpec::new(OptionKind::String, "message", "What to say").required())
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = match refuse(&self.access, &context, RequiredAccess::Mod) {
                Some(refusal) => refusal,
                None => self.run(&context).await,
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /health ----

fn status_word(status: HealthStatus) -> &'static str {
    match status {
        HealthStatus::Healthy => "healthy",
        HealthStatus::Degraded => "degraded",
        HealthStatus::Unhealthy => "unhealthy",
    }
}

fn glyph(status: HealthStatus) -> &'static str {
    match status {
        HealthStatus::Healthy => theme::OK,
        HealthStatus::Degraded => theme::WARN,
        HealthStatus::Unhealthy => theme::BAD,
    }
}

fn one_line(text: &str, max: usize) -> String {
    let flat = text
        .split(['\r', '\n'])
        .collect::<Vec<_>>()
        .join(" ")
        .trim()
        .to_owned();
    if flat.chars().count() <= max {
        flat
    } else {
        format!("{}…", flat.chars().take(max).collect::<String>())
    }
}

/// Every service failure, newest first, stopping BEFORE the field overflows: Discord rejects
/// an over-long field outright, which would lose the whole health reply.
#[must_use]
pub fn recent_errors(status: &[ServiceStatus]) -> String {
    let mut failures: Vec<(&str, i64, &str)> = status
        .iter()
        .flat_map(|s| {
            s.errors
                .iter()
                .map(move |e| (s.name.as_str(), e.at.timestamp(), e.message.as_str()))
        })
        .collect();
    failures.sort_by_key(|f| std::cmp::Reverse(f.1));
    let mut body = String::new();
    let mut shown = 0;
    for (name, at, message) in &failures {
        let line = format!("<t:{at}:R> `{name}` — {}\n", one_line(message, ERROR_LINE));
        if body.chars().count() + line.chars().count() > MAX_FIELD - MORE_ROOM {
            break;
        }
        body.push_str(&line);
        shown += 1;
    }
    if shown < failures.len() {
        body.push_str(&format!("*and {} more*", failures.len() - shown));
    }
    body
}

/// The health embed: the SAME registry the HTTP endpoint serves, plus the services.
#[must_use]
pub fn health_embed(
    report: &HealthReport,
    services: &[ServiceStatus],
    running: usize,
    build: &str,
) -> Embed {
    let colors = &police().colors;
    let color = match report.status {
        HealthStatus::Healthy => palette::GREEN,
        HealthStatus::Degraded => colors.amber,
        HealthStatus::Unhealthy => colors.ban_red,
    };
    let components: Vec<String> = report
        .components
        .values()
        .map(|c| {
            format!(
                "{} **{}** — {}",
                glyph(c.status),
                c.name,
                c.detail
                    .clone()
                    .unwrap_or_else(|| status_word(c.status).to_owned())
            )
        })
        .collect();
    let mut embed = Embed::new()
        .color(color)
        .title(format!("Health — {}", status_word(report.status)))
        .description(components.join("\n"));
    if !services.is_empty() {
        let mut sorted: Vec<&ServiceStatus> = services.iter().collect();
        sorted.sort_by(|a, b| a.name.cmp(&b.name));
        let lines: Vec<String> = sorted
            .iter()
            .map(|s| {
                let mark = if s.state == ServiceState::Running {
                    theme::UP
                } else {
                    theme::DOWN
                };
                let overran = if s.overruns > 0 {
                    format!(", {} overran", s.overruns)
                } else {
                    String::new()
                };
                format!(
                    "{mark} `{}` — {} ticks, {} failed{overran}",
                    s.name, s.ticks, s.failures
                )
            })
            .collect();
        embed = embed.field(
            format!("Services ({running}/{})", services.len()),
            lines.join("\n"),
            false,
        );
    }
    let errors = recent_errors(services);
    if !errors.is_empty() {
        embed = embed.field("Recent errors", errors, false);
    }
    embed.brand(Some(&format!("build {build}")))
}

/// `/health`.
pub struct HealthCommand {
    pub health: Arc<HealthRegistry>,
    pub services: Arc<ServiceRegistry>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for HealthCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HealthCommand").finish_non_exhaustive()
    }
}

impl SlashCommand for HealthCommand {
    fn name(&self) -> &str {
        "health"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("health", "Owner - Bot health and background services")
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = match refuse(&self.access, &context, RequiredAccess::Owner) {
                Some(refusal) => refusal.brand(None),
                None => {
                    let report = self.health.report().await;
                    health_embed(
                        &report,
                        &self.services.status(),
                        self.services.running(),
                        &pavlov_host::build::stamp(),
                    )
                }
            };
            context.reply(Reply::embed(embed)).await
        })
    }
}

// ---- /help ----

/// `/help`: EVERY command, not only those the caller may run. Hiding them makes the bot look
/// broken to somebody told a command exists, and the permission check still refuses them.
pub struct HelpCommand {
    pub catalog: Arc<CommandCatalog>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for HelpCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HelpCommand").finish_non_exhaustive()
    }
}

/// The help embed for a caller at `level`.
#[must_use]
pub fn help_embed(catalog: &CommandCatalog, level: &str) -> Embed {
    let lines: Vec<String> = catalog
        .commands()
        .iter()
        .map(|c| format!("`/{}`\n{}", c.name, c.description))
        .collect();
    let pages = theme::paginate(lines.iter().map(String::as_str), theme::MAX_DESCRIPTION);
    let footer = (pages.len() > 1).then(|| format!("Page 1 of {}", pages.len()));
    theme::notice(
        police().words.help_title,
        Some(&format!(
            "Your access: **{level}**\n\n{}",
            pages.first().map_or("", String::as_str)
        )),
    )
    .brand(footer.as_deref())
}

impl SlashCommand for HelpCommand {
    fn name(&self) -> &str {
        "help"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("help", "Show the available commands and your access level")
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let level = self.access.describe_access(&context.invocation.caller);
            context
                .reply(Reply::embed(help_embed(&self.catalog, level)))
                .await
        })
    }
}
