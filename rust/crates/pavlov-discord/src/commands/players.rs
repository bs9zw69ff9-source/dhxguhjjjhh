//! `/players` (C# `PlayersCommand.cs`): who is online right now, by name.
//!
//! - A roster that has NEVER BEEN FETCHED is not an empty server: it says "no roster yet".
//! - A stale roster is still shown, with how old it is, so an hour-old list never reads as live.
//! - Paged, because a full server's roster can outgrow an embed.

use std::sync::Arc;

use chrono::TimeDelta;
use futures_util::future::BoxFuture;
use pavlov_core::skin::{lore, police, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::time::Clock;
use pavlov_host::registry::RconRegistry;

use crate::command::CommandSpec;
use crate::paged::Paged;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// What the command is wired to.
pub struct PlayersCommand {
    pub rcon: Arc<RconRegistry>,
    pub paged: Arc<Paged>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for PlayersCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PlayersCommand").finish_non_exhaustive()
    }
}

/// "4 minutes", "2 hours": enough to judge whether the list is worth believing.
#[must_use]
pub fn describe(age: TimeDelta) -> String {
    #[allow(clippy::cast_precision_loss)]
    let seconds = age.num_milliseconds() as f64 / 1000.0;
    if seconds < 90.0 {
        format!("{seconds:.0} seconds")
    } else if seconds < 90.0 * 60.0 {
        format!("{:.0} minutes", seconds / 60.0)
    } else if seconds < 36.0 * 3600.0 {
        format!("{:.0} hours", seconds / 3600.0)
    } else {
        format!("{:.0} days", seconds / 86_400.0)
    }
}

impl PlayersCommand {
    /// The sections and the title, one section per server.
    #[must_use]
    pub fn sections(&self) -> (String, Vec<String>) {
        let words = &police().words;
        let now = self.clock.now();
        let (mut sections, mut total, mut reported, mut any_stale) = (Vec::new(), 0i64, 0, false);
        for server in self.rcon.servers() {
            let roster = self.rcon.roster(&server);
            let label = Sanitize::message(Some(&server));
            let Some(taken_at) = roster.taken_at else {
                let why = self
                    .rcon
                    .roster_problem(&server)
                    .map(|w| format!("\n{} {}", theme::BAD, Sanitize::message(Some(&w))))
                    .unwrap_or_default();
                sections.push(format!("**{label}** — *no roster yet*{why}"));
                continue;
            };
            reported += 1;
            total += i64::try_from(roster.players.len()).unwrap_or(i64::MAX);
            let mut names: Vec<&str> = roster
                .players
                .iter()
                .map(|p| p.name.as_str())
                .filter(|n| !n.is_empty())
                .collect();
            names.sort_by_key(|n| n.to_lowercase());
            let stale = !self.rcon.roster_is_fresh(&server);
            any_stale |= stale;
            let mut heading = format!("**{label}** — {} online", roster.players.len());
            if stale {
                heading.push_str(&format!(
                    "  {} **as of {} ago**",
                    theme::WARN,
                    describe(now - taken_at)
                ));
                if let Some(problem) = self.rcon.roster_problem(&server) {
                    heading.push_str(&format!(
                        "\n{} {}",
                        theme::BAD,
                        Sanitize::message(Some(&problem))
                    ));
                }
            }
            let body = if roster.players.is_empty() {
                words.empty_server.to_owned()
            } else {
                names
                    .iter()
                    .map(|n| format!("`{}`", Sanitize::code(Some(n))))
                    .collect::<Vec<_>>()
                    .join(" ")
            };
            sections.push(format!("{heading}\n{body}"));
        }
        let title = if reported > 0 {
            let stale = if any_stale {
                " (some data is stale)"
            } else {
                ""
            };
            format!("{} {}{stale}", lore::head_count(total), words.whereabouts)
        } else {
            words.whereabouts_title.to_owned()
        };
        if sections.is_empty() {
            sections.push("*No servers are configured.*".to_owned());
        }
        (title, sections)
    }
}

impl SlashCommand for PlayersCommand {
    fn name(&self) -> &str {
        "players"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("players", "Who is online right now")
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let (title, sections) = self.sections();
            let pages =
                theme::paginate(sections.iter().map(String::as_str), theme::MAX_DESCRIPTION);
            Ok(self
                .paged
                .send(
                    context.responder,
                    context.invocation.caller.id,
                    &title,
                    pages,
                )
                .await?)
        })
    }
}
