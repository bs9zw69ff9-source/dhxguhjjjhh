//! `/donator` and `/suspendrank` (C# `ConfigCommands.cs`; `/setroles` has its own file).
//! `/setrconroles` went with the RCON+ menu panel it configured (owner, 2026-11-18).
//!
//! - AN UNREADABLE ROSTER IS NOT EMPTY: a donator list "updated" from nothing wipes everyone.
//! - A SUSPENSION IS RECORDED BEFORE THE RANK IS PULLED, and the write's result is CHECKED:
//!   the other order strips a rank with nothing saying how to give it back.

use std::sync::Arc;

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::skin::{police, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::{IgnoreCaseMap, eq_ignore_case};
use pavlov_core::time::{Clock, EasternTime};
use pavlov_host::factions::RosterService;
use pavlov_host::factions::rank_restorer::RankSuspension;
use pavlov_storage::datasets::{DONATOR_SUSPEND, RANK_SUSPENSIONS};
use serde::{Deserialize, Serialize};

use crate::access::{Access, RequiredAccess};
use crate::caller::Caller;
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::paged::Paged;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// The donator roster file, beside the faction rosters.
pub const DONATOR_ROSTER: &str = "donators.txt";

/// One donator, as stored (camelCase, the time in epoch milliseconds).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Donator {
    #[serde(default)]
    pub player: String,
    #[serde(default)]
    pub added_by: String,
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub at: DateTime<Utc>,
}

fn refuse(access: &Access, caller: &Caller, required: RequiredAccess) -> Option<Embed> {
    (!access.allows(required, caller)).then(|| {
        theme::denied(
            "Not allowed",
            Some(&access.explain_refusal(caller, required)),
        )
    })
}

// ---- /donator ----

pub struct DonatorCommand {
    pub store: Arc<SerializedStore>,
    pub rosters: Arc<RosterService>,
    pub access: Arc<Access>,
    pub paged: Arc<Paged>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for DonatorCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DonatorCommand").finish_non_exhaustive()
    }
}

impl DonatorCommand {
    async fn change(
        &self,
        username: &str,
        adding: bool,
        raw: Option<&str>,
    ) -> Result<Embed, CommandError> {
        let player = Sanitize::id(raw);
        if player.is_empty() {
            return Ok(theme::failure("That name has nothing usable in it", None));
        }
        if self.rosters.enabled() {
            let Some(roster) = self.rosters.read(DONATOR_ROSTER) else {
                return Ok(theme::failure(
                    "Could not read the donator roster",
                    Some("The file is unreadable, so nothing was written - the list is untouched."),
                ));
            };
            let next: Vec<String> = if adding {
                let mut next = roster.clone();
                if !roster.iter().any(|p| eq_ignore_case(p, &player)) {
                    next.push(player.clone());
                }
                next
            } else {
                roster
                    .into_iter()
                    .filter(|p| !eq_ignore_case(p, &player))
                    .collect()
            };
            if !self.rosters.write(DONATOR_ROSTER, next, false).await {
                return Ok(theme::failure(
                    "Roster write refused",
                    Some("The write guard rejected it - the list is untouched. Check the log."),
                ));
            }
        }
        let donator = Donator {
            player: player.clone(),
            added_by: username.to_owned(),
            at: self.clock.now(),
        };
        self.store
            .update(
                DONATOR_SUSPEND,
                IgnoreCaseMap::new,
                |mut all: IgnoreCaseMap<Donator>| {
                    if adding {
                        all.insert(player.clone(), donator);
                    } else {
                        all.remove(&player);
                    }
                    Some(all)
                },
            )
            .await?;
        tracing::info!(action = if adding { "add" } else { "remove" }, player = %player, by = username, "donator");
        Ok(theme::success(
            if adding {
                "Donator added"
            } else {
                "Donator removed"
            },
            Some(&format!("**{}**", Sanitize::code(Some(&player)))),
        ))
    }
}

impl SlashCommand for DonatorCommand {
    fn name(&self) -> &str {
        "donator"
    }

