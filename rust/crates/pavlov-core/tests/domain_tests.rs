//! Port of `dotnet/tests/PavlovBot.Tests/DomainTests.cs`. One Rust module per C# test class,
//! one Rust test per C# test method (snake_case), `[InlineData]` rows as table loops.

// Test helpers may panic: a panic is how a test reports failure.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

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

mod staff_hierarchy_tests {
    use chrono::{TimeDelta, Utc};
    use pavlov_core::moderation::{BanRecord, BanRules, StaffHierarchy, StaffTier};

    #[test]
    fn highest_matching_role_wins() {
        assert_eq!(
            StaffHierarchy::tier_of(true, true, true, true, false),
            StaffTier::SuperOwner
        );
        assert_eq!(
            StaffHierarchy::tier_of(false, false, false, true, false),
            StaffTier::Mod
        );
        assert_eq!(
            StaffHierarchy::tier_of(false, false, false, false, false),
            StaffTier::None
        );
    }

    #[test]
    fn a_lower_tier_cannot_undo_a_higher_tiers_action() {
        assert!(!StaffHierarchy::can_override(
            StaffTier::Mod,
            Some(StaffTier::Admin)
        ));
        assert!(!StaffHierarchy::can_override(
            StaffTier::Admin,
            Some(StaffTier::Owner)
        ));
        assert!(!StaffHierarchy::can_override(
            StaffTier::Owner,
            Some(StaffTier::SuperOwner)
        ));
    }

    #[test]
    fn a_super_owner_can_undo_anything_below_a_master_owner() {
        for t in StaffTier::ALL
            .into_iter()
            .filter(|t| *t != StaffTier::MasterOwner)
        {
            assert!(
                StaffHierarchy::can_override(StaffTier::SuperOwner, Some(t)),
                "tier {t:?}"
            );
        }
        assert!(!StaffHierarchy::can_override(
            StaffTier::SuperOwner,
            Some(StaffTier::MasterOwner)
        ));
    }

    #[test]
    fn a_master_owner_is_above_everyone_and_only_a_master_owner_can_undo_them() {
        for t in StaffTier::ALL {
            assert!(
                StaffHierarchy::can_override(StaffTier::MasterOwner, Some(t)),
                "tier {t:?}"
            );
        }
        for t in StaffTier::ALL
            .into_iter()
            .filter(|t| *t != StaffTier::MasterOwner)
        {
            assert!(
                !StaffHierarchy::can_override(t, Some(StaffTier::MasterOwner)),
                "tier {t:?}"
            );
        }
        assert_eq!(
            StaffHierarchy::tier_of(true, true, true, true, true),
            StaffTier::MasterOwner
        );
        assert_eq!(StaffHierarchy::name(StaffTier::MasterOwner), "Master Owner");
    }

    #[test]
    fn a_ban_cannot_be_replaced_by_a_tier_that_could_not_lift_it() {
        // Replacing is lifting: a mod who cannot /unban an owner's permanent ban must not be
        // able to /tempban the same player for a minute and wait.
        let now = Utc::now();
        let bans = [BanRecord {
            permanent: true,
            tier: Some(StaffTier::MasterOwner),
            ..BanRecord::for_player("Cheater")
        }];

        assert!(
            BanRules::protected_from_replacement(&bans, "cheater", StaffTier::SuperOwner, now)
                .is_some()
        );
        assert!(
            BanRules::protected_from_replacement(&bans, "Cheater", StaffTier::None, now).is_some()
        ); // automation
        assert!(
            BanRules::protected_from_replacement(&bans, "Cheater", StaffTier::MasterOwner, now)
                .is_none()
        );
        assert!(
            BanRules::protected_from_replacement(&bans, "SomeoneElse", StaffTier::Mod, now)
                .is_none()
        );
    }

    #[test]
    fn an_expired_or_untiered_ban_can_always_be_replaced() {
        let now = Utc::now();
        let bans = [
            BanRecord {
                expires: Some(now - TimeDelta::minutes(1)),
                tier: Some(StaffTier::MasterOwner),
                ..BanRecord::for_player("Served")
            },
            BanRecord {
                permanent: true,
                tier: None,
                ..BanRecord::for_player("Auto")
            },
        ];

        assert!(
            BanRules::protected_from_replacement(&bans, "Served", StaffTier::Mod, now).is_none()
        );
        assert!(BanRules::protected_from_replacement(&bans, "Auto", StaffTier::Mod, now).is_none());
    }

