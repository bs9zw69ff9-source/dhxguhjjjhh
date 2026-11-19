//! Port of `TempBanExpiryTests.cs`: the time left on a ban, as staff are shown it. "0m" was
//! once written for a ban in its last minute. The ban-file import tests (`denotes_elapsed`) are
//! dropped with the import (owner, 2026-11-19: bans live in the player's save).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{TimeDelta, Utc};
use pavlov_core::time::EasternTime;

#[test]
fn a_ban_with_seconds_left_is_never_written_as_zero() {
    let now = Utc::now();
    for seconds in [50, 1] {
        let written = EasternTime::time_left(now + TimeDelta::seconds(seconds), now);
        assert_ne!(written, "0m");
        assert_eq!(written, "1m");
    }
}

#[test]
fn longer_spans_are_unchanged() {
    let now = Utc::now();
    assert_eq!(
        EasternTime::time_left(now + TimeDelta::minutes(5), now),
        "5m"
    );
    assert_eq!(
        EasternTime::time_left(now + TimeDelta::minutes(150), now),
        "2h 30m"
    );
    assert_eq!(
        EasternTime::time_left(now - TimeDelta::minutes(1), now),
        "expired"
    );
}

#[test]
fn permanent_is_still_permanent() {
    assert_eq!(EasternTime::parse_ban_span(Some("Permanent")), None);
}
