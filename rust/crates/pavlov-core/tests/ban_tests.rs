//! Port of `BanTests.cs` (classes BanRecordTests, AutoBanDecisionTests, SourceBanTests).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{DateTime, TimeZone, Utc};

fn now() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 7, 15, 12, 0, 0).unwrap()
}

mod ban_record_tests {
    use super::now;
    use chrono::{DateTime, TimeDelta, Utc};
    use pavlov_core::moderation::{BanRecord, BanRules};

    fn ban(id: &str, permanent: bool, expires: Option<DateTime<Utc>>) -> BanRecord {
        BanRecord {
            permanent,
            expires,
            ..BanRecord::for_player(id)
        }
    }

    #[test]
    fn a_record_that_cannot_answer_whether_it_is_in_force_is_invalid() {
        assert!(!ban("alice", false, None).is_valid());
        assert!(!ban("", false, None).is_valid());
        assert!(!ban("   ", true, None).is_valid());
        assert!(ban("alice", true, None).is_valid());
        assert!(ban("alice", false, Some(now())).is_valid());
    }

    #[test]
    fn only_valid_in_force_bans_are_active() {
        let bans = [
            ban("permanent", true, None),
            ban("current", false, Some(now() + TimeDelta::hours(1))),
            ban("served", false, Some(now() - TimeDelta::hours(1))),
            ban("broken", false, None),
        ];
        let active: Vec<&str> = BanRules::active(&bans, now())
            .iter()
            .map(|b| b.player_id.as_str())
            .collect();
        assert_eq!(active, ["permanent", "current"]);
    }

    #[test]
    fn a_ban_expiring_exactly_now_is_served() {
        assert!(BanRules::active(&[ban("alice", false, Some(now()))], now()).is_empty());
    }

    #[test]
    fn player_matching_ignores_case_and_surrounding_space() {
        assert!(BanRules::same_player(Some("Alice"), Some(" alice ")));
        assert!(!BanRules::same_player(Some("Alice"), Some("Alicia")));
        assert!(!BanRules::same_player(None, Some("Alice")));
    }
}

mod auto_ban_decision_tests {
    use super::now;
    use chrono::TimeDelta;
    use pavlov_core::moderation::{AutoBanAction, BanRecord, BanRules};

    #[test]
    fn an_unexpired_temp_ban_blocks_without_escalating() {
        let existing = BanRecord {
            expires: Some(now() + TimeDelta::hours(2)),
            ..BanRecord::for_player("alice")
        };
        assert_eq!(
            BanRules::auto_ban_decision(Some(&existing), Some("blacklisted ip"), now()),
            AutoBanAction::Block
        );
    }

    #[test]
    fn a_served_temp_ban_is_lifted_not_escalated_to_permanent() {
        // A served ban's own leftover flags look exactly like evasion until the sweep runs.
        let served = BanRecord {
            expires: Some(now() - TimeDelta::hours(1)),
            ..BanRecord::for_player("alice")
        };
        assert_eq!(
            BanRules::auto_ban_decision(Some(&served), Some("blacklisted ip"), now()),
            AutoBanAction::Lift
        );
        assert_eq!(
            BanRules::auto_ban_decision(Some(&served), Some("Blacklisted account 0002abc"), now()),
            AutoBanAction::Lift
        );
    }

    #[test]
    fn a_username_flag_still_escalates_after_a_temp_ban_expires() {
        let served = BanRecord {
            expires: Some(now() - TimeDelta::hours(1)),
            ..BanRecord::for_player("alice")
        };
        assert_eq!(
            BanRules::auto_ban_decision(Some(&served), Some("blacklisted username"), now()),
            AutoBanAction::Ban
        );
    }

    #[test]
    fn no_covering_ban_means_auto_ban() {
        assert_eq!(
            BanRules::auto_ban_decision(None, Some("blacklisted ip"), now()),
            AutoBanAction::Ban
        );
    }

    #[test]
    fn a_permanent_ban_never_produces_a_lift() {
        let permanent = BanRecord {
            permanent: true,
            ..BanRecord::for_player("alice")
        };
        assert_eq!(
            BanRules::auto_ban_decision(Some(&permanent), Some("blacklisted ip"), now()),
            AutoBanAction::Ban
        );
    }
}

mod source_ban_tests {
    use std::collections::HashMap;

    use pavlov_core::moderation::{BanRecord, BanRules};

    fn ban(id: &str, reason: &str, moderator: &str) -> BanRecord {
        BanRecord {
            permanent: true,
            reason: Some(reason.to_owned()),
            moderator: Some(moderator.to_owned()),
            ..BanRecord::for_player(id)
        }
    }

    #[test]
    fn auto_bans_and_evasion_bans_are_not_real_punishments() {
        assert!(!BanRules::is_real_ban(Some(&ban(
            "a",
            "Auto-ban: flagged IP",
            "IP-Guard"
        ))));
        assert!(!BanRules::is_real_ban(Some(&ban(
            "a",
            "Ban evasion",
            "Ban evasion (auto)"
        ))));
        assert!(!BanRules::is_real_ban(Some(&ban(
            "a", "Cheating", "IP-Guard"
        ))));
    }

    #[test]
    fn broad_configure_blacklists_are_not_real_punishments_either() {
        assert!(!BanRules::is_real_ban(Some(&ban(
            "a",
            "Blacklisted via /configure",
            "Owner"
        ))));
    }

    #[test]
    fn an_ordinary_ban_is_a_real_punishment() {
        assert!(BanRules::is_real_ban(Some(&ban("a", "Cheating", "Dana"))));
    }

    #[test]
    fn a_ban_with_no_reason_is_not_usable_as_a_source() {
        assert!(!BanRules::is_real_ban(Some(&BanRecord {
            permanent: true,
            ..BanRecord::for_player("a")
        })));
        assert!(!BanRules::is_real_ban(None));
    }

    #[test]
    fn the_same_account_is_matched_before_the_same_name() {
        let bans = [
            ban("SomeoneElse", "Griefing", "Dana"),
            ban("OldName", "Cheating", "Dana"),
        ];
        let eos: HashMap<&str, &str> = HashMap::from([("OldName", "0002abc")]);
        let source = BanRules::source_ban_for(
            &bans,
            "NewName",
            Some("0002abc"),
            |n| eos.get(n).map(|s| (*s).to_owned()),
            &[],
        )
        .unwrap();
        assert_eq!(source.player_id, "OldName");
        assert_eq!(source.reason.as_deref(), Some("Cheating"));
    }

    #[test]
    fn a_known_alt_name_is_matched_when_the_account_is_unknown() {
        let bans = [ban("OldName", "Cheating", "Dana")];
        let source =
            BanRules::source_ban_for(&bans, "NewName", None, |_| None, &["OldName".to_owned()])
                .unwrap();
        assert_eq!(source.player_id, "OldName");
    }

    #[test]
    fn no_real_ban_anywhere_returns_null_rather_than_a_machinery_ban() {
        let bans = [ban("OldName", "Auto-ban: flagged IP", "IP-Guard")];
        assert!(BanRules::source_ban_for(&bans, "OldName", None, |_| None, &[]).is_none());
    }
}
