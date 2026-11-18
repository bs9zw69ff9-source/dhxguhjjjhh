//! Port of `LeaveTrackingTests.cs`: leave lines come from Pavlov's DISCONNECT line, not from
//! diffing the RCON roster, which on a live server produced nothing at all.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashMap;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_host::feeds::{join_line, leave_line};
use pavlov_host::logs::feed_bridge::report;
use pavlov_host::server_labels::{ServerLabels, label};

const WINDOW: TimeDelta = TimeDelta::seconds(20);

fn now() -> DateTime<Utc> {
    DateTime::UNIX_EPOCH + TimeDelta::days(56 * 365)
}

#[test]
fn one_disconnect_is_reported_once() {
    // One disconnect writes several close lines milliseconds apart.
    let mut seen = HashMap::new();
    let id = "76561198000000001";

    assert!(report(&mut seen, id, now(), WINDOW));
    assert!(!report(
        &mut seen,
        id,
        now() + TimeDelta::milliseconds(40),
        WINDOW
    ));
    assert!(!report(
        &mut seen,
        id,
        now() + TimeDelta::seconds(19),
        WINDOW
    ));
}

#[test]
fn a_rejoin_and_a_second_drop_are_both_reported() {
    // Somebody dropping repeatedly is exactly who staff want to see.
    let mut seen = HashMap::new();

    assert!(report(&mut seen, "abc", now(), WINDOW));
    assert!(report(
        &mut seen,
        "abc",
        now() + TimeDelta::seconds(21),
        WINDOW
    ));
}

#[test]
fn two_players_leaving_together_are_both_reported() {
    // Per ACCOUNT: a round ending drops everybody at once.
    let mut seen = HashMap::new();

    assert!(report(&mut seen, "alice", now(), WINDOW));
    assert!(report(&mut seen, "bob", now(), WINDOW));
}

#[test]
fn the_lines_read_the_way_the_node_bots_did() {
    assert!(join_line("Pkdestroy", Some("Server 1"), now()).ends_with("Pkdestroy joined Server 1"));
    assert!(leave_line("Pkdestroy", Some("Server 1"), now()).ends_with("Pkdestroy left Server 1"));
}

#[test]
fn an_unknown_server_is_not_guessed_at() {
    // "Server 1" for what is actually server 2 sends staff to the wrong place.
    assert!(join_line("Alice", None, now()).ends_with("joined the server"));
    assert!(leave_line("Alice", Some("   "), now()).ends_with("left the server"));
}

#[test]
fn a_leave_line_cannot_be_forged_by_a_player_name() {
    let line = leave_line(
        "Alice\n[2026-07-31 13:33:53] Admin left Server 1",
        Some("Server 1"),
        now(),
    );
    assert!(!line.contains('\n'));
}

#[test]
fn servers_are_numbered_by_discovery_order() {
    let logs = [
        "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log".to_owned(),
        "/home/steam/pavlov-ttt/Pavlov/Saved/Logs/Pavlov.log".to_owned(),
    ];

    assert_eq!(label(&logs, &logs[0]), "Server 1");
    // By ORDER, not folder name: the second install is rarely "…server2".
    assert_eq!(label(&logs, &logs[1]), "Server 2");
}

#[test]
fn an_unrecognised_log_is_not_numbered() {
    assert_eq!(label(&[], "/somewhere/else/Pavlov.log"), "the server");
}

#[test]
fn the_labels_are_live_once_assigned() {
    let labels = ServerLabels::default();
    assert_eq!(labels.of("/a/Pavlov.log"), "the server");

    labels.assign(vec!["/a/Pavlov.log".into(), "/b/Pavlov.log".into()]);

    assert_eq!(labels.of("/b/Pavlov.log"), "Server 2");
}
