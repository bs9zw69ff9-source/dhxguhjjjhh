//! Port of `BanSpanOverflowTests.cs`: a number too big for a span must be UNREADABLE (`None`),
//! never fatal and never clamped. The ban importer parses the game's own banlist.txt, and one
//! malformed line that threw used to lose the whole batch.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::TimeDelta;
use pavlov_core::time::EasternTime;

#[test]
fn an_absurd_ban_span_is_unreadable_rather_than_fatal() {
    for input in [
        "99999999d",
        "99999999999999999999d",
        "999999999999w",
        "9999999999y",
        "999999999mo",
        "9223372036854775807d",
        "1d 99999999d",
    ] {
        assert_eq!(
            EasternTime::parse_ban_span(Some(input)),
            None,
            "input {input:?}"
        );
    }
}

#[test]
fn an_absurd_duration_is_unreadable_rather_than_fatal() {
    for input in [
        "99999999d",
        "9223372036854775807d",
        "99999999999999999999",
        "999999999h",
    ] {
        assert_eq!(
            EasternTime::parse_duration(Some(input)),
            None,
            "input {input:?}"
        );
    }
}

#[test]
fn a_compound_span_cannot_wrap_back_into_a_plausible_one() {
    assert_eq!(EasternTime::parse_ban_span(Some("99y 99y 99y")), None);
    assert_eq!(
        EasternTime::parse_ban_span(Some("50000d 50000d 50000d 50000d")),
        None
    );
}

#[test]
fn ordinary_ban_spans_are_unchanged() {
    for (input, minutes) in [
        ("1d", 1440),
        ("3d 4h", 4560),
        ("1w", 10080),
        ("1mo", 43200),
        ("1y", 525_600),
        ("30s", 0),
        ("2h 30m", 150),
    ] {
        let span = EasternTime::parse_ban_span(Some(input))
            .unwrap_or_else(|| panic!("{input:?} should parse"));
        assert_eq!(span.num_minutes(), minutes, "input {input:?}");
    }
}

#[test]
fn thirty_seconds_is_still_thirty_seconds() {
    assert_eq!(
        EasternTime::parse_ban_span(Some("30s")),
        Some(TimeDelta::seconds(30))
    );
}

#[test]
fn ordinary_durations_are_unchanged() {
    for (input, seconds) in [
        ("30s", 30),
        ("10m", 600),
        ("2h", 7200),
        ("1d", 86400),
        ("45", 2700),
    ] {
        assert_eq!(
            EasternTime::parse_duration(Some(input)),
            Some(TimeDelta::seconds(seconds)),
            "input {input:?}"
        );
    }
}

#[test]
fn the_ceiling_itself_is_accepted_and_one_step_past_it_is_not() {
    assert_eq!(
        EasternTime::parse_ban_span(Some("36500d")),
        Some(EasternTime::max_span())
    );
    assert_eq!(EasternTime::parse_ban_span(Some("36501d")), None);
}

#[test]
fn nonsense_is_still_just_null() {
    assert_eq!(EasternTime::parse_ban_span(Some("until friday")), None);
    assert_eq!(EasternTime::parse_ban_span(Some("2026-08-01")), None);
    assert_eq!(EasternTime::parse_ban_span(Some("")), None);
    assert_eq!(EasternTime::parse_ban_span(None), None);
    assert_eq!(EasternTime::parse_duration(Some("soon")), None);
}
