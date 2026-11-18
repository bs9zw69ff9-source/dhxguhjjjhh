//! Port of `RotateMapTests.cs`, the player-count half: which unit states read as Offline, and
//! the offline name's format. The unit names are in `pavlov-core`, systemd in `pavlov-host`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_discord::player_count::{is_offline, offline_name, server_name};
use pavlov_host::service_control::UnitState;

#[test]
fn only_a_definitely_down_unit_becomes_offline() {
    for (state, offline) in [
        (UnitState::Inactive, true),
        (UnitState::Failed, true),
        (UnitState::Active, false),
        (UnitState::Starting, false),
        (UnitState::Unknown, false),
    ] {
        assert_eq!(is_offline(state), offline, "{state:?}");
    }
}

#[test]
fn the_offline_name_matches_the_counted_name_format() {
    assert_eq!(offline_name(2), "Server 2: Offline");
    assert_eq!(server_name(1, 3, 24), "Server 1: 3/24");
    assert!(offline_name(3).starts_with("Server 3:"));
}
