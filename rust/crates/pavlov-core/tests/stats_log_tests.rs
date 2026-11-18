//! Port of `StatsLogTests.cs`, class StatsLogReaderTests. The other two classes feed the
//! records into the kill feed and the score store and arrive with the log pipeline (M6).
//!
//! THE SAMPLE IS REAL, copied from a live server, and the assertions are pinned to it rather
//! than to what the format looked like it ought to be.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{TimeZone, Utc};
use pavlov_core::logs::{StatsKill, StatsLogReader, StatsRecord};

/// Verbatim from a running server, tabs and all.
const SAMPLE: &str = "[2026.09.11-19.45.16] StatManagerLog: {\n\
\t\"RoundState\":\n\
\t{\n\
\t\t\"State\": \"Starting\",\n\
\t\t\"Timestamp\": \"2026.09.11-15.45.16\"\n\
\t}\n\
}\n\
[2026.09.11-20.01.15] StatManagerLog: {\n\
\t\"KillData\":\n\
\t{\n\
\t\t\"Killer\": \"Holosight1\",\n\
\t\t\"Killed\": \"LxPXHam\",\n\
\t\t\"KilledBy\": \"10mmpistol\",\n\
\t\t\"Headshot\": false\n\
\t}\n\
}\n\
[2026.09.11-20.01.29] StatManagerLog: {\n\
\t\"KillData\":\n\
\t{\n\
\t\t\"Killer\": \"Holosight1\",\n\
\t\t\"Killed\": \"Holosight1\",\n\
\t\t\"KilledBy\": \"10mmpistol\",\n\
\t\t\"Headshot\": true\n\
\t}\n\
}\n\
[2026.09.11-20.30.19] StatManagerLog: {\n\
\t\"KillData\":\n\
\t{\n\
\t\t\"Killer\": \"LxPXHam\",\n\
\t\t\"Killed\": \"LxPXHam\",\n\
\t\t\"KilledBy\": \"None\",\n\
\t\t\"Headshot\": false\n\
\t}\n\
}\n";

fn read_all(text: &str, reader: &mut StatsLogReader) -> Vec<StatsRecord> {
    text.split('\n')
        .filter_map(|line| reader.read(line))
        .collect()
}

fn records(text: &str) -> Vec<StatsRecord> {
    read_all(text, &mut StatsLogReader::new())
}

fn kills(text: &str) -> Vec<StatsKill> {
    records(text)
        .into_iter()
        .filter_map(|r| match r {
            StatsRecord::Kill(k) => Some(k),
            StatsRecord::Round(_) => None,
        })
        .collect()
}

#[test]
fn the_sample_parses_into_one_round_state_and_three_kills() {
    let all = records(SAMPLE);

    assert_eq!(all.len(), 4);
    assert!(matches!(all[0], StatsRecord::Round(_)));
    assert_eq!(kills(SAMPLE).len(), 3);
}

#[test]
fn a_kill_carries_the_weapon_the_headshot_flag_and_its_own_timestamp() {
    let kill = kills(SAMPLE).remove(0);

    assert_eq!(kill.killer, "Holosight1");
    assert_eq!(kill.killed, "LxPXHam");
    assert_eq!(kill.weapon.as_deref(), Some("10mmpistol"));
    assert!(!kill.headshot);
    assert!(!kill.suicide());
    // THE POINT OF USING THIS FILE: the kill's own time, not when the poll noticed it.
    assert_eq!(
        kill.at,
        Utc.with_ymd_and_hms(2026, 9, 11, 20, 1, 15).unwrap()
    );
}

#[test]
fn the_bracket_stamp_is_read_as_utc() {
    /* The RoundState block's own "Timestamp" is local (15.45.16, Eastern in September); the
    bracket (19.45.16) is UTC and does not move twice a year. */
    let StatsRecord::Round(round) = records(SAMPLE).remove(0) else {
        panic!("the first record is the round state");
    };
    assert_eq!(round.state, "Starting");
    assert_eq!(
        round.at,
        Utc.with_ymd_and_hms(2026, 9, 11, 19, 45, 16).unwrap()
    );
}

#[test]
fn a_self_kill_is_marked_a_suicide() {
    let kill = &kills(SAMPLE)[1];
    assert!(kill.suicide());
    assert!(kill.headshot);
    assert_eq!(kill.weapon.as_deref(), Some("10mmpistol"));
}

#[test]
fn killed_by_none_means_no_weapon() {
    // A fall, a drown, the map - not a weapon called None.
    let last = kills(SAMPLE).pop().unwrap();
    assert!(last.suicide());
    assert!(last.weapon.is_none());
}

#[test]
fn a_block_split_across_two_polls_is_still_read() {
    // The reason this is a state machine: a poll routinely lands in the middle of a block.
    let mut reader = StatsLogReader::new();
    let lines: Vec<&str> = SAMPLE.split('\n').collect();

    let first = lines[..10].iter().filter_map(|l| reader.read(l)).count();
    let second = lines[10..].iter().filter_map(|l| reader.read(l)).count();

    assert_eq!(first + second, 4);
}

#[test]
fn an_unclosed_block_is_dropped_when_the_next_one_starts() {
    // Rotation mid-block: the old block's closing brace never arrives.
    let mut reader = StatsLogReader::new();
    let text = format!(
        "[2026.09.11-20.01.15] StatManagerLog: {{\n\t\"KillData\":\n\t{{\n\t\t\"Killer\": \"Holosight1\",\n{SAMPLE}"
    );

    let all = read_all(&text, &mut reader);

    assert_eq!(reader.discarded(), 1);
    assert_eq!(all.len(), 4); // the sample still parses in full
}

#[test]
fn a_malformed_block_is_skipped_rather_than_throwing() {
    let text = format!(
        "[2026.09.11-20.01.15] StatManagerLog: {{\n\t\"KillData\": not json at all\n}}\n{SAMPLE}"
    );
    assert_eq!(records(&text).len(), 4);
}

#[test]
fn a_kill_missing_a_name_is_not_reported() {
    assert!(
        records(
            "[2026.09.11-20.01.15] StatManagerLog: {\n\t\"KillData\":\n\t{\n\t\t\"Killed\": \"LxPXHam\",\n\t\t\"KilledBy\": \"10mmpistol\"\n\t}\n}\n"
        )
        .is_empty()
    );
}

#[test]
fn an_unknown_record_type_is_ignored_quietly() {
    assert!(
        records(
            "[2026.09.11-20.01.15] StatManagerLog: {\n\t\"SomethingElse\":\n\t{\n\t\t\"Whatever\": 1\n\t}\n}\n"
        )
        .is_empty()
    );
}

#[test]
fn lines_outside_a_block_are_ignored() {
    assert!(records("some other log line\n\t\"Killer\": \"Nobody\"\n}\n").is_empty());
}
