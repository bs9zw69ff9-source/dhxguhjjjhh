//! Port of `KillLineTests.cs`: the kill feed line. Pavlov.log carries no headshot flag, so the
//! headshot half is fed only by Stats.log.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{DateTime, TimeZone, Utc};
use pavlov_host::feeds::kill_line;

fn at() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 9, 11, 20, 1, 15).unwrap()
}

#[test]
fn an_ordinary_kill_names_both_players_and_the_weapon() {
    let line = kill_line(
        Some("Holosight1"),
        "LxPXHam",
        Some("10mmpistol"),
        at(),
        false,
    );

    assert!(line.contains("Holosight1 → LxPXHam"));
    assert!(line.contains("(10mmpistol)"));
    assert!(!line.contains("headshot"));
}

#[test]
fn a_headshot_says_so() {
    let line = kill_line(
        Some("Holosight1"),
        "LxPXHam",
        Some("10mmpistol"),
        at(),
        true,
    );
    assert!(line.contains("(10mmpistol, headshot)"));
}

#[test]
fn a_suicide_is_not_an_arrow() {
    // "Name → Name" reads like a parser fault every single time.
    let line = kill_line(
        Some("Holosight1"),
        "Holosight1",
        Some("10mmpistol"),
        at(),
        true,
    );

    assert!(!line.contains('→'));
    assert!(line.contains("Holosight1 killed themselves"));
    assert!(line.contains("headshot"));
}

#[test]
fn a_self_kill_with_no_weapon_is_a_death_not_a_suicide() {
    // A fall or a drowning. "Killed themselves" is the wrong words for walking off a roof.
    let line = kill_line(Some("LxPXHam"), "LxPXHam", None, at(), false);

    assert!(line.contains("LxPXHam died"));
    assert!(!line.contains("themselves"));
}

#[test]
fn a_kill_with_no_killer_is_the_world() {
    assert!(kill_line(None, "LxPXHam", None, at(), false).contains("the world → LxPXHam"));
}
