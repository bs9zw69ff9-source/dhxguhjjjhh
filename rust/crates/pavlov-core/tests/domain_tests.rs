//! Port of `dotnet/tests/PavlovBot.Tests/DomainTests.cs`. One Rust module per C# test class,
//! one Rust test per C# test method (snake_case), `[InlineData]` rows as table loops.

mod sanitize_tests {
    use pavlov_core::text::{NameLabels, Sanitize};

    fn id(s: &str) -> String {
        Sanitize::id(Some(s))
    }

    #[test]
    fn an_id_cannot_carry_a_space_or_a_newline() {
        // RCON commands are space-delimited with NO quoting: a space is command injection.
        assert_eq!(id("Kick everyone"), "Kickeveryone");
        assert_eq!(id("a\nBan"), "aBan");
    }

    #[test]
    fn only_the_allowed_alphabet_survives() {
        assert_eq!(id("Player_1-2.3"), "Player_1-2.3");
        assert_eq!(id("<@&123>drop;\"'`$()"), "123drop");
    }

    #[test]
    fn the_bots_own_autocomplete_labels_are_stripped_back() {
        assert_eq!(id("Alice (manual entry)"), "Alice");
        assert_eq!(id("Bob (offline)"), "Bob");
        assert_eq!(id("Carol [s1+s2]"), "Carol");
    }

    #[test]
    fn every_label_the_bot_can_add_is_one_it_can_take_back_off() {
        // The two lists drifted once and banned "Aliceonline", a player who does not exist.
        for label in NameLabels::ALL {
            assert_eq!(
                id(&NameLabels::decorate("Alice", Some(label))),
                "Alice",
                "label {label}"
            );
        }
    }

    #[test]
    fn a_name_wearing_two_labels_loses_both() {
        assert_eq!(id("Alice (online) (manual entry)"), "Alice");
    }

    #[test]
    fn a_name_with_no_label_is_untouched() {
        assert_eq!(id(&NameLabels::decorate("Alice", None)), "Alice");
        assert_eq!(id(&NameLabels::decorate("Alice", Some(""))), "Alice");
    }

    #[test]
    fn ids_are_length_capped() {
        assert_eq!(id(&"a".repeat(200)).len(), 64);
    }

    #[test]
    fn a_message_keeps_its_words_but_loses_its_line_breaks() {
        assert_eq!(Sanitize::message(Some("hello\nthere")), "hello there");
        assert_eq!(Sanitize::message(Some("a\tb\rc")), "a b c");
    }

    #[test]
    fn non_printables_are_dropped_from_messages() {
        assert_eq!(Sanitize::message(Some("h\0i\u{200B}")), "hi");
    }

    #[test]
    fn private_detail_is_scrubbed_from_public_text() {
        let cases = [
            (
                "connect from 192.168.1.50 ok",
                "connect from [ip redacted] ok",
            ),
            ("peer 2001:db8::1 closed", "peer [ip redacted] closed"),
            (
                "user 123456789012345678 joined",
                "user [id redacted] joined",
            ),
            ("read /home/pavlov/Saved/x", "read [path redacted]/Saved/x"),
            ("read /root/secrets", "read [path redacted]/secrets"),
            (
                "Ban evasion - blacklisted ip 73.164.223.3",
                "Ban evasion - blacklisted ip [ip redacted]",
            ),
            (
                "Ban evasion - blacklisted account 00020322e7bc4c6a9b5f83ae6e6b1ed5",
                "Ban evasion - blacklisted account [id redacted]",
            ),
        ];
        for (input, expected) in cases {
            assert_eq!(
                Sanitize::redact_private(Some(input)),
                expected,
                "input {input:?}"
            );
        }
    }

    #[test]
    fn harmless_text_survives_redaction() {
        assert_eq!(
            Sanitize::redact_private(Some("fixed in a3fdaab (v1.2.3)")),
            "fixed in a3fdaab (v1.2.3)"
        );
    }

    #[test]
    fn a_backtick_in_a_name_cannot_escape_a_code_span() {
        assert_eq!(Sanitize::code(Some("ev`il")), "ev'il");
    }
}

mod eastern_time_tests {
    use chrono::{DateTime, TimeDelta, TimeZone, Utc};
    use pavlov_core::time::{BanDurations, EasternTime};