    #[test]
    fn equal_tiers_can_undo_each_other() {
        assert!(StaffHierarchy::can_override(
            StaffTier::Mod,
            Some(StaffTier::Mod)
        ));
        assert!(StaffHierarchy::can_override(
            StaffTier::Admin,
            Some(StaffTier::Admin)
        ));
    }

    #[test]
    fn a_record_with_no_tier_can_be_lifted_by_anyone() {
        assert!(StaffHierarchy::can_override(StaffTier::Mod, None));
        assert!(StaffHierarchy::can_override(
            StaffTier::Mod,
            Some(StaffTier::None)
        ));
    }
}

mod menu_link_tests {
    use pavlov_core::moderation::{MenuClaimAction, MenuLink};

    #[test]
    fn a_first_claim_is_granted() {
        assert_eq!(
            MenuLink::decide(None, Some("Alice"), None, "1", false).action,
            MenuClaimAction::Grant
        );
    }

    #[test]
    fn a_bound_member_cannot_claim_a_different_name() {
        let decision = MenuLink::decide(Some("Alice"), Some("Bob"), None, "1", false);
        assert_eq!(decision.action, MenuClaimAction::Locked);
        assert_eq!(decision.bound_name.as_deref(), Some("Alice"));
    }

    #[test]
    fn the_lock_holds_even_with_no_menu_held() {
        // The state somebody would engineer: release the menu, then re-claim as an alt.
        assert_eq!(
            MenuLink::decide(Some("Alice"), Some("Bob"), None, "1", false).action,
            MenuClaimAction::Locked
        );
    }

    #[test]
    fn two_accounts_cannot_converge_on_one_name() {
        let decision = MenuLink::decide(None, Some("Alice"), Some("999"), "1", false);
        assert_eq!(decision.action, MenuClaimAction::Taken);
        assert_eq!(decision.owner_id.as_deref(), Some("999"));
    }

    #[test]
    fn re_entering_your_own_name_while_holding_a_menu_releases_it() {
        assert_eq!(
            MenuLink::decide(Some("Alice"), Some("alice"), Some("1"), "1", true).action,
            MenuClaimAction::Release
        );
    }

    #[test]
    fn re_entering_your_own_name_without_a_menu_grants_it_back() {
        assert_eq!(
            MenuLink::decide(Some("Alice"), Some("Alice"), Some("1"), "1", false).action,
            MenuClaimAction::Grant
        );
    }

    #[test]
    fn name_matching_ignores_case_and_surrounding_space() {
        assert_eq!(
            MenuLink::decide(Some("Alice"), Some("  ALICE "), Some("1"), "1", false).action,
            MenuClaimAction::Grant
        );
    }
}

mod verification_rules_tests {
    use indexmap::IndexMap;
    use pavlov_core::verification::{VerificationConflict, VerificationRecord, VerificationRules};

    fn store(rows: &[(&str, &str, &[&str])]) -> IndexMap<String, VerificationRecord> {
        rows.iter()
            .map(|(id, name, ips)| {
                (
                    (*id).to_owned(),
                    VerificationRecord {
                        name: (*name).to_owned(),
                        ips: ips.iter().map(|s| (*s).to_owned()).collect(),
                    },
                )
            })
            .collect()
    }

    fn ips(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| (*s).to_owned()).collect()
    }

    #[test]
    fn a_fresh_account_with_a_free_name_is_allowed() {
        assert!(
            VerificationRules::check(&store(&[]), "1", Some("Alice"), &ips(&["1.2.3.4"]))
                .is_allowed()
        );
    }

    #[test]
    fn an_already_verified_account_is_refused_with_its_own_name() {
        let verdict =
            VerificationRules::check(&store(&[("1", "Alice", &[])]), "1", Some("Bob"), &[]);
        assert_eq!(verdict.conflict, VerificationConflict::AlreadyVerified);
        assert!(verdict.reason.unwrap().contains("Alice"));
    }

    #[test]
    fn a_name_verified_to_somebody_else_is_refused() {
        let verdict =
            VerificationRules::check(&store(&[("999", "Alice", &[])]), "1", Some("alice"), &[]);
        assert_eq!(verdict.conflict, VerificationConflict::NameTaken);
        assert_eq!(verdict.other_id.as_deref(), Some("999"));
    }

    #[test]
    fn a_shared_ip_reads_as_an_alt() {
        let verdict = VerificationRules::check(
            &store(&[("999", "Bob", &["1.2.3.4"])]),
            "1",
            Some("Alice"),
            &ips(&["1.2.3.4"]),
        );
        assert_eq!(verdict.conflict, VerificationConflict::Alt);
        assert_eq!(verdict.other_id.as_deref(), Some("999"));
    }

    #[test]
    fn no_shared_ip_means_no_alt_finding() {
        assert!(
            VerificationRules::check(
                &store(&[("999", "Bob", &["5.6.7.8"])]),
                "1",
                Some("Alice"),
                &ips(&["1.2.3.4"])
            )
            .is_allowed()
        );
    }

    #[test]
    fn verifying_with_no_ips_at_all_still_checks_the_name() {
        let verdict = VerificationRules::check(
            &store(&[("999", "Alice", &["1.2.3.4"])]),
            "1",
            Some("Alice"),
            &[],
        );
        assert_eq!(verdict.conflict, VerificationConflict::NameTaken);
    }
}

