//! Port of `DataLayerTests.cs`, class RosterWriteGuardTests. The SerializedStoreTests class in
//! that file arrives with `pavlov-storage` (M4).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod roster_write_guard_tests {
    use pavlov_core::data::{RosterWriteDecision, RosterWriteGuard};

    fn roster(n: usize) -> Vec<String> {
        (0..n).map(|i| format!("player{i}")).collect()
    }

    #[test]
    fn a_small_change_is_allowed() {
        let current = roster(20);
        let mut proposed: Vec<String> = current.iter().take(19).cloned().collect();
        proposed.push("newMember".into());
        let v = RosterWriteGuard::evaluate(Some(&current), Some(&proposed), true, false);
        assert!(v.is_allowed());
        assert_eq!(v.dropped, 1);
    }

    #[test]
    fn a_wipe_is_refused() {
        // An empty roster is VALID to Pavlov; a parse bug producing one strips a faction silently.
        let v = RosterWriteGuard::evaluate(Some(&roster(20)), Some(&[]), true, false);
        assert!(!v.is_allowed());
        assert_eq!(v.decision, RosterWriteDecision::RefusedBulkDrop);
        assert_eq!(v.dropped, 20);
        assert!(v.explanation.unwrap().contains("suspected corruption"));
    }

    #[test]
    fn the_limit_boundary_is_exact() {
        let current = roster(20);
        assert!(
            RosterWriteGuard::evaluate(Some(&current), Some(&current[..15]), true, false)
                .is_allowed()
        ); // 5 dropped
        assert!(
            !RosterWriteGuard::evaluate(Some(&current), Some(&current[..14]), true, false)
                .is_allowed()
        ); // 6 dropped
    }

    #[test]
    fn an_unreadable_file_is_refused_not_treated_as_empty() {
        let v = RosterWriteGuard::evaluate(None, Some(&["someone".to_owned()]), true, false);
        assert!(!v.is_allowed());
        assert_eq!(v.decision, RosterWriteDecision::RefusedUnreadable);
    }

    #[test]
    fn a_missing_file_is_allowed_creating_a_roster_is_legitimate() {
        assert!(
            RosterWriteGuard::evaluate(None, Some(&["someone".to_owned()]), false, false)
                .is_allowed()
        );
    }

    #[test]
    fn an_empty_existing_roster_can_be_filled() {
        assert!(RosterWriteGuard::evaluate(Some(&[]), Some(&roster(30)), true, false).is_allowed());
    }

    #[test]
    fn a_deliberate_wipe_is_allowed_when_asked_for() {
        assert!(RosterWriteGuard::evaluate(Some(&roster(50)), Some(&[]), true, true).is_allowed());
    }

    #[test]
    fn a_null_payload_is_refused() {
        assert_eq!(
            RosterWriteGuard::evaluate(Some(&roster(10)), None, true, false).decision,
            RosterWriteDecision::RefusedInvalidPayload
        );
    }

    #[test]
    fn comparison_is_case_insensitive() {
        let current = roster(20);
        let proposed: Vec<String> = current.iter().map(|n| n.to_uppercase()).collect();
        let v = RosterWriteGuard::evaluate(Some(&current), Some(&proposed), true, false);
        assert!(v.is_allowed());
        assert_eq!(v.dropped, 0);
    }

    #[test]
    fn split_lines_normalises_the_way_the_game_reads_it() {
        assert_eq!(
            RosterWriteGuard::split_lines(Some("alice\r\n  bob  \n\n\ncarol\n")),
            ["alice", "bob", "carol"]
        );
    }
}
