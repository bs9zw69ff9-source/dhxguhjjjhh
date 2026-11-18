//! Port of `LogParsingTests.cs`: PavlovLogTests, JoinCorrelatorTests, FlagMatchingTests and
//! PendingFlagTests. The three RCON embed tests in PavlovLogTests build Discord embeds and
//! arrive with `pavlov-discord` (M7).
//!
//! Every pattern was validated against real Pavlov logs. A parser that stops matching after a
//! game update does not fail loudly: the log still reads, it just stops finding anyone.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod pavlov_log_tests {
    use chrono::{TimeZone, Utc};
    use pavlov_core::logs::pavlov_log::{
        accepted_address, banned_by_rcon, clean_id, confirmed, is_ipv4, is_placeholder_id, kill,
        login, rcon, rcon_plus_command, rcon_result, timestamp_of, unbanned_by_rcon,
    };

    #[test]
    fn the_lines_own_timestamp_is_read() {
        let at = timestamp_of("[2026.07.15-18.30.45:123]LogNet: something");
        assert_eq!(
            at,
            Some(
                Utc.with_ymd_and_hms(2026, 7, 15, 18, 30, 45).unwrap()
                    + chrono::TimeDelta::milliseconds(123)
            )
        );
    }

    #[test]
    fn a_line_with_no_or_a_corrupt_timestamp_does_not_stop_the_parse() {
        assert!(timestamp_of("LogNet: no timestamp here").is_none());
        assert!(timestamp_of("[2026.13.45-99.99.99:999]LogNet: corrupt").is_none());
    }

    #[test]
    fn an_accepted_connection_yields_its_address() {
        assert_eq!(
            accepted_address(
                "[2026.07.15-18.30.45:123]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777"
            )
            .as_deref(),
            Some("203.0.113.5")
        );
        assert_eq!(
            accepted_address(
                "LogNet: AddClientConnection: Added client connection RemoteAddr: 203.0.113.6:7777"
            )
            .as_deref(),
            Some("203.0.113.6")
        );
    }

    #[test]
    fn a_confirmed_pairing_needs_both_fields_on_one_line() {
        // This pairing is CERTAIN and is the only thing allowed to flag an address.
        let pairing = confirmed(
            "LogNet: UChannel::Close: UniqueId: EOS:0002abc123 RemoteAddr: 203.0.113.5:7777",
        )
        .unwrap();

        assert_eq!(pairing.ip, "203.0.113.5");
        assert_eq!(pairing.id, "0002abc123");
    }

    #[test]
    fn confirmation_is_field_order_independent() {
        let pairing = confirmed("LogNet: RemoteAddr: 203.0.113.5:7777, UniqueId: EOS:0002abc123");
        assert_eq!(pairing.unwrap().id, "0002abc123");
    }

    #[test]
    fn one_field_alone_is_not_a_confirmation() {
        assert!(confirmed("LogNet: RemoteAddr: 203.0.113.5:7777").is_none());
        assert!(confirmed("LogNet: UniqueId: EOS:0002abc123").is_none());
    }

    #[test]
    fn a_login_request_yields_the_name_and_the_id() {
        let request = login(
            "[2026.07.15-18.30.45:123]LogNet: Login request: ?Name=Alice userId: EOS:0002abc123 platform: EOS",
        )
        .unwrap();

        assert_eq!(request.name.as_deref(), Some("Alice"));
        assert_eq!(request.id, "0002abc123");
    }

    #[test]
    fn a_name_ends_before_the_following_field_even_with_no_separator() {
        /* Real lines are `?Name=Bob userId: EOS:...` with no ? or & before userId, so a lazy
        match to the next option alone would swallow the id into the name. */
        let request = login("LogNet: Join request: ?Name=Bob Smith userId: EOS:0002abc").unwrap();
        assert_eq!(request.name.as_deref(), Some("Bob Smith"));
        assert_eq!(request.id, "0002abc");
    }

    #[test]
    fn a_login_without_a_name_still_links_the_account() {
        let request = login("LogNet: Login request: userId: EOS:0002abc123").unwrap();
        assert!(request.name.is_none());
        assert_eq!(request.id, "0002abc123");
    }

    #[test]
    fn a_line_that_is_not_a_login_is_ignored_even_with_an_id_on_it() {
        assert!(login("LogNet: some other line userId: EOS:0002abc").is_none());
    }

    #[test]
    fn ids_are_normalised() {
        /* The strip is not cosmetic: NetworkFailure lines quote the id, and an id with a stray
        apostrophe does not match the same account seen anywhere else. */
        for (raw, expected) in [
            ("EOS:0002abc123", "0002abc123"),
            ("NULL:abc123", "abc123"),
            ("0002abc123", "0002abc123"),
            ("EOS:0002abc123'", "0002abc123"),
        ] {
            assert_eq!(clean_id(Some(raw)), expected, "{raw}");
        }
    }

    #[test]
    fn placeholder_ids_identify_nobody() {
        for id in [Some("INVALID"), Some("localhost-abc"), Some(""), None] {
            assert!(is_placeholder_id(id), "{id:?}");
        }
    }

    #[test]
    fn rcon_and_rcon_plus_commands_are_recognised() {
        assert_eq!(
            banned_by_rcon("LogTemp: Rcon: BanPlayer Alice").as_deref(),
            Some("Alice")
        );
        assert_eq!(
            unbanned_by_rcon("LogTemp: Rcon: UnbanPlayer Alice").as_deref(),
            Some("Alice")
        );
        assert_eq!(
            rcon_plus_command("LogTemp: Warning: Rcon Plus Command Executed: GiveMenu SomePlayer")
                .as_deref(),
            Some("GiveMenu SomePlayer")
        );
        // The Error level is just how the mod prints, not a failure.
        assert_eq!(
            rcon_plus_command("LogTemp: Error: Rcon Plus Command Executed: Ban Bob").as_deref(),
            Some("Ban Bob")
        );
    }

    /// The unified audit parser, pinned to real lines from a live Pavlov.log.
    #[test]
    fn the_rcon_audit_parser_classifies_real_lines() {
        // Base RCON: verb + argument, NO instigator. `BanPlayer` maps back to the sent `Ban`.
        let ban =
            rcon("[2026.09.16-04.08.14:875][471]LogTemp: Rcon: BanPlayer hhhhhhhhhh").unwrap();
        assert!(!ban.plus);
        assert_eq!(ban.verb, "Ban");
        assert!(ban.instigator.is_none());
        assert_eq!(ban.argument.as_deref(), Some("hhhhhhhhhh"));
        assert_eq!(ban.command(), "Ban hhhhhhhhhh");

        let kick = rcon(
            "[2026.09.16-05.00.00:000][ 10]LogTemp: Rcon: KickPlayer 0002980854f84fbcbc37a8f948a79c3a",
        )
        .unwrap();
        assert_eq!(kick.verb, "Kick");
        assert!(kick.instigator.is_none());
        assert_eq!(
            kick.argument.as_deref(),
            Some("0002980854f84fbcbc37a8f948a79c3a")
        );

        assert!(rcon("[t][0]LogTemp: Rcon: ModeratorList").is_none()); // a read-only poll

        // RCON+: the acting player is logged first, so it is the instigator.
        let warp = rcon(
            "[2026.09.16-20.37.50:372][ 16]LogTemp: Warning: Rcon Plus Command Executed: Warp Holosight1 ricely",
        )
        .unwrap();
        assert!(warp.plus);
        assert_eq!(warp.verb, "Warp");
        assert_eq!(warp.instigator.as_deref(), Some("Holosight1"));
        assert_eq!(warp.argument.as_deref(), Some("ricely"));
        assert_eq!(warp.command(), "Warp ricely");
        // Full rejoins everything: it is what a sent command is confirmed against.
        assert_eq!(warp.full(), "Warp Holosight1 ricely");
        assert_eq!(ban.full(), "Ban hhhhhhhhhh");

        // An RCON+ command genuinely named BanPlayer is NOT remapped.
        let plus_ban = rcon(
            "[t][0]LogTemp: Warning: Rcon Plus Command Executed: BanPlayer Holosight1 griefer99",
        )
        .unwrap();
        assert!(plus_ban.plus);
        assert_eq!(plus_ban.verb, "BanPlayer");
        assert_eq!(plus_ban.instigator.as_deref(), Some("Holosight1"));

        // Self-service menu action: the instigator gives THEMSELVES cash.
        let cash = rcon(
            "[t][0]LogTemp: Warning: Rcon Plus Command Executed: SetCash Rickythegamer1001 9999999",
        )
        .unwrap();
        assert_eq!(cash.instigator.as_deref(), Some("Rickythegamer1001"));
        assert_eq!(cash.command(), "SetCash 9999999");

        // A server-wide verb has no acting player.
        let clean =
            rcon("[t][0]LogTemp: Warning: Rcon Plus Command Executed: CleanUp Items").unwrap();
        assert!(clean.instigator.is_none());
        assert_eq!(clean.command(), "CleanUp Items");
    }

    #[test]
    fn the_rcon_result_line_parses_pass_and_fail() {
        assert_eq!(
            rcon_result("Successful: true        Instigated By: Holosight1"),
            Some(true)
        );
        assert_eq!(
            rcon_result("Successful: false        Instigated By: Console"),
            Some(false)
        );
        assert!(
            rcon_result("[t][0]LogTemp: Warning: Rcon Plus Command Executed: Warp Bob ricely")
                .is_none()
        );
        assert!(rcon_result("[t][0]LogTemp: Rcon: BanPlayer Alice").is_none());
    }

    #[test]
    fn rcon_noise_and_non_rcon_lines_are_dropped() {
        for line in [
            "[t][0]LogTemp: Rcon: User authenticated 216.21.5.196:55846",
            "[t][0]LogTemp: Rcon: Connection is blocked due to failed login attempts!",
            "[t][0]LogTemp: Rcon: RefreshList",
            "[t][0]LogTemp: Rcon: ServerInfo",
            "[t][0]LogTemp: Rcon: UGCModList",
            "[t][0]LogNet: NotifyAcceptingConnection accepted from: 47.13.242.106:34870",
        ] {
            assert!(rcon(line).is_none(), "{line}");
        }
    }

    #[test]
    fn a_kill_record_is_read_whether_whole_or_split() {
        let whole =
            kill(r#"LogTemp: KillData {"Killer": "Alice", "Killed": "Bob", "KilledBy": "AK47"}"#)
                .unwrap();
        assert_eq!(whole.killer.as_deref(), Some("Alice"));
        assert_eq!(whole.killed.as_deref(), Some("Bob"));
        assert_eq!(whole.killed_by.as_deref(), Some("AK47"));

        let partial = kill(r#"    "Killer": "Alice","#).unwrap();
        assert_eq!(partial.killer.as_deref(), Some("Alice"));
        assert!(partial.killed.is_none());
    }

    #[test]
    fn addresses_are_octet_bounded() {
        for (value, valid) in [
            ("203.0.113.5", true),
            ("255.255.255.255", true),
            ("999.1.1.1", false),
            ("203.0.113", false),
            ("not an ip", false),
        ] {
            assert_eq!(is_ipv4(Some(value)), valid, "{value}");
        }
    }
}

mod join_correlator_tests {
    use chrono::{DateTime, TimeDelta, TimeZone, Utc};
    use pavlov_core::logs::JoinCorrelator;

    const FILE: &str = "server1.log";

    fn t0() -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 7, 15, 12, 0, 0).unwrap()
    }

    fn secs(n: i64) -> DateTime<Utc> {
        t0() + TimeDelta::seconds(n)
    }

    #[test]
    fn one_connection_then_one_login_is_a_confident_guess() {
        let mut correlator = JoinCorrelator::default();
        correlator.accepted(FILE, "203.0.113.5", t0());

        let guess = correlator.login(FILE, secs(2), false);
        assert_eq!(guess.ip.as_deref(), Some("203.0.113.5"));
        assert!(guess.confident);
    }

    #[test]
    fn two_connections_at_once_mean_we_do_not_know() {
        /* The whole reason confidence is tracked: a coin flip here attributes a stranger's
        address to an account. */
        let mut correlator = JoinCorrelator::default();
        correlator.accepted(FILE, "203.0.113.5", t0());
        correlator.accepted(FILE, "203.0.113.6", secs(1));

        assert!(!correlator.login(FILE, secs(2), false).confident);
    }

    #[test]
    fn the_same_address_twice_is_still_one_connection() {
        // A retrying client is not two people.
        let mut correlator = JoinCorrelator::default();
        correlator.accepted(FILE, "203.0.113.5", t0());
        correlator.accepted(FILE, "203.0.113.5", secs(1));

        assert!(correlator.login(FILE, secs(2), false).confident);
    }

    #[test]
    fn an_accept_too_long_before_the_login_is_unrelated() {
        let mut correlator = JoinCorrelator::new(TimeDelta::seconds(10));
        correlator.accepted(FILE, "203.0.113.5", t0());

        let guess = correlator.login(FILE, secs(30), false);
        assert!(guess.ip.is_none());
        assert!(!guess.confident);
    }

    #[test]
    fn stale_ambiguity_expires_rather_than_poisoning_every_later_join() {
        let mut correlator = JoinCorrelator::new(TimeDelta::seconds(10));
        correlator.accepted(FILE, "203.0.113.5", t0());
        correlator.accepted(FILE, "203.0.113.6", secs(1));

        correlator.accepted(FILE, "203.0.113.7", secs(300));
        assert!(correlator.login(FILE, secs(301), false).confident);
    }

    #[test]
    fn a_placeholder_login_does_not_consume_the_pending_connection() {
        /* The pre-authentication line arrives first for the SAME connection; the real login
        is still coming. */
        let mut correlator = JoinCorrelator::default();
        correlator.accepted(FILE, "203.0.113.5", t0());

        correlator.login(FILE, secs(1), true);
        let real = correlator.login(FILE, secs(2), false);

        assert_eq!(real.ip.as_deref(), Some("203.0.113.5"));
        assert!(real.confident);
    }

    #[test]
    fn a_real_login_consumes_it_so_the_next_join_does_not_inherit_the_address() {
        let mut correlator = JoinCorrelator::default();
        correlator.accepted(FILE, "203.0.113.5", t0());
        correlator.login(FILE, secs(1), false);

        assert!(correlator.login(FILE, secs(2), false).ip.is_none());
    }

    #[test]
    fn two_servers_do_not_correlate_against_each_other() {
        let mut correlator = JoinCorrelator::default();
        correlator.accepted("server1.log", "203.0.113.5", t0());

        assert!(correlator.login("server2.log", secs(1), false).ip.is_none());
        assert_eq!(
            correlator
                .login("server1.log", secs(1), false)
                .ip
                .as_deref(),
            Some("203.0.113.5")
        );
    }
}