mod ledger_tests {
    use std::collections::HashMap;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::{Arc, Mutex};
    use std::time::Duration;

    use pavlov_core::economy::{BalanceStore, Ledger};
    use pavlov_core::text::case::fold_case;

    #[derive(Default)]
    struct MemoryBalances {
        balances: Mutex<HashMap<String, i64>>,
        fail_writes: AtomicBool,
    }

    impl BalanceStore for MemoryBalances {
        fn read(&self, player_id: &str) -> Option<i64> {
            self.balances
                .lock()
                .unwrap()
                .get(&fold_case(player_id))
                .copied()
        }

        fn write(&self, player_id: &str, balance: i64) -> bool {
            if self.fail_writes.load(Ordering::SeqCst) {
                return false;
            }
            self.balances
                .lock()
                .unwrap()
                .insert(fold_case(player_id), balance);
            true
        }
    }

    #[tokio::test]
    async fn a_credit_applies_and_reports_the_delta() {
        let ledger = Ledger::new(MemoryBalances::default());
        let change = ledger.credit("alice", 500).await;
        assert!(change.ok);
        assert_eq!(change.before, 0);
        assert_eq!(change.after, 500);
        assert_eq!(change.delta(), 500);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn concurrent_credits_do_not_double_spend() {
        // Two concurrent payouts both reading 500 and both writing 600 is the bug this prevents.
        let ledger = Arc::new(Ledger::new(MemoryBalances::default()));
        let tasks: Vec<_> = (0..100)
            .map(|_| {
                let ledger = Arc::clone(&ledger);
                tokio::spawn(async move { ledger.credit("alice", 10).await })
            })
            .collect();
        for t in tasks {
            t.await.unwrap();
        }
        assert_eq!(ledger.credit("alice", 0).await.after, 1000);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn different_players_do_not_wait_on_each_other() {
        let ledger = Arc::new(Ledger::new(MemoryBalances::default()));
        let (entered_tx, entered_rx) = tokio::sync::oneshot::channel::<()>();
        let (release_tx, release_rx) = std::sync::mpsc::channel::<()>();

        // The mutator BLOCKS inside alice's lock, on a worker of its own.
        let slow = {
            let ledger = Arc::clone(&ledger);
            tokio::spawn(async move {
                ledger
                    .mutate::<()>("alice", move |before| {
                        let _ = entered_tx.send(());
                        let _ = release_rx.recv();
                        Ok(Some(before + 1))
                    })
                    .await
            })
        };
        tokio::time::timeout(Duration::from_secs(5), entered_rx)
            .await
            .unwrap()
            .unwrap();

        // Bob must complete while alice's mutator is still held.
        let fast = tokio::time::timeout(Duration::from_secs(5), ledger.credit("bob", 5))
            .await
            .unwrap();
        assert!(fast.ok);

        release_tx.send(()).unwrap();
        slow.await.unwrap().unwrap();
    }

    #[tokio::test]
    async fn a_veto_leaves_the_balance_untouched() {
        let ledger = Ledger::new(MemoryBalances::default());
        ledger.credit("alice", 100).await;
        let change = ledger.debit("alice", 500).await;
        assert!(!change.ok);
        assert_eq!(change.before, 100);
        assert_eq!(change.after, 100);
    }

    #[tokio::test]
    async fn a_debit_to_exactly_zero_is_allowed() {
        let ledger = Ledger::new(MemoryBalances::default());
        ledger.credit("alice", 100).await;
        assert!(ledger.debit("alice", 100).await.ok);
    }

    #[tokio::test]
    async fn a_throwing_mutator_propagates_but_leaves_the_balance_untouched() {
        // A defect must not come back in the vocabulary of a routine veto.
        let ledger = Ledger::new(MemoryBalances::default());
        ledger.credit("alice", 100).await;

        let result = ledger.mutate("alice", |_| Err("boom")).await;
        assert_eq!(result, Err("boom"));

        // Untouched, and the next caller must still get through.
        let next = ledger.credit("alice", 1).await;
        assert!(next.ok);
        assert_eq!(next.before, 100);
        assert_eq!(next.after, 101);
    }

    #[tokio::test]
    async fn a_failed_write_reports_the_old_balance_not_the_intended_one() {
        let store = MemoryBalances::default();
        store.fail_writes.store(true, Ordering::SeqCst);
        let ledger = Ledger::new(store);
        let change = ledger.credit("alice", 500).await;
        assert!(!change.ok);
        assert_eq!(change.after, 0);
    }

    #[tokio::test]
    async fn many_distinct_players_each_keep_their_own_balance() {
        // Striping means distinct players share a LOCK; it must never mean sharing a BALANCE.
        let ledger = Ledger::new(MemoryBalances::default());
        for i in 0..500 {
            ledger.credit(&format!("player{i}"), i).await;
        }
        assert_eq!(ledger.credit("player0", 0).await.before, 0);
        assert_eq!(ledger.credit("player250", 0).await.before, 250);
        assert_eq!(ledger.credit("player499", 0).await.before, 499);
    }
}

mod menu_role_map_tests {
    use pavlov_core::moderation::{MenuRoleMap, MenuTier};

    fn map(high_staff: Option<u64>, staff: Option<u64>) -> MenuRoleMap {
        MenuRoleMap {
            high_staff,
            staff,
            blacklist: None,
        }
    }

    #[test]
    fn the_highest_tier_wins() {
        let map = map(Some(1), Some(2));
        assert_eq!(map.tier_for(&[1, 2]), Some(MenuTier::HighStaff));
        assert_eq!(map.tier_for(&[1]), Some(MenuTier::HighStaff));
        assert_eq!(map.tier_for(&[2]), Some(MenuTier::Staff));
        assert_eq!(MenuTier::HighStaff.as_str(), "highstaff");
    }

    #[test]
    fn an_unmapped_member_gets_no_tier() {
        let map = map(Some(1), Some(2));
        assert_eq!(map.tier_for(&[99]), None);
        assert_eq!(map.tier_for(&[]), None);
    }

    #[test]
    fn an_unconfigured_map_grants_nothing() {
        assert_eq!(MenuRoleMap::EMPTY.tier_for(&[1, 2, 3]), None);
    }

    #[test]
    fn what_the_command_stored_beats_the_environment() {
        let in_force = map(Some(10), Some(20)).over(Some(1), Some(2), Some(3));
        assert_eq!(in_force.tier_for(&[10]), Some(MenuTier::HighStaff));
        assert_eq!(in_force.tier_for(&[20]), Some(MenuTier::Staff));
        assert_eq!(in_force.tier_for(&[1]), None);
    }

    #[test]
    fn the_environment_still_applies_to_tiers_the_command_never_set() {
        let in_force = map(None, Some(20)).over(Some(1), Some(2), Some(3));
        assert_eq!(in_force.tier_for(&[1]), Some(MenuTier::HighStaff));
        assert_eq!(in_force.tier_for(&[20]), Some(MenuTier::Staff));
        assert_eq!(in_force.blacklist, Some(3));
    }

    #[test]
    fn an_empty_store_leaves_the_environment_in_charge() {
        let in_force = MenuRoleMap::EMPTY.over(Some(1), Some(2), Some(3));
        assert_eq!(in_force.tier_for(&[1]), Some(MenuTier::HighStaff));
        assert_eq!(in_force.tier_for(&[2]), Some(MenuTier::Staff));
        assert!(in_force.any());
    }

    #[test]
    fn neither_source_set_means_nobody_qualifies() {
        let in_force = MenuRoleMap::EMPTY.over(None, None, None);
        assert!(!in_force.any());
        assert_eq!(in_force.tier_for(&[1, 2, 3]), None);
    }
}
