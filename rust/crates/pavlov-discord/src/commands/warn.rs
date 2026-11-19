//! `/warn give|list|remove|clear|standings` (C# `WarnCommand.cs`): warnings with automatic
//! escalation to a temporary ban.
//!
//! - CLEARING NEEDS ADMIN, everything else Mod: a mod who can wipe their own escalation's
//!   evidence can un-ban by the back door once the sweep re-reads it.
//! - The escalation ban is RECORDED FIRST, then enforced, and REPLACES any older row for the
//!   player (two rows: an unban lifts one and the other re-catches them).
//! - The same two rules as `/tempban` hold: a master owner's pardon, and a higher tier's ban,
//!   stand against a lower tier.
//! - NO ACCOUNT FLAG: flags never expire, and this ban was issued by a counter, not a person.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::moderation::{BanRecord, BanRules, StaffHierarchy, StaffTier};
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::time::{Clock, EasternTime};
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::moderation::AuditLog;
use pavlov_host::moderation::bans::BanService;
use pavlov_host::moderation::master_names::StoredMasterNames;
use pavlov_host::moderation::warnings::{
    DECAY_WINDOW, ESCALATION_BAN, ESCALATION_THRESHOLD, WarningError, WarningOutcome,
    WarningService, warnings_until_ban,
};
use pavlov_storage::datasets::TEMP_BANS;

use crate::access::{Access, RequiredAccess};
use crate::caller::Caller;
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Rows on the standings board.
const STANDINGS_ROWS: usize = 20;

/// What the command is wired to.
pub struct WarnCommand {
    pub warnings: Arc<WarningService>,
    pub bans: Arc<BanService>,
    pub tracking: Arc<IpTrackingService>,
    pub store: Arc<SerializedStore>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
    pub masters: Arc<StoredMasterNames>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for WarnCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WarnCommand").finish_non_exhaustive()
    }
}

/// The access a subcommand needs.
#[must_use]
pub fn required_for(sub: Option<&str>) -> RequiredAccess {
    if sub == Some("clear") {
        RequiredAccess::Admin
    } else {
        RequiredAccess::Mod
    }
}

fn days(window: chrono::TimeDelta) -> i64 {
    window.num_days()
}

impl WarnCommand {
    async fn give(
        &self,
        caller: &Caller,
        username: &str,
        player: &str,
        reason: &str,
    ) -> Result<Embed, CommandError> {
        if player.is_empty() {
            return Ok(theme::failure(
                "That name has nothing usable in it",
                Some("After sanitising there was no identifier left to warn."),
            ));
        }
        let outcome = match self.warnings.warn(player, reason, username).await {
            Ok(outcome) => outcome,
            Err(WarningError::NoPlayer) => {
                return Ok(theme::failure(
                    "That name has nothing usable in it",
                    Some("After sanitising there was no identifier left to warn."),
                ));
            }
            Err(error) => return Err(CommandError(format!("warnings: {error}"))),
        };
        let mut embed = theme::warning(
            &format!("{} Warned", theme::WARN),
            Some(&format!(
                "**{}** — {}",
                Sanitize::code(Some(player)),
                Sanitize::code(Some(&outcome.warning.reason))
            )),
        )
        .field(
            "Active warnings",
            format!(
                "{} (in the last {} days)",
                outcome.active,
                days(DECAY_WINDOW)
            ),
            true,
        )
        .field("On record", outcome.total.to_string(), true)
        .field("Warning id", format!("`{}`", outcome.warning.id), true);
        if !outcome.escalated {
            // How close they are turns a warning into a decision rather than a note.
            if let Some(remaining) = warnings_until_ban(outcome.active) {
                embed = embed.field(
                    "Next step",
                    format!(
                        "{}h ban at {ESCALATION_THRESHOLD} active warnings ({remaining} more).",
                        ESCALATION_BAN.num_hours()
                    ),
                    false,
                );
            }
            return Ok(embed);
        }
        let applied = self.escalate(caller, username, player, &outcome).await?;
        Ok(embed.field(format!("{} Escalated", theme::DENY), applied, false))
    }