mod flag_matching_tests {
    use pavlov_core::evasion::{FlagMatch, FlagMatching, FlagSet};

    fn flags(ips: &[&str], names: &[&str], ids: &[&str], manual: &[&str]) -> FlagSet {
        FlagSet {
            ips: ips.iter().map(|s| (*s).to_owned()).collect(),
            names: names.iter().copied().collect(),
            ids: ids.iter().copied().collect(),
            manual_ips: manual.iter().map(|s| (*s).to_owned()).collect(),
            ..FlagSet::default()
        }
    }

    #[test]
    fn a_flagged_address_is_a_hit() {
        let verdict = FlagMatching::check(
            &flags(&["203.0.113.5"], &[], &[], &[]),
            Some("203.0.113.5"),
            Some("Alice"),
            Some("0002abc"),
        );
        assert_eq!(verdict.matched, FlagMatch::Ip);
        assert!(verdict.detail.unwrap().contains("blacklisted ip"));
    }

    #[test]
    fn matching_is_exact_and_never_by_subnet() {
        /* A /24 covers 254 addresses, and on a residential ISP those belong to 254 unrelated
        households - one evader would ban a neighbourhood. */
        assert!(
            !FlagMatching::check(
                &flags(&["203.0.113.5"], &[], &[], &[]),
                Some("203.0.113.6"),
                None,
                None
            )
            .hit()
        );
    }