    fn winter() -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 1, 15, 17, 30, 0).unwrap() // EST, UTC-5
    }

    fn summer() -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 7, 15, 17, 30, 0).unwrap() // EDT, UTC-4
    }

    #[test]
    fn timestamps_are_eastern_and_dst_aware() {
        assert_eq!(EasternTime::stamp(Some(winter())), "2026-01-15 12:30:00");
        assert_eq!(EasternTime::stamp(Some(summer())), "2026-07-15 13:30:00");
    }

    #[test]
    fn an_absent_timestamp_is_unknown_not_the_epoch() {
        assert_eq!(EasternTime::stamp(None), "unknown");
        assert_eq!(EasternTime::stamp(Some(DateTime::UNIX_EPOCH)), "unknown");
    }

    #[test]
    fn the_date_is_the_eastern_calendar_day_not_the_utc_one() {
        let late = Utc.with_ymd_and_hms(2026, 1, 16, 1, 30, 0).unwrap();
        assert_eq!(EasternTime::date(Some(late)), "2026-01-15");
    }

    #[test]
    fn time_left_uses_two_units_and_never_three() {
        let now = winter();
        let at = |d| now + d;
        assert_eq!(
            EasternTime::time_left(
                at(TimeDelta::days(3) + TimeDelta::hours(4) + TimeDelta::minutes(17)),
                now
            ),
            "3d 4h"
        );
        assert_eq!(
            EasternTime::time_left(at(TimeDelta::hours(5) + TimeDelta::minutes(20)), now),
            "5h 20m"
        );
        assert_eq!(
            EasternTime::time_left(at(TimeDelta::minutes(12)), now),
            "12m"
        );
        assert_eq!(
            EasternTime::time_left(at(TimeDelta::minutes(-1)), now),
            "expired"
        );
    }

    #[test]
    fn simple_durations_parse() {
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
    fn unparseable_durations_are_null() {
        for input in ["", "0", "soon", "2 weeks"] {
            assert_eq!(
                EasternTime::parse_duration(Some(input)),
                None,
                "input {input:?}"
            );
        }
    }

    #[test]
    fn month_is_matched_before_minute() {
        assert_eq!(
            EasternTime::parse_ban_span(Some("1mo")),
            Some(TimeDelta::days(30))
        );
        assert_eq!(
            EasternTime::parse_ban_span(Some("1m")),
            Some(TimeDelta::minutes(1))
        );
    }

    #[test]
    fn compound_ban_spans_sum() {
        assert_eq!(
            EasternTime::parse_ban_span(Some("3d 4h")),
            Some(TimeDelta::days(3) + TimeDelta::hours(4))
        );
        assert_eq!(
            EasternTime::parse_ban_span(Some("1w")),
            Some(TimeDelta::days(7))
        );
        assert_eq!(
            EasternTime::parse_ban_span(Some("1y")),
            Some(TimeDelta::days(365))
        );
    }

    #[test]
    fn calendar_dates_and_words_are_refused_never_guessed_at() {
        for input in ["until friday", "2026-08-01", "next week", "3d until monday"] {
            assert_eq!(
                EasternTime::parse_ban_span(Some(input)),
                None,
                "input {input:?}"
            );
        }
    }

    #[test]
    fn clock_times_normalise_to24_hour() {
        let cases = [
            ("18:30", "18:30"),
            ("6:30pm", "18:30"),
            ("3pm", "15:00"),
            ("12am", "00:00"),
            ("12pm", "12:00"),
            ("0:00", "00:00"),
        ];
        for (input, expected) in cases {
            assert_eq!(
                EasternTime::parse_clock_time(Some(input)).as_deref(),
                Some(expected),
                "input {input:?}"
            );
        }
    }

    #[test]
    fn impossible_clock_times_are_null() {
        for input in ["25:00", "6:75", "13pm", "0pm"] {
            assert_eq!(
                EasternTime::parse_clock_time(Some(input)),
                None,
                "input {input:?}"
            );
        }
    }

    #[test]
    fn every_picker_duration_resolves() {
        for (value, length) in BanDurations::all() {
            assert_eq!(
                BanDurations::length(Some(value)),
                Some(length),
                "value {value}"
            );
        }
        assert_eq!(BanDurations::length(Some("1d")), Some(TimeDelta::days(1)));
    }

    #[test]
    fn permanent_and_nonsense_both_resolve_to_null_and_the_caller_must_tell_them_apart() {
        assert_eq!(BanDurations::length(Some(BanDurations::PERMANENT)), None);
        assert_eq!(BanDurations::length(Some("whenever")), None);
    }

    #[test]
    fn a_typed_span_works_where_a_pick_does() {
        assert_eq!(
            BanDurations::length(Some("3d 4h")),
            Some(TimeDelta::days(3) + TimeDelta::hours(4))
        );
    }
}
