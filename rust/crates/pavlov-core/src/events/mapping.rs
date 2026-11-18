//! Which lens a staff action belongs to (C# `Core/Events/EventMapping.cs`).
//!
//! EVERY ACTION LANDS SOMEWHERE. The fallback is [`EventCategory::Staff`] rather than a "misc"
//! bucket or a dropped event: an action in the audit log and missing from the timeline is the
//! failure this feature exists to remove. THE TABLE IS EXPLICIT, because a mis-categorised event
//! is invisible to the filter somebody is investigating with.

use super::EventCategory;
use crate::text::case::{eq_ignore_case, to_lower_invariant};

const CATEGORIES: &[(&str, EventCategory)] = &[
    // ---- security: detections, not decisions ----
    ("autoban", EventCategory::Security),
    ("vpnban", EventCategory::Security),
    ("evasion", EventCategory::Security),
    ("flag", EventCategory::Security),
    // ---- economy ----
    ("givecaps", EventCategory::Economy),
    ("adjustcaps", EventCategory::Economy),
    ("givemoney", EventCategory::Economy), // the same two, under the police theme
    ("adjustmoney", EventCategory::Economy),
    ("payroll", EventCategory::Economy),
    ("money-alert", EventCategory::Economy),
    // ---- faction ----
    ("whitelist", EventCategory::Faction),
    ("whitelist-add", EventCategory::Faction),
    ("whitelist-remove", EventCategory::Faction),
    ("whitelist-wipe", EventCategory::Faction),
    // The wipe recorded itself with a SPACE before it was hyphenated to match the rest. Kept so
    // records already in the timeline categorise the same way they did.
    ("whitelist wipe", EventCategory::Faction),
    ("promotion", EventCategory::Faction),
    ("demotion", EventCategory::Faction),
    ("subclass", EventCategory::Faction),
    ("suspendrank", EventCategory::Faction),
    ("unsuspendrank", EventCategory::Faction),
    ("tester-add", EventCategory::Faction),
    ("tester-remove", EventCategory::Faction),
    // ---- server: things done TO a server rather than to a player ----
    ("auto-restart", EventCategory::Server),
    ("auto-restart-stopped", EventCategory::Server),
    ("manual", EventCategory::Server),
    ("rotatemap", EventCategory::Server),
    ("serverswitch", EventCategory::Server),
    ("restart", EventCategory::Server),
];

/// The category an audit action belongs to.
#[must_use]
pub fn category_of(action: Option<&str>) -> EventCategory {
    let Some(action) = action.map(str::trim).filter(|a| !a.is_empty()) else {
        return EventCategory::Staff;
    };
    CATEGORIES
        .iter()
        .find(|(name, _)| eq_ignore_case(name, action))
        .map_or(EventCategory::Staff, |(_, category)| *category)
}

/// The stable slug an audit action is stored under, PREFIXED WITH ITS CATEGORY so a kind read
/// out of a database browser says what it is, and two systems cannot collide on "restart".
#[must_use]
pub fn kind_of(action: Option<&str>) -> String {
    let name = action
        .map(str::trim)
        .filter(|a| !a.is_empty())
        .map_or_else(|| "unknown".to_owned(), to_lower_invariant);
    format!("{}.{name}", category_of(action).slug())
}

/// Whether an automated system produced this rather than a person. The staff-activity view is
/// about what PEOPLE did; counting the VPN responder's bans towards a moderator called "auto"
/// would put a phantom at the top of every leaderboard.
#[must_use]
pub fn is_automated(actor: Option<&str>) -> bool {
    actor
        .map(str::trim)
        .is_none_or(|a| a.is_empty() || eq_ignore_case(a, "auto") || eq_ignore_case(a, "system"))
}