    #[test]
    fn an_account_flag_beats_a_name_flag_when_both_apply() {
        let verdict = FlagMatching::check(
            &flags(&[], &["Alice"], &["0002abc"], &[]),
            None,
            Some("Alice"),
            Some("0002abc"),
        );
        assert_eq!(verdict.matched, FlagMatch::AccountId);
    }

    #[test]
    fn a_manual_address_flag_counts_the_same_as_a_ban_derived_one() {
        assert_eq!(
            FlagMatching::check(
                &flags(&[], &[], &[], &["203.0.113.5"]),
                Some("203.0.113.5"),
                None,
                None
            )
            .matched,
            FlagMatch::Ip
        );
    }

    #[test]
    fn the_verdict_says_whether_an_owner_set_the_flag_or_a_ban_left_it_behind() {
        /* Both render as "blacklisted ip 203.0.113.5"; the DETAIL stays identical on purpose
        (the auto-ban decision matches its wording), so the source travels beside it. */
        let ip = Some("203.0.113.5");
        assert!(
            FlagMatching::check(&flags(&[], &[], &[], &["203.0.113.5"]), ip, None, None).manual
        );
        assert!(
            !FlagMatching::check(&flags(&["203.0.113.5"], &[], &[], &[]), ip, None, None).manual
        );

        // A username flag is only ever set by hand; an account flag only ever by a ban.
        assert!(
            FlagMatching::check(&flags(&[], &["Alice"], &[], &[]), None, Some("Alice"), None)
                .manual
        );
        assert!(
            !FlagMatching::check(
                &flags(&[], &[], &["0002abc"], &[]),
                None,
                None,
                Some("0002abc")
            )
            .manual
        );
    }

