//! `/whitelist`'s rendering (C# `WhitelistCommand` in `FactionCommands.cs`): the roster and
//! sub-class lists, and the reply for each membership outcome.

use pavlov_core::embed::Embed;
use pavlov_core::factions::{FactionDefinition, MembershipDecision, MembershipOutcome};
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::{IgnoreCaseMap, cmp_ignore_case, eq_ignore_case};
use pavlov_host::factions::roster::Membership;

use super::membership_reply;

/// Characters per page of a roster list.
pub const LIST_PAGE_LENGTH: usize = 1800;

fn code(text: &str) -> String {
    Sanitize::code(Some(text))
}

fn markdown(text: &str) -> String {
    Sanitize::markdown(Some(text))
}

/// The roster, highest rank first, one member per line with their sub-class beside them.
#[must_use]
pub fn roster_lines(
    faction: &FactionDefinition,
    roster: &[Membership],
    subclasses: &IgnoreCaseMap<String>,
) -> Vec<String> {
    if roster.is_empty() {
        return vec!["*Nobody is on this roster.*".to_owned()];
    }
    let mut lines = Vec::new();
    for rank in faction.order.iter().rev() {
        let mut held: Vec<&Membership> = roster
            .iter()
            .filter(|m| eq_ignore_case(&m.rank, rank))
            .collect();
        if held.is_empty() {
            continue;
        }
        held.sort_by(|a, b| cmp_ignore_case(&a.player, &b.player));
        if !lines.is_empty() {
            lines.push(String::new());
        }
        lines.push(format!("**{}** · {}", markdown(rank), held.len()));
        lines.extend(held.iter().map(|m| match subclasses.get(&m.player) {
            Some(subclass) => format!(
                "{} `{}` · *{}*",
                theme::DOT,
                code(&m.player),
                markdown(subclass)
            ),
            None => format!("{} `{}`", theme::DOT, code(&m.player)),
        }));
    }
    lines
}

/// Every sub-class the faction has, with who holds it (senior first) and their rank.
#[must_use]
pub fn subclass_lines(
    faction: &FactionDefinition,
    roster: &[Membership],
    subclasses: &IgnoreCaseMap<String>,
) -> Vec<String> {
    if faction.subclasses.is_empty() {
        return vec![format!(
            "**{}** has no sub-classes.",
            markdown(&faction.name)
        )];
    }
    let rank_of = |player: &str| {
        roster
            .iter()
            .find(|m| eq_ignore_case(&m.player, player))
            .map(|m| m.rank.as_str())
    };
    let mut names: Vec<&String> = faction.subclasses.keys().collect();
    names.sort_by(|a, b| cmp_ignore_case(a, b));
    let mut lines = Vec::new();
    for name in names {
        let mut holders: Vec<&str> = subclasses
            .iter()
            .filter(|(_, held)| eq_ignore_case(held, name))
            .map(|(player, _)| player)
            .collect();
        holders.sort_by(|a, b| {
            faction
                .index_of(rank_of(b))
                .cmp(&faction.index_of(rank_of(a)))
                .then_with(|| cmp_ignore_case(a, b))
        });
        if !lines.is_empty() {
            lines.push(String::new());
        }
        lines.push(format!("**{}** · {}", markdown(name), holders.len()));
        if holders.is_empty() {
            lines.push("*nobody yet*".to_owned());
        }
        lines.extend(holders.iter().map(|p| match rank_of(p) {
            Some(rank) => format!("{} `{}` · {}", theme::DOT, code(p), markdown(rank)),
            None => format!("{} `{}` · *not on the roster*", theme::DOT, code(p)),
        }));
    }
    lines
}

/// The reply for an add or a removal.
#[must_use]
pub fn describe(decision: &MembershipDecision, player: &str, faction: &FactionDefinition) -> Embed {
    match decision.outcome {
        MembershipOutcome::Allowed => theme::success(
            "Whitelist updated",
            Some(&format!(
                "**{}** — {} **{}**",
                code(player),
                faction.name,
                decision.rank.as_deref().unwrap_or("")
            )),
        ),
        MembershipOutcome::AlreadyInAnotherFaction => theme::denied(
            "Already in another faction",
            Some(&format!(
                "**{}** belongs to **{}**. Remove them from that faction first - a player may only belong to one.",
                code(player),
                decision.conflict.as_deref().unwrap_or("another faction")
            )),
        ),
        MembershipOutcome::NoChange => theme::notice(
            "Nothing to do",
            Some(&format!("**{}** is already in that state.", code(player))),
        ),
        MembershipOutcome::NotWhitelisted => theme::failure(
            "Not whitelisted",
            Some(&format!("**{}** is not on this roster.", code(player))),
        ),
        _ => membership_reply::fallback(decision),
    }
}