    fn build(&self) -> CommandSpec {
        let player = || {
            OptionSpec::new(OptionKind::String, "playerid", "Player ID or username")
                .required()
                .autocomplete()
        };
        CommandSpec::new("donator", "Admin - Manage the donator list")
            .option(OptionSpec::sub("add", "Add a donator").option(player()))
            .option(OptionSpec::sub("remove", "Remove a donator").option(player()))
            .option(OptionSpec::sub("list", "Show the donator list"))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let sub = invocation.subcommand();
            if sub == Some("list") {
                let stored: IgnoreCaseMap<Donator> = self
                    .store
                    .read(DONATOR_SUSPEND, IgnoreCaseMap::new())
                    .unwrap_or_default();
                let mut all: Vec<Donator> = stored.iter().map(|(_, d)| d.clone()).collect();
                if all.is_empty() {
                    return context
                        .reply(Reply::embed(theme::notice("No donators", None).brand(None)))
                        .await;
                }
                all.sort_by_key(|d| d.player.to_lowercase());
                let lines: Vec<String> = all
                    .iter()
                    .map(|d| {
                        format!(
                            "`{}` — added by {}, {}",
                            Sanitize::code(Some(&d.player)),
                            d.added_by,
                            theme::relative(d.at)
                        )
                    })
                    .collect();
                // Every page: taking the first silently dropped the rest of a long list.
                let pages =
                    theme::paginate(lines.iter().map(String::as_str), theme::MAX_DESCRIPTION);
                let title = format!("{} Donators — {}", police().words.money_glyph, all.len());
                return Ok(self
                    .paged
                    .send(context.responder, invocation.caller.id, &title, pages)
                    .await?);
            }
            let embed = match refuse(&self.access, &invocation.caller, RequiredAccess::Admin) {
                Some(refusal) => refusal,
                None => {
                    self.change(
                        &invocation.username,
                        sub == Some("add"),
                        invocation.string("playerid"),
                    )
                    .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /suspendrank ----

pub struct SuspendRankCommand {
    pub store: Arc<SerializedStore>,
    pub rosters: Arc<RosterService>,
    pub access: Arc<Access>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for SuspendRankCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SuspendRankCommand").finish_non_exhaustive()
    }
}

impl SuspendRankCommand {
    async fn run(
        &self,
        username: &str,
        raw_player: Option<&str>,
        raw_time: Option<&str>,
    ) -> Result<Embed, CommandError> {
        let player = Sanitize::id(raw_player);
        let Some(duration) = EasternTime::parse_duration(raw_time) else {
            return Ok(theme::failure(
                "That duration makes no sense",
                Some("Use `30m`, `2h` or `1d`. A bare number is minutes."),
            ));
        };
        let Some(membership) = self.rosters.find(&player) else {
            return Ok(theme::failure(
                "Not whitelisted",
                Some(&format!(
                    "**{}** is not on any roster.",
                    Sanitize::code(Some(&player))
                )),
            ));
        };
        let Some(faction) = self
            .rosters
            .factions()
            .get(Some(&membership.faction))
            .cloned()
        else {
            return Ok(theme::failure(
                "Not whitelisted",
                Some("Their faction is no longer configured."),
            ));
        };
        let until = self.clock.now() + duration;
        let suspension = RankSuspension {
            player: player.clone(),
            faction: faction.name.clone(),
            restore_to: membership.rank.clone(),
            suspended_by: username.to_owned(),
            until,
        };
        // RECORD FIRST, and check it: an unreadable dataset refuses without failing.
        let recorded = self
            .store
            .update(
                RANK_SUSPENSIONS,
                IgnoreCaseMap::new,
                |mut all: IgnoreCaseMap<RankSuspension>| {
                    all.insert(player.clone(), suspension);
                    Some(all)
                },
            )
            .await?;
        if !recorded.ok {
            let error = recorded.error.unwrap_or_else(|| "unknown error".into());
            tracing::error!(player = %player, %error, "suspendrank refused: the suspension could not be recorded");
            return Ok(theme::failure(
                "Not suspended",
                Some(&format!(
                    "The suspension could not be saved, so **{}** keeps their rank. ({})",
                    Sanitize::code(Some(&player)),
                    Sanitize::code(Some(&error))
                )),
            ));
        }
        let left = self.rosters.leave(&faction, &player).await;
        if !left.is_allowed() {
            // Still on the roster: drop the record, or the restore reports a rank never lost.
            self.store
                .update(
                    RANK_SUSPENSIONS,
                    IgnoreCaseMap::new,
                    |mut all: IgnoreCaseMap<RankSuspension>| all.remove(&player).map(|_| all),
                )
                .await?;
            tracing::error!(player = %player, outcome = ?left.outcome, "suspendrank failed: could not remove them from the roster");
            return Ok(theme::failure(
                "Not suspended",
                Some(&format!(
                    "**{}** could not be taken off the {} roster ({:?}). Nothing changed.",
                    Sanitize::code(Some(&player)),
                    faction.name,
                    left.outcome
                )),
            ));
        }
        tracing::info!(player = %player, was = %membership.rank, until = %EasternTime::stamp(Some(until)), by = username, "suspendrank");
        Ok(theme::warning(
            "Rank suspended",
            Some(&format!(
                "**{}** — {} **{}**",
                Sanitize::code(Some(&player)),
                faction.name,
                membership.rank
            )),
        )
        .field("Restores", theme::relative(until), true)
        .field("By", username.to_owned(), true))
    }
}

impl SlashCommand for SuspendRankCommand {
    fn name(&self) -> &str {
        "suspendrank"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "suspendrank",
            "Mod - Pull a member's whitelist rank for a set time; it auto-restores",
        )
        .option(
            OptionSpec::new(OptionKind::String, "playerid", "Player ID or username")
                .required()
                .autocomplete(),
        )
        .option(
            OptionSpec::new(OptionKind::String, "time", "How long, e.g. 30m, 2h, 1d").required(),
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = match refuse(&self.access, &invocation.caller, RequiredAccess::Mod) {
                Some(refusal) => refusal,
                None => {
                    self.run(
                        &invocation.username,
                        invocation.string("playerid"),
                        invocation.string("time"),
                    )
                    .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
