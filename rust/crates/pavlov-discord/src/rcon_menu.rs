//! The in-game staff menu granted from Discord (C# `RconMenu.cs`): `/menu` and the menu
//! claim panel. A master gets the bare `GiveMenu` (see
//! [`pavlov_host::moderation::master_access::grant_commands`]); staff get a tier's bitcode.

use pavlov_core::text::case::eq_ignore_case;

/// The staff tier's menu. POSITIONAL: a changed character silently grants a DIFFERENT set of
/// buttons rather than failing, so each is pinned verbatim from a working grant on a live
/// server. The space is part of the code.
pub const STAFF_MENU_ID: &str = "0010010000000000101000000000000 10001000000000";
/// High staff: one command too, a fuller bitcode carrying the mod and access-manager powers.
pub const HIGH_STAFF_MENU_ID: &str = "0111110000000001101000000000010 11111111000010";

pub const STAFF: &str = "staff";
pub const HIGH_STAFF: &str = "highstaff";

/// Only an exact (case-insensitive) `highstaff`: a typo must never hand out moderator powers.
#[must_use]
pub fn is_high_staff(tier: Option<&str>) -> bool {
    tier.is_some_and(|t| eq_ignore_case(t, HIGH_STAFF))
}

#[must_use]
pub fn menu_id(tier: Option<&str>) -> &'static str {
    if is_high_staff(tier) {
        HIGH_STAFF_MENU_ID
    } else {
        STAFF_MENU_ID
    }
}

/// The single `GiveMenu` that grants a tier. The `AddMod`/`AddAccessManager` the Node bot
/// bolted on are not sent: the bitcode carries those powers itself.
#[must_use]
pub fn grant(player: &str, tier: &str) -> Vec<String> {
    vec![format!("GiveMenu {player} {}", menu_id(Some(tier)))]
}

/// `RemoveMenu` ("StripMenu" is not an RCON+ verb: it appeared to work and left the menu),
/// and for high staff the extras too, or they keep moderator powers after losing the menu.
#[must_use]
pub fn revoke(player: &str, was_high_staff: bool) -> Vec<String> {
    pavlov_host::moderation::master_access::revoke_commands(player, was_high_staff)
}
