//! Joins and flagged joins on the timeline (C# `PlayerEventBridge`; no C# test file).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_host::logs::player_events::server_of;

#[test]
fn the_server_is_the_install_that_holds_the_log() {
    assert_eq!(
        server_of("/home/steam/pavlovserver2/Pavlov/Saved/Logs/Pavlov.log").as_deref(),
        Some("pavlovserver2")
    );
    assert_eq!(server_of("Pavlov.log"), None);
    assert_eq!(server_of("  "), None);
}
