//! Port of `TempBanExpiryTests.cs`: a temp ban must not come back from the ban file as a
//! permanent one. "0m" was written for a ban in its last minute, read back as unparseable,
//! and imported as PERMANENT.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{TimeDelta, Utc};
use pavlov_core::time::EasternTime;
use pavlov_host::moderation::ban_file::denotes_elapsed;

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
fn an_unban_value_with_no_time_left_is_recognised_as_elapsed() {
    for unban in ["0m", "0", "00h", "expired", " 0m "] {
        assert!(
            denotes_elapsed(Some(unban)),
            "\"{unban}\" means the ban has served its time"
        );
    }
}

#[test]
fn everything_else_is_left_to_the_normal_parser() {
    for unban in [
        Some("Permanent"),
        Some("1m"),
        Some("3d 4h"),
        Some("30s"),
        Some(""),
        None,
    ] {
        assert!(!denotes_elapsed(unban), "{unban:?} is not an elapsed span");
    }
}

#[test]
fn permanent_is_still_permanent() {
    assert!(!denotes_elapsed(Some("Permanent")));
    assert_eq!(EasternTime::parse_ban_span(Some("Permanent")), None);
}
