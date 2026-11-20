//! The `/stats` card (C# `StatsCommand.Card`). PURE, so what it says is testable without a
//! gateway or a live profile service.

use crate::embed::Embed;
use crate::intelligence::PlayerProfile;
use crate::skin::theme::{self, UP};
use crate::skin::{lore, police};
use crate::text::Sanitize;
use crate::text::number::{fixed, group_thousands};

/// "3h 20m", "45m": a playtime somebody can read at a glance.
#[must_use]
pub fn hours(minutes: i64) -> String {
    if minutes >= 60 {
        format!("{}h {}m", group_thousands(minutes / 60), minutes % 60)
    } else {
        format!("{minutes}m")
    }
}

/// The card.
#[must_use]
pub fn card(profile: &PlayerProfile) -> Embed {
    let skin = police();
    let who = Sanitize::message(Some(&profile.identity.current));
    let title = format!("{} {who}", skin.words.mark);

    // A STRANGER IS TOLD SO: zeroes read as "this player is terrible", not "never seen".
    if !profile.known() {
        return theme::notice(
            &title,
            Some("Never seen on any server. Check the spelling."),
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
        None => "*unaffiliated*".into(),
    };
    let activity = &profile.activity;
    let mut embed = theme::notice(&title, Some(&faction)).color(if activity.online {
        skin.colors.green
    } else {
        skin.colors.blue
    });

    embed = embed.field("Playtime", hours(activity.playtime_minutes), true);
    embed = embed.field(
        "K/D",
        if combat.any() {
            format!("**{}**", fixed(combat.ratio(), 2))
        } else {
            "*nothing recorded*".into()
        },
        true,
    );
    embed = embed.field(
        "Kills / deaths",
        if combat.any() {
            // Named rather than folded into deaths: being outgunned is not standing on
            // your own grenade.
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
        embed = embed.field(skin.words.money_label, lore::amount(balance), true);
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
        format!("{UP} online now{on}")
    } else {
        activity.last_seen.map_or_else(
            || "not seen recently".into(),
            |seen| format!("last seen {}", theme::relative(seen)),
        )
    };
    embed = embed.field("Status", status, true);
    if let Some(first) = activity.first_seen {
        embed = embed.field("First seen", theme::relative(first), true);
    }
    embed
}