    #[test]
    fn names_and_ids_match_case_insensitively_addresses_do_not() {
        assert!(
            FlagMatching::check(&flags(&[], &["alice"], &[], &[]), None, Some("ALICE"), None).hit()
        );
        assert!(
            FlagMatching::check(
                &flags(&[], &[], &["0002ABC"], &[]),
                None,
                None,
                Some("0002abc")
            )
            .hit()
        );
    }

    #[test]
    fn nothing_known_is_clean() {
        assert!(
            !FlagMatching::check(
                &FlagSet::default(),
                Some("203.0.113.5"),
                Some("Alice"),
                Some("0002abc")
            )
            .hit()
        );
        assert!(
            !FlagMatching::check(&flags(&["203.0.113.5"], &[], &[], &[]), None, None, None).hit()
        );
    }
}

mod pending_flag_tests {
    use chrono::{TimeDelta, TimeZone, Utc};
    use pavlov_core::evasion::PendingFlag;

    #[test]
    fn a_pending_flag_expires() {
        /* The case it exists for: banning somebody currently connected, whose address is
        confirmed by the DISCONNECT the ban causes. After a few minutes any disconnect is
        somebody else's. */
        let t0 = Utc.with_ymd_and_hms(2026, 7, 15, 12, 0, 0).unwrap();
        let pending = PendingFlag {
            account_id: "0002abc".into(),
            requested_at: t0,
            flag_account_id: true,
        };
        assert!(pending.is_live(t0 + TimeDelta::minutes(2), TimeDelta::minutes(5)));
        assert!(!pending.is_live(t0 + TimeDelta::minutes(10), TimeDelta::minutes(5)));
    }
}
