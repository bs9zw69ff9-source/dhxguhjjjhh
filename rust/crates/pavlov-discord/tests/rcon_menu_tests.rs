//! Port of `RconMenuTests.cs`: the exact RCON+ commands that grant and take back the staff menu.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_discord::rcon_menu::{
    HIGH_STAFF, HIGH_STAFF_MENU_ID, STAFF, STAFF_MENU_ID, grant, is_high_staff, revoke,
};

#[test]
fn granting_is_the_single_give_menu_command() {
    assert_eq!(
        grant("Pkdestroy", STAFF),
        [format!("GiveMenu Pkdestroy {STAFF_MENU_ID}")]
    );
    assert!(STAFF_MENU_ID.contains(' ')); // the space is part of the code
}

#[test]
fn the_bit_codes_are_the_ones_the_server_itself_sends() {
    assert_eq!(
        STAFF_MENU_ID,
        "0010010000000000101000000000000 10001000000000"
    );
    assert_eq!(
        HIGH_STAFF_MENU_ID,
        "0111110000000001101000000000010 11111111000010"
    );
}

#[test]
fn high_staff_is_the_fuller_code_not_extra_commands() {
    assert_eq!(
        grant("Pkdestroy", HIGH_STAFF),
        [format!("GiveMenu Pkdestroy {HIGH_STAFF_MENU_ID}")]
    );
}

#[test]
fn the_two_tiers_use_different_menus() {
    assert_ne!(STAFF_MENU_ID, HIGH_STAFF_MENU_ID);
    assert!(grant("X", HIGH_STAFF)[0].contains(HIGH_STAFF_MENU_ID));
    assert!(grant("X", STAFF)[0].contains(STAFF_MENU_ID));
}

#[test]
fn revoking_uses_remove_menu() {
    assert_eq!(revoke("Pkdestroy", false), ["RemoveMenu Pkdestroy"]);
}

#[test]
fn revoking_high_staff_takes_back_the_extras() {
    assert_eq!(
        revoke("Pkdestroy", true),
        [
            "RemoveMenu Pkdestroy",
            "RemoveMod Pkdestroy",
            "RemoveAccessManager Pkdestroy"
        ]
    );
}

#[test]
fn an_unknown_tier_is_treated_as_plain_staff() {
    assert!(!is_high_staff(Some("HIGHSTAFF ")));
    assert!(!is_high_staff(Some("mod")));
    assert!(!is_high_staff(None));
    assert!(is_high_staff(Some("HighStaff"))); // case only
}