    async fn escalate(
        &self,
        caller: &Caller,
        username: &str,
        player: &str,
        outcome: &WarningOutcome,
    ) -> Result<String, CommandError> {
        let hours = ESCALATION_BAN.num_hours();
        let tier = self.access.tier_of(caller);
        if tier < StaffTier::MasterOwner && self.masters.is_pardoned(player) {
            return Ok(format!(
                "No escalation ban: a {} unbanned them, and only a Master Owner can ban them again.",
                StaffHierarchy::name(StaffTier::MasterOwner)
            ));
        }
        let now = self.clock.now();
        let expires = now + ESCALATION_BAN;
        let reason = format!(
            "Automatic: {} warnings in {} days",
            outcome.active,
            days(DECAY_WINDOW)
        );
        let record = BanRecord {
            reason: Some(reason.clone()),
            moderator: Some(format!("{username} (auto)")),
            at: Some(now),
            expires: Some(expires),
            permanent: false,
            duration_label: Some(EasternTime::time_left(expires, now)),
            tier: Some(tier),
            ..BanRecord::for_player(player)
        };
        let mut outranked: Option<BanRecord> = None;
        self.store
            .update(TEMP_BANS, Vec::new, |mut all: Vec<BanRecord>| {
                if let Some(standing) =
                    BanRules::protected_from_replacement(&all, player, tier, now)
                {
                    outranked = Some(standing.clone());
                    return None;
                }
                all.retain(|b| !BanRules::same_player(Some(&b.player_id), Some(player)));
                all.push(record);
                Some(all)
            })
            .await?;
        if let Some(standing) = outranked {
            return Ok(format!(
                "No escalation ban: they are already banned by a {}, and replacing that ban is above your authority.",
                StaffHierarchy::name(standing.tier.unwrap_or(StaffTier::None))
            ));
        }
        let account = self.tracking.account_by_name(player);
        let enforcement = self
            .bans
            .hard_enforce(player, account.as_ref().map(|a| a.id.as_str()), true, true)
            .await;
        self.audit
            .record(
                "warn-ban",
                username,
                player,
                Some(&format!("{reason} - {hours}h ban")),
            )
            .await?;
        tracing::warn!(
            player,
            hours,
            active = outcome.active,
            kicked_on = enforcement.servers,
            save = enforcement.summary(),
            "warning escalation banned"
        );
        Ok(if enforcement.landed() {
            format!(
                "{hours}h ban at {ESCALATION_THRESHOLD} warnings — expires {}.",
                theme::relative(expires)
            )
        } else {
            format!(
                "{hours}h ban recorded at {ESCALATION_THRESHOLD} warnings, expiring {}: {}.",
                theme::relative(expires),
                enforcement.summary()
            )
        })
    }

    fn list(&self, player: &str) -> Embed {
        let mut all = self.warnings.for_player(player);
        if all.is_empty() {
            return theme::success(
                &format!("{} Clean record", theme::OK),
                Some(&format!(
                    "**{}** has no warnings.",
                    Sanitize::code(Some(player))
                )),
            );
        }
        let active = self.warnings.active_for(player);
        all.sort_by_key(|w| std::cmp::Reverse(w.at));
        let lines: Vec<String> = all
            .iter()
            .map(|w| {
                let mark = if active.iter().any(|a| a.id == w.id) {
                    theme::WARN
                } else {
                    theme::INFO
                };
                format!(
                    "{mark} `{}` — {}\n　by {}, {}",
                    w.id,
                    Sanitize::code(Some(&w.reason)),
                    Sanitize::code(Some(&w.moderator)),
                    theme::relative(w.at)
                )
            })
            .collect();
        let first = theme::paginate(lines.iter().map(String::as_str), theme::MAX_DESCRIPTION)
            .into_iter()
            .next()
            .unwrap_or_default();
        let mut embed = theme::notice(
            &format!(
                "{} Warnings for {}",
                theme::WARN,
                Sanitize::code(Some(player))
            ),
            Some(&first),
        )
        .field(
            "Active",
            format!("{} (counts toward escalation)", active.len()),
            true,
        )
        .field("On record", all.len().to_string(), true);
        if let Some(remaining) = warnings_until_ban(active.len()) {
            embed = embed.field(
                "Next step",
                format!("{}h ban in {remaining}", ESCALATION_BAN.num_hours()),
                true,
            );
        }
        embed
    }

