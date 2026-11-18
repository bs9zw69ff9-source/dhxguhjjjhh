//! `/serverswitch` (C# `ServerSwitchCommand.cs`): start, shut down or restart a game server.
//!
//! - The server is a CHOICE by index into the configured units, and the action is validated
//!   against the three actions offered: nothing typed reaches a privileged command line.
//! - The actions that drop players WARN FIRST and PAUSE, so the broadcast renders before the
//!   process it warns about disappears.
//! - The rosters are dropped afterwards: they describe a process that no longer exists.
//! - A stop SAYS it stays down: nothing brings a stopped server back on its own.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::moderation::AuditLog;
use pavlov_host::notice::{GRACE, NoticeResult, PlayerNotice, warning_for};
use pavlov_host::registry::RconRegistry;
use pavlov_host::service_control::{UnitAction, UnitControl, UnitResult};

use crate::access::{Access, RequiredAccess};
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// The most of systemd's answer shown.
const DETAIL_LIMIT: usize = 400;

/// The action values offered, in the C# enum's order.
const ACTIONS: [(i64, UnitAction, &str); 3] = [
    (2, UnitAction::Restart, "Restart"),
    (1, UnitAction::Stop, "Shut down"),
    (0, UnitAction::Start, "Start"),
];

/// The action a choice value names; anything not offered is `None`, never a fall-through.
#[must_use]
pub fn action_of(value: i64) -> Option<UnitAction> {
    ACTIONS
        .iter()
        .find(|(v, _, _)| *v == value)
        .map(|(_, a, _)| *a)
}

fn verb(action: UnitAction) -> &'static str {
    match action {
        UnitAction::Start => "Starting",
        UnitAction::Stop => "Shutting down",
        UnitAction::Restart => "Restarting",
    }
}

fn past(action: UnitAction) -> &'static str {
    match action {
        UnitAction::Start => "started",
        UnitAction::Stop => "stopped",
        UnitAction::Restart => "back up",
    }
}

fn truncate(text: &str) -> String {
    if text.chars().count() <= DETAIL_LIMIT {
        text.to_owned()
    } else {
        format!("{}\n…", text.chars().take(DETAIL_LIMIT).collect::<String>())
    }
}

/// The outcome.
#[must_use]
pub fn report(
    number: i32,
    unit: &str,
    action: UnitAction,
    result: &UnitResult,
    warned: &NoticeResult,
    advice: Option<&str>,
) -> Embed {
    let title = format!("Server {number} — {}", verb(action).to_lowercase());
    let mut embed = if result.ok {
        theme::success(
            &format!("{title} ✓"),
            Some(&format!(
                "`{}` is {}.",
                Sanitize::code(Some(unit)),
                past(action)
            )),
        )
    } else {
        theme::failure(
            &format!("{title} failed"),
            Some(&format!(
                "`{}` did not respond to `systemctl {}`.",
                Sanitize::code(Some(unit)),
                action.verb()
            )),
        )
    };
    if result.ok && action == UnitAction::Stop {
        embed = embed.field(
            format!("{} It will stay down", theme::WARN),
            "Nothing brings it back on its own — run `/serverswitch` again with **Start** when you want it up.",
            false,
        );
    }
    // Only where a warning was owed: "warned: No" beside a start reads as a failure.
    if warning_for(action).is_some() {
        let said = if warned.delivered {
            format!("Yes, {}s beforehand", GRACE.as_secs())
        } else {
            format!("No — {}", warned.detail)
        };
        embed = embed.field("Players warned", said, true);
    }
    if !result.ok {
        embed = embed.field(
            "systemd said",
            format!(
                "```\n{}\n```",
                Sanitize::code(Some(&truncate(&result.detail)))
            ),
            false,
        );
    }
    if let Some(advice) = advice {
        embed = embed.field(
            format!("{} How to fix this", theme::WARN),
            advice.to_owned(),
            false,
        );
    }
    embed
}

/// What the command is wired to.
pub struct ServerSwitchCommand {
    pub services: Arc<dyn UnitControl>,
    pub notice: Arc<PlayerNotice>,
    pub rcon: Arc<RconRegistry>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
    /// The pause between the warning and the action; [`GRACE`] in production.
    pub grace: std::time::Duration,
}

impl std::fmt::Debug for ServerSwitchCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServerSwitchCommand")
            .finish_non_exhaustive()
    }
}

impl ServerSwitchCommand {
    async fn run(&self, context: &CommandContext<'_>) -> Result<Embed, CommandError> {
        let invocation = context.invocation;
        let number = invocation
            .integer("server")
            .and_then(|n| i32::try_from(n).ok())
            .unwrap_or(0);
        let Some(action) = invocation.integer("action").and_then(action_of) else {
            return Ok(theme::failure(
                "Unknown action",
                Some("Pick one of the offered actions."),
            ));
        };
        let Some(unit) = self.services.unit_for(number).map(str::to_owned) else {
            return Ok(theme::failure(
                "No such server",
                Some(&format!(
                    "Only {} unit(s) are configured. Set `PAVLOV_UNITS` if that is wrong.",
                    self.services.units().len()
                )),
            ));
        };
        let who = &invocation.username;
        tracing::warn!(user = %who, number, unit = %unit, action = action.verb(), "SERVERSWITCH");
        let mut warned = NoticeResult {
            delivered: false,
            detail: "nobody to warn".into(),
        };
        if let Some(message) = warning_for(action) {
            warned = self.notice.warn(number, message).await;
            let said = if warned.delivered {
                format!("broadcast `{message}`")
            } else {
                format!("could not warn players: {}", warned.detail)
            };
            context
                .reply(Reply::embed(
                    theme::notice(
                        &format!("{} {} Server {number}", theme::WARN, verb(action)),
                        Some(&format!("`{unit}` — {said}. In {}s…", GRACE.as_secs())),
                    )
                    .brand(None),
                ))
                .await?;
            // THE PAUSE IS THE POINT: the broadcast must render before the process goes.
            tokio::time::sleep(self.grace).await;
        }
        let result = self.services.run(&unit, action).await;
        self.rcon.invalidate_rosters();
        let outcome = if result.ok {
            "ok".to_owned()
        } else {
            result.detail.clone()
        };
        self.audit
            .record(
                "serverswitch",
                who,
                &unit,
                Some(&format!("{} - {outcome}", verb(action).to_lowercase())),
            )
            .await?;
        let advice = self.services.advice(std::slice::from_ref(&result));
        Ok(report(
            number,
            &unit,
            action,
            &result,
            &warned,
            advice.as_deref(),
        ))
    }
}

impl SlashCommand for ServerSwitchCommand {
    fn name(&self) -> &str {
        "serverswitch"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        let server = self.services.units().iter().enumerate().fold(
            OptionSpec::new(OptionKind::Integer, "server", "Which server").required(),
            |option, (i, unit)| {
                let n = i64::try_from(i + 1).unwrap_or(i64::MAX);
                option.choice(Choice::integer(format!("Server {n} ({unit})"), n))
            },
        );
        let action = ACTIONS.iter().fold(
            OptionSpec::new(OptionKind::Integer, "action", "What to do").required(),
            |option, (value, _, label)| option.choice(Choice::integer(*label, *value)),
        );
        CommandSpec::new(
            "serverswitch",
            "Admin - Start, shut down or restart a game server",
        )
        .option(server)
        .option(action)
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if self.access.allows(RequiredAccess::Admin, caller) {
                self.run(&context).await?
            } else {
                theme::denied(
                    "Not allowed",
                    Some(&self.access.explain_refusal(caller, RequiredAccess::Admin)),
                )
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
