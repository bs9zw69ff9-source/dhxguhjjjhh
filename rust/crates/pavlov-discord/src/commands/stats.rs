//! `/stats` (C# `StatsCommand.cs`): playtime, K/D and faction for a player. Open to everyone,
//! so it asks for the MODERATION view: the card physically cannot print an address.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::{Embed, palette};
use pavlov_core::intelligence::{PlayerProfile, ProfileVisibility};
use pavlov_core::skin::{lore, police, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::text::number::group_thousands;
use pavlov_host::factions::FactionMembers;
use pavlov_host::intelligence::PlayerIntelligenceService;

use crate::access::{Access, RequiredAccess};
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// What the command is wired to.
pub struct StatsCommand {
    pub intelligence: Arc<PlayerIntelligenceService>,
    pub members: Arc<FactionMembers>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for StatsCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StatsCommand").finish_non_exhaustive()
    }
}

/// `75` is `1h 15m`; under an hour, minutes only.
#[must_use]
pub fn hours(minutes: i64) -> String {
    if minutes >= 60 {
        format!("{}h {}m", group_thousands(minutes / 60), minutes % 60)
    } else {
        format!("{minutes}m")
    }
}

/// The card. A stranger is TOLD so: zeroes read as "terrible player", not "never seen".
#[must_use]
pub fn card(profile: &PlayerProfile) -> Embed {
    let mark = police().words.mark;
    let who = Sanitize::message(Some(&profile.identity.current));
    if !profile.known() {
        return theme::notice(
            &format!("{mark} {who}"),
            Some(
                "Never seen on any server. Check the spelling - names are exactly as they are in game.",
            ),
        );
    }
    let combat = profile.combat.unwrap_or_default();
    let faction = match profile.faction.name.as_deref().filter(|n| !n.is_empty()) {
        Some(name) => {
            let rank = profile
                .faction
                .rank
                .as_deref()
                .filter(|r| !r.is_empty())
                .map(|r| format!(" — {}", Sanitize::markdown(Some(r))))
                .unwrap_or_default();
            format!("**{}**{rank}", Sanitize::markdown(Some(name)))
        }
        None => "*unaffiliated*".to_owned(),
    };
    let activity = &profile.activity;
    let mut embed = theme::notice(&format!("{mark} {who}"), Some(&faction))
        .color(if activity.online {
            palette::GREEN
        } else {
            palette::BLUE
        })
        .field("Playtime", hours(activity.playtime_minutes), true)
        .field(
            "K/D",
            if combat.any() {
                format!("**{:.2}**", combat.ratio())
            } else {
                "*nothing recorded*".into()
            },
            true,
        )
        .field(
            "Kills / deaths",
            if combat.any() {
                // Named apart from deaths: outgunned is not standing on your own grenade.
                let own = if combat.suicides > 0 {
                    format!("  ({} self)", combat.suicides)
                } else {
                    String::new()
                };
                format!(
                    "{} / {}{own}",
                    group_thousands(combat.kills),
                    group_thousands(combat.deaths)
                )
            } else {
                "—".into()
            },
            true,
        );
    if let Some(balance) = profile.economy.balance {
        embed = embed.field(police().words.money_label, lore::amount(balance), true);
    }
    let status = if activity.online {
        let on = if activity.servers.is_empty() {
            String::new()
        } else {
            format!(
                " on {}",
                Sanitize::markdown(Some(&activity.servers.join(", ")))
            )
        };
        format!("{} online now{on}", theme::UP)
    } else {
        activity.last_seen.map_or_else(
            || "not seen recently".to_owned(),
            |seen| format!("last seen {}", theme::relative(seen)),
        )
    };
    embed = embed.field("Status", status, true);
    if let Some(first) = activity.first_seen {
        embed = embed.field("First seen", theme::relative(first), true);
    }
    embed
}

impl StatsCommand {
    fn answer(&self, caller: u64, typed: Option<&str>) -> Embed {
        let mut player = Sanitize::id(typed);
        if player.is_empty() {
            // Their own name, when the bot has it on file.
            player = Sanitize::id(self.members.of(caller).map(|m| m.name).as_deref());
        }
        if player.is_empty() {
            return theme::failure(
                "Which player?",
                Some(
                    "Give an in-game name. Yours is filled in automatically once you have been whitelisted with \
                     `/whitelist add`.",
                ),
            );
        }
        card(
            &self
                .intelligence
                .profile_for(&player, ProfileVisibility::Moderation),
        )
    }
}

impl SlashCommand for StatsCommand {
    fn name(&self) -> &str {
        "stats"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("stats", "Playtime, K/D and faction for a player").option(
            OptionSpec::new(
                OptionKind::String,
                "player",
                "In-game name. Defaults to the name you were whitelisted under",
            )
            .autocomplete(),
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if self.access.allows(RequiredAccess::Public, caller) {
                self.answer(caller.id, context.invocation.string("player"))
            } else {
                theme::denied(
                    "Not allowed",
                    Some(&self.access.explain_refusal(caller, RequiredAccess::Public)),
                )
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
