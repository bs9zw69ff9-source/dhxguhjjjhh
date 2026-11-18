//! `/factionstats` (C# `IntelligenceCommands.cs`): roster health, who is active and who to
//! review.
//!
//! - IT RECOMMENDS AND NEVER ACTS: nothing here demotes or removes anyone, and no setting could
//!   make it, or a Sergeant on holiday would be quietly demoted.
//! - Faction Leader, not Mod: it is the roster's own management view.
//! - Playtime and last-seen are READ ONCE and joined in memory, not parsed per member.
//! - Never-seen members are listed APART from idle ones: they usually predate the tracking,
//!   and mixed in they would read as the most inactive people in the faction.

use std::sync::Arc;

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::factions::{FactionActivity, FactionDefinition, FactionReport, MemberActivity};
use pavlov_core::json::timestamp_from_value;
use pavlov_core::skin::{EmbedBudget, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::text::number::group_thousands;
use pavlov_core::time::Clock;
use pavlov_host::factions::RosterService;
use pavlov_storage::datasets::LAST_SEEN;
use serde_json::Value;

use crate::access::{Access, RequiredAccess};
use crate::boards::Boards;
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Idle members listed.
const IDLE_ROWS: usize = 25;
/// Never-seen members listed.
const UNKNOWN_ROWS: usize = 15;

/// What the command is wired to.
pub struct FactionStatsCommand {
    pub rosters: Arc<RosterService>,
    pub boards: Arc<Boards>,
    pub store: Arc<SerializedStore>,
    pub access: Arc<Access>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for FactionStatsCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FactionStatsCommand")
            .finish_non_exhaustive()
    }
}

/// One field's worth: the C# paged at the DESCRIPTION limit and put it in a field, which
/// Discord rejects past 1024 characters, taking the whole reply with it.
fn first_page<'a>(lines: impl Iterator<Item = &'a str>) -> String {
    theme::paginate(lines, EmbedBudget::FIELD_LIMIT)
        .into_iter()
        .next()
        .unwrap_or_default()
}

/// Members, activity and the rank ladder.
#[must_use]
pub fn overview(report: &FactionReport, faction: &FactionDefinition) -> Embed {
    let mut embed = theme::notice(
        &format!("{} — {} member(s)", report.faction, report.total),
        None,
    )
    .field(
        "Active",
        format!("{} ({}%)", report.active, report.active_percent()),
        true,
    )
    .field("Worth reviewing", report.inactive30().to_string(), true)
    .field(
        "Total playtime",
        format!("{}h", group_thousands(report.total_minutes / 60)),
        true,
    );
    if faction.has_ranks() {
        let ranks: Vec<String> = report
            .rank_counts
            .iter()
            .map(|(rank, count)| format!("`{count:>3}` {}", Sanitize::message(Some(rank))))
            .collect();
        embed = embed.field("Ranks", ranks.join("\n"), false);
    }
    embed.field(
        "Active means",
        format!(
            "Seen inside the last {} days. Worth reviewing means idle for {}+ days, or under {} minutes of recorded \
             playtime.",
            FactionActivity::active_window().num_days(),
            FactionActivity::inactive_after().num_days(),
            FactionActivity::MINIMUM_MONTHLY_MINUTES
        ),
        false,
    )
}

/// Who to review, idle and never-seen apart.
#[must_use]
pub fn inactive(report: &FactionReport) -> Embed {
    if report.inactive.is_empty() {
        return theme::success(
            &format!("{} — everybody is active", report.faction),
            Some("Nobody meets the review thresholds."),
        );
    }
    let (idle, unknown): (Vec<_>, Vec<_>) = report.inactive.iter().partition(|i| i.days.is_some());
    let mut embed = theme::warning(
        &format!("{} — {} to review", report.faction, report.inactive.len()),
        None,
    );
    if !idle.is_empty() {
        let lines: Vec<String> = idle
            .iter()
            .take(IDLE_ROWS)
            .map(|i| {
                format!(
                    "`{:<16}` **{}** — {}",
                    i.member.rank,
                    Sanitize::code(Some(&i.member.player)),
                    Sanitize::message(Some(&i.reason))
                )
            })
            .collect();
        embed = embed.field(
            format!("Idle ({})", idle.len()),
            first_page(lines.iter().map(String::as_str)),
            false,
        );
    }
    if !unknown.is_empty() {
        let lines: Vec<String> = unknown
            .iter()
            .take(UNKNOWN_ROWS)
            .map(|i| format!("`{}`", Sanitize::code(Some(&i.member.player))))
            .collect();
        embed = embed.field(
            format!("No record ({})", unknown.len()),
            format!(
                "{}\n\nThe bot has never seen these connect — usually they predate its tracking or play under \
                 another name. **Not** the same as inactive.",
                first_page(lines.iter().map(String::as_str))
            ),
            false,
        );
    }
    embed
}

impl FactionStatsCommand {
    fn members(&self, faction: &FactionDefinition) -> Vec<MemberActivity> {
        let playtime = self.boards.playtime();
        let seen: IgnoreCaseMap<Value> = self
            .store
            .read(LAST_SEEN, IgnoreCaseMap::new())
            .unwrap_or_default();
        self.rosters
            .roster(faction)
            .into_iter()
            .map(|m| {
                let entry = playtime.get(&m.player);
                let last_seen: Option<DateTime<Utc>> = seen
                    .get(&m.player)
                    .and_then(timestamp_from_value)
                    .or_else(|| entry.and_then(|e| e.last_seen));
                MemberActivity {
                    minutes: entry.map_or(0, |e| e.minutes),
                    player: m.player,
                    rank: m.rank,
                    last_seen,
                }
            })
            .collect()
    }

    fn answer(&self, sub: Option<&str>, name: Option<&str>) -> Embed {
        let Some(faction) = self.rosters.factions().get(name) else {
            return theme::failure("Unknown faction", None);
        };
        if !self.rosters.enabled() {
            return theme::failure(
                "The roster files are unreachable",
                Some("Check `FACTION_ROLES_PATH`. Without it there is no roster to report on."),
            );
        }
        let report = FactionActivity::report(faction, &self.members(faction), self.clock.now());
        if sub == Some("inactive") {
            inactive(&report)
        } else {
            overview(&report, faction)
        }
    }
}

impl SlashCommand for FactionStatsCommand {
    fn name(&self) -> &str {
        "factionstats"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        let faction = || {
            self.rosters.factions().names().fold(
                OptionSpec::new(OptionKind::String, "faction", "Which faction").required(),
                |option, name| option.choice(Choice::string(name, name)),
            )
        };
        CommandSpec::new(
            "factionstats",
            "Faction Leader - Roster activity, rank distribution and who to review",
        )
        .option(
            OptionSpec::sub("overview", "Members, activity and rank distribution")
                .option(faction()),
        )
        .option(OptionSpec::sub("inactive", "Members worth reviewing").option(faction()))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if self.access.allows(RequiredAccess::FactionLeader, caller) {
                self.answer(
                    context.invocation.subcommand(),
                    context.invocation.string("faction"),
                )
            } else {
                theme::denied(
                    "Not allowed",
                    Some(
                        &self
                            .access
                            .explain_refusal(caller, RequiredAccess::FactionLeader),
                    ),
                )
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
