//! Port of `ConfirmedOnlineTests.cs`: who may be PAID against. A frozen roster from a crashed
//! server is excluded, as is one never refreshed.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use pavlov_host::registry::{RosterSnapshot, confirmed_online};
use pavlov_rcon::reply::PavlovPlayer;

fn now() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 1, 12, 0, 0).unwrap()
}

fn snapshot(server: &str, age: TimeDelta, names: &[&str]) -> RosterSnapshot {
    RosterSnapshot {
        server: server.into(),
        players: names
            .iter()
            .map(|n| PavlovPlayer {
                name: (*n).into(),
                unique_id: format!("id-{n}"),
            })
            .collect(),
        taken_at: Some(now() - age),
    }
}

#[test]
fn a_frozen_roster_from_a_crashed_server_is_excluded() {
    let rosters = [
        snapshot("server1", TimeDelta::seconds(20), &["Alice"]),
        snapshot("server2", TimeDelta::minutes(30), &["Bob"]), // stopped answering
    ];
    assert_eq!(confirmed_online(&rosters, now()), ["Alice"]);
}

#[test]
fn a_never_refreshed_roster_is_excluded() {
    assert!(confirmed_online(&[RosterSnapshot::empty("server1")], now()).is_empty());
}

#[test]
fn names_are_deduplicated_across_servers_and_blanks_dropped() {
    let rosters = [
        snapshot("server1", TimeDelta::zero(), &["Alice", ""]),
        snapshot("server2", TimeDelta::zero(), &["alice"]),
    ];
    assert_eq!(confirmed_online(&rosters, now()).len(), 1);
}
