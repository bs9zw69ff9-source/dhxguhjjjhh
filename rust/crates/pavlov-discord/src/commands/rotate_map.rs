//! `/rotatemap` (C# `RotateMapCommand.cs`): send `RotateMap` over RCON, and nothing else.
//!
//! - AN RCON MAP CHANGE, NOT A RESTART: the process restart lives only in `/serverswitch`.
//! - No warning and no grace period: a map change drops nobody.
//! - Servers are chosen from fixed choices, so nothing a caller types reaches the wire.
//! - Rotated ONE AT A TIME: three map loads at once is three times the spike, and a failure on
//!   the first is visible before the rest go.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::moderation::AuditLog;
use pavlov_host::notice::{ROTATE_COMMAND, RotationOutcome, RotationResult, rotate};
use pavlov_host::registry::RconRegistry;
use pavlov_host::service_control::rcon_name_for;

use crate::access::{Access, RequiredAccess};
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// The most of a server's reply shown per field.
const DETAIL_LIMIT: usize = 400;

/// What the command is wired to.
pub struct RotateMapCommand {
    pub rcon: Arc<RconRegistry>,
    /// How many units are configured (`PAVLOV_UNITS`): the choices offered.
    pub unit_count: usize,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
}

impl std::fmt::Debug for RotateMapCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RotateMapCommand")
            .field("unit_count", &self.unit_count)
            .finish_non_exhaustive()
    }
}

/// The servers a choice names: `0` is all of them, an out-of-range number is none.
#[must_use]
pub fn targets(choice: i64, unit_count: usize) -> Vec<i32> {
    let count = i32::try_from(unit_count).unwrap_or(i32::MAX);
    if choice == 0 {
        return (1..=count).collect();
    }
    match i32::try_from(choice) {
        Ok(n) if (1..=count).contains(&n) => vec![n],
        _ => Vec::new(),
    }
}

fn truncate(text: &str) -> String {
    if text.chars().count() <= DETAIL_LIMIT {
        text.to_owned()
    } else {
        format!("{}\n…", text.chars().take(DETAIL_LIMIT).collect::<String>())
    }
}

/// The report: one field per server.
#[must_use]
pub fn report(results: &[RotationResult]) -> Embed {
    let ok = results.iter().filter(|r| r.ok()).count();
    let mut embed = if ok == results.len() {
        theme::success(&format!("Rotated {ok} server(s)"), None)
    } else {
        theme::failure(
            &format!("Rotated {ok} of {} server(s)", results.len()),
            None,
        )
    };
    for result in results {
        let mark = match result.outcome {
            RotationOutcome::Rotated => theme::OK,
            RotationOutcome::Unconfirmed => theme::WARN,
            RotationOutcome::Failed => theme::BAD,
        };
        embed = embed.field(
            format!("{mark} Server {}", result.number),
            Sanitize::message(Some(&truncate(&result.detail))),
            false,
        );
    }
    embed
}

impl RotateMapCommand {
    async fn run(&self, context: &CommandContext<'_>) -> Result<(), CommandError> {
        let choice = context.invocation.integer("server").unwrap_or(0);
        let targets = targets(choice, self.unit_count);
        if targets.is_empty() {
            let text = format!(
                "Only {} unit(s) are configured. Set `PAVLOV_UNITS` if that is wrong.",
                self.unit_count
            );
            return context
                .reply(Reply::embed(
                    theme::failure("No such server", Some(&text)).brand(None),
                ))
                .await;
        }
        let who = &context.invocation.username;
        let names: Vec<String> = targets.iter().map(|t| rcon_name_for(*t)).collect();
        tracing::warn!(user = %who, units = %names.join(", "), "ROTATEMAP");
        context
            .reply(Reply::embed(
                theme::notice(
                    &format!("Rotating {} server(s)", targets.len()),
                    Some(&format!("Sending `{ROTATE_COMMAND}`…")),
                )
                .brand(None),
            ))
            .await?;
        let mut results = Vec::with_capacity(targets.len());
        for target in &targets {
            results.push(rotate(&self.rcon, *target).await);
        }
        let detail = format!(
            "{}/{} rotated",
            results.iter().filter(|r| r.ok()).count(),
            results.len()
        );
        self.audit
            .record("rotatemap", who, &names.join(", "), Some(&detail))
            .await?;
        context
            .reply(Reply::embed(report(&results).brand(None)))
            .await
    }
}

impl SlashCommand for RotateMapCommand {
    fn name(&self) -> &str {
        "rotatemap"
    }

    fn build(&self) -> CommandSpec {
        let mut server = OptionSpec::new(
            OptionKind::Integer,
            "server",
            "Which server to rotate. Defaults to all of them.",
        )
        .choice(Choice::integer("All servers", 0));
        for i in 1..=i64::try_from(self.unit_count).unwrap_or(0) {
            server = server.choice(Choice::integer(format!("Server {i}"), i));
        }
        CommandSpec::new(
            "rotatemap",
            "Admin - Rotate the map on the game server(s) via RCON",
        )
        .option(server)
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            // ADMIN: this ends the round for every player on the server.
            let caller = &context.invocation.caller;
            if !self.access.allows(RequiredAccess::Admin, caller) {
                let refusal = self.access.explain_refusal(caller, RequiredAccess::Admin);
                return context
                    .reply(Reply::embed(
                        theme::denied("Not allowed", Some(&refusal)).brand(None),
                    ))
                    .await;
            }
            self.run(&context).await
        })
    }
}
