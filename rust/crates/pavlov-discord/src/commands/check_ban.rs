//! `/checkban`'s ban history (C# `CheckBanCommand.BanHistory`): the staff bans a player has
//! served, newest first, under any name their account has used.
//!
//! - A ban staff LIFTED EARLY is not counted: it was a mistake, not a record.
//! - Automated bans are never listed (one evader can log dozens), but they take part in the
//!   matching, so the `/unban` that lifted one is not charged to an earlier staff ban.
//! - A reason is redacted (an auto-ban reason can carry an address) and the whole history is
//!   FITTED to one embed field, oldest dropped first.

use pavlov_core::skin::{EmbedBudget, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_host::moderation::ModAction;

/// How many bans are listed before "and N older".
pub const HISTORY_SHOWN: usize = 8;
const HISTORY_REASON_LENGTH: usize = 70;

/// The staff bans that count, and how each is labelled.
const LABELS: [(&str, &str); 3] = [
    ("permban", "Permanent ban"),
    ("tempban", "Temp ban"),
    ("warn-ban", "Ban (warning limit)"),
];
const AUTOMATED: [&str; 4] = ["autoban", "auto-ipban", "vpnban", "auto-vpnban"];
const MANUAL_UNBAN: &str = "unban";
const RELEASES: [&str; 2] = ["autoban-released", "auto-unban"];

fn label(action: &str) -> Option<&'static str> {
    LABELS
        .iter()
        .find(|(a, _)| eq_ignore_case(a, action))
        .map(|(_, l)| *l)
}

fn is_in(set: &[&str], action: &str) -> bool {
    set.iter().any(|a| eq_ignore_case(a, action))
}

/// The staff bans not lifted early, in log (chronological) order. An unban or release ends
/// the most recent standing ban; only an unban marks it lifted.
fn counted(actions: &[&ModAction]) -> Vec<usize> {
    let mut standing: Vec<usize> = Vec::new();
    let mut lifted = vec![false; actions.len()];
    for (index, action) in actions.iter().enumerate() {
        if label(&action.action).is_some() || is_in(&AUTOMATED, &action.action) {
            standing.push(index);
        } else if eq_ignore_case(&action.action, MANUAL_UNBAN) || is_in(&RELEASES, &action.action) {
            if let Some(ended) = standing.pop() {
                if !is_in(&RELEASES, &action.action) {
                    lifted[ended] = true;
                }
            }
        }
    }
    (0..actions.len())
        .filter(|&i| label(&actions[i].action).is_some() && !lifted[i])
        .collect()
}

/// The history for a field, or `None` when there is none. `names` is every name the
/// account has used; `asked` is the one typed, and others are labelled "as ...".
#[must_use]
pub fn ban_history(log: &[ModAction], names: &[String], asked: &str) -> Option<String> {
    let mine: Vec<&ModAction> = log
        .iter()
        .filter(|a| names.iter().any(|n| eq_ignore_case(n, &a.player)))
        .collect();
    let mut entries: Vec<&ModAction> = counted(&mine).into_iter().map(|i| mine[i]).collect();
    if entries.is_empty() {
        return None;
    }
    // Newest first; the list is chronological, so reversing first keeps ties in order.
    entries.reverse();
    entries.sort_by_key(|a| std::cmp::Reverse(a.at));

    let mut lines: Vec<String> = entries
        .iter()
        .take(HISTORY_SHOWN)
        .map(|a| {
            let mut reason = Sanitize::redact_private(a.reason.as_deref());
            if reason.chars().count() > HISTORY_REASON_LENGTH {
                reason = format!(
                    "{}…",
                    reason
                        .chars()
                        .take(HISTORY_REASON_LENGTH)
                        .collect::<String>()
                );
            }
            let alias = if eq_ignore_case(&a.player, asked) {
                String::new()
            } else {
                format!(" as **{}**", Sanitize::code(Some(&a.player)))
            };
            let why = if reason.is_empty() {
                String::new()
            } else {
                format!(" - {}", Sanitize::code(Some(&reason)))
            };
            format!(
                "{} **{}**{alias} by {}{why} {}",
                theme::DOT,
                label(&a.action).unwrap_or("Ban"),
                Sanitize::code(Some(&a.moderator)),
                theme::relative(a.at)
            )
        })
        .collect();
    let header = format!("{} ban(s) on record.", entries.len());
    let render = |lines: &[String]| {
        let mut text = format!("{header}\n{}", lines.join("\n"));
        if entries.len() > lines.len() {
            text.push_str(&format!("\n…and {} older.", entries.len() - lines.len()));
        }
        text
    };
    let limit = EmbedBudget::FIELD_LIMIT;
    let mut text = render(&lines);
    while text.chars().count() > limit && lines.len() > 1 {
        lines.pop();
        text = render(&lines);
    }
    Some(EmbedBudget::truncate(&text, limit))
}