    async fn remove(&self, player: &str, id: &str) -> Result<Embed, CommandError> {
        if id.is_empty() {
            return Ok(theme::failure(
                "No warning id given",
                Some("Run `/warn list` to see the ids."),
            ));
        }
        Ok(if self.warnings.remove(player, id).await? {
            theme::success(
                &format!("{} Warning removed", theme::OK),
                Some(&format!(
                    "`{id}` is off **{}**'s record.",
                    Sanitize::code(Some(player))
                )),
            )
        } else {
            theme::failure(
                "No such warning",
                Some(&format!(
                    "**{}** has no warning with id `{}`.",
                    Sanitize::code(Some(player)),
                    Sanitize::code(Some(id))
                )),
            )
        })
    }

    async fn clear(&self, username: &str, player: &str) -> Result<Embed, CommandError> {
        let cleared = self.warnings.clear(player, username).await?;
        Ok(if cleared == 0 {
            theme::notice(
                "Nothing to clear",
                Some(&format!(
                    "**{}** had no warnings.",
                    Sanitize::code(Some(player))
                )),
            )
        } else {
            theme::success(
                &format!("{} Record cleared", theme::OK),
                Some(&format!(
                    "Removed {cleared} warning(s) from **{}**.\nAn existing escalation ban is NOT lifted by this - use \
                     `/unban` for that.",
                    Sanitize::code(Some(player))
                )),
            )
        })
    }

    fn standings(&self) -> Embed {
        let rows = self.warnings.standings();
        if rows.is_empty() {
            return theme::success(
                &format!("{} Nobody is warned", theme::OK),
                Some("No player has an active warning."),
            );
        }
        let lines: Vec<String> = rows
            .iter()
            .take(STANDINGS_ROWS)
            .enumerate()
            .map(|(i, r)| {
                format!(
                    "`{:>2}.` **{}** — {} active, {} on record",
                    i + 1,
                    Sanitize::code(Some(&r.player)),
                    r.active,
                    r.total
                )
            })
            .collect();
        theme::warning(
            &format!("{} Active warnings", theme::WARN),
            Some(&lines.join("\n")),
        )
        .brand(Some(&format!("Decays after {} days", days(DECAY_WINDOW))))
    }

    async fn run(&self, context: &CommandContext<'_>) -> Result<Embed, CommandError> {
        let invocation = context.invocation;
        let sub = invocation.subcommand();
        let required = required_for(sub);
        let caller = &invocation.caller;
        if !self.access.allows(required, caller) {
            return Ok(theme::denied(
                "Not allowed",
                Some(&self.access.explain_refusal(caller, required)),
            ));
        }
        let player = Sanitize::id(invocation.string("playerid"));
        let username = invocation.username.as_str();
        Ok(match sub {
            Some("give") => {
                self.give(
                    caller,
                    username,
                    &player,
                    invocation.string("reason").unwrap_or(""),
                )
                .await?
            }
            Some("list") => self.list(&player),
            Some("remove") => {
                self.remove(&player, invocation.string("id").unwrap_or("").trim())
                    .await?
            }
            Some("clear") => self.clear(username, &player).await?,
            Some("standings") => self.standings(),
            _ => theme::failure("Unknown subcommand", None),
        })
    }
}

impl SlashCommand for WarnCommand {
    fn name(&self) -> &str {
        "warn"
    }

    fn build(&self) -> CommandSpec {
        let player = || {
            OptionSpec::new(OptionKind::String, "playerid", "Player ID or username")
                .required()
                .autocomplete()
        };
        CommandSpec::new(
            "warn",
            "Moderation - Warn a player, with automatic escalation",
        )
        .option(
            OptionSpec::sub("give", "Warn a player")
                .option(player())
                .option(OptionSpec::new(OptionKind::String, "reason", "Why").required()),
        )
        .option(OptionSpec::sub("list", "A player's warning record").option(player()))
        .option(
            OptionSpec::sub("remove", "Remove one warning by its id")
                .option(player())
                .option(
                    OptionSpec::new(OptionKind::String, "id", "The warning id from /warn list")
                        .required(),
                ),
        )
        .option(OptionSpec::sub("clear", "Clear a player's whole record").option(player()))
        .option(OptionSpec::sub(
            "standings",
            "Who currently has active warnings",
        ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = self.run(&context).await?;
            // Standings carries its own footer; brand(None) keeps an existing one.
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
