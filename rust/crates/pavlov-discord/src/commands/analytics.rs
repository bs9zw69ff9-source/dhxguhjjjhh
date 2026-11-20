//! The analytics commands (C# `AnalyticsCommands.cs`): `/staff activity`, a count of work done
//! per staff member over a window. A count, not a ranking of quality; automated actions are
//! excluded upstream.

use std::sync::Arc;

use chrono::TimeDelta;
use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_core::text::number::group_thousands;
use pavlov_core::time::Clock;
use pavlov_host::analytics::AnalyticsService;

use crate::access::{Access, RequiredAccess};
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// The rows a leaderboard shows.
const LEADERBOARD_ROWS: usize = 20;

/// The windows offered, as their labels and lengths.
pub const WINDOWS: [(&str, i64); 4] = [("24h", 1), ("7d", 7), ("30d", 30), ("90d", 90)];

/// The window a choice names; anything else is the default, 7 days.
#[must_use]
pub fn window_of(choice: Option<&str>) -> (&'static str, TimeDelta) {
    let (label, days) = WINDOWS
        .iter()
        .find(|(label, _)| choice.is_some_and(|c| eq_ignore_case(label, c)))
        .copied()
        .unwrap_or(("7d", 7));
    (label, TimeDelta::days(days))
}

/// The `window` option.
#[must_use]
pub fn window_option() -> OptionSpec {
    WINDOWS.iter().fold(
        OptionSpec::new(
            OptionKind::String,
            "window",
            "Period to report on (default 7d)",
        ),
        |option, (label, _)| option.choice(Choice::string(*label, *label)),
    )
}

/// The leaderboard for `rows`.
#[must_use]
pub fn leaderboard(label: &str, rows: &[(String, u64)]) -> Embed {
    let title = format!("Staff activity — last {label}");
    if rows.is_empty() {
        return theme::notice(&title, Some("No staff actions recorded."));
    }
    let lines: Vec<String> = rows
        .iter()
        .take(LEADERBOARD_ROWS)
        .enumerate()
        .map(|(i, (who, actions))| {
            format!(
                "`{:>2}.` **{}** — {} action(s)",
                i + 1,
                Sanitize::code(Some(who)),
                group_thousands(i64::try_from(*actions).unwrap_or(i64::MAX))
            )
        })
        .collect();
    theme::notice(&title, Some(&lines.join("\n"))).footer("Automated actions excluded")
}

/// `/staff`.
pub struct StaffStatsCommand {
    pub analytics: Arc<AnalyticsService>,
    pub access: Arc<Access>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for StaffStatsCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StaffStatsCommand").finish_non_exhaustive()
    }
}

impl SlashCommand for StaffStatsCommand {
    fn name(&self) -> &str {
        "staff"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "staff",
            "Admin - Staff activity over a period, busiest first",
        )
        .option(OptionSpec::sub("activity", "Everyone, busiest first").option(window_option()))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if self.access.allows(RequiredAccess::Admin, caller) {
                let (label, window) = window_of(context.invocation.string("window"));
                let rows = self
                    .analytics
                    .staff_leaderboard(window, self.clock.now())
                    .map_err(|e| CommandError(format!("events: {e}")))?;
                leaderboard(label, &rows)
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
