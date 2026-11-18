//! Port of `RosterAuditTests.cs`: roster changes are faction events, and which staff log
//! channel each one lands in.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::events::EventCategory;
use pavlov_core::events::mapping::category_of;
use pavlov_discord::staff_log::{StaffLogChannels, channel_for};

fn all() -> StaffLogChannels {
    StaffLogChannels {
        moderation: Some(100),
        ban: Some(200),
        police: Some(300),
        arrest: Some(400),
    }
}

#[test]
fn a_roster_action_is_a_faction_event() {
    for action in [
        "whitelist-add",
        "whitelist-remove",
        "whitelist-wipe",
        "promotion",
        "demotion",
        "subclass",
    ] {
        assert_eq!(
            category_of(Some(action)),
            EventCategory::Faction,
            "{action}"
        );
    }
}

#[test]
fn the_wipes_old_spaced_name_still_categorises_the_same_way() {
    assert_eq!(category_of(Some("whitelist wipe")), EventCategory::Faction);
}

#[test]
fn a_whitelist_change_goes_to_the_mod_log_channel() {
    for action in ["whitelist-add", "whitelist-remove", "whitelist-wipe"] {
        assert_eq!(channel_for(action, &all()), Some(100), "{action}");
    }
}

#[test]
fn a_rank_change_goes_to_the_police_log_channel_instead() {
    for action in ["promotion", "demotion", "subclass"] {
        assert_eq!(channel_for(action, &all()), Some(300), "{action}");
    }
}

#[test]
fn with_only_a_ban_channel_set_it_still_lands_somewhere() {
    let channels = StaffLogChannels::new(None, Some(200));
    assert_eq!(channel_for("whitelist-add", &channels), Some(200));
}

#[test]
fn with_no_channels_configured_it_goes_nowhere_rather_than_throwing() {
    assert_eq!(
        channel_for("whitelist-add", &StaffLogChannels::default()),
        None
    );
}
