//! Port of `WarningServiceTests.cs`: warnings, and the one rule that turns three of them into a
//! day's ban. The escalation is a COUNTER THAT BANS PEOPLE, so the tests that matter are about
//! when it must not fire.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use chrono::{DateTime, TimeDelta};
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::time::ManualClock;
use pavlov_host::moderation::warnings::{
    DECAY_WINDOW, ESCALATION_BAN, ESCALATION_THRESHOLD, warnings_until_ban,
};
use pavlov_host::moderation::{AuditLog, WarningOutcome, WarningService};

struct Fixture {
    clock: Arc<ManualClock>,
    warnings: WarningService,
}

impl Fixture {
    fn new() -> Self {
        let clock = Arc::new(ManualClock::new(
            DateTime::UNIX_EPOCH + TimeDelta::days(56 * 365),
        ));
        let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())));
        let audit = Arc::new(AuditLog::new(Arc::clone(&store), Arc::clone(&clock) as _));
        let warnings = WarningService::new(store, audit, Arc::clone(&clock) as _);
        Self { clock, warnings }
    }

    async fn warn(&self) -> WarningOutcome {
        self.warn_for("spawn killing").await
    }

    async fn warn_for(&self, reason: &str) -> WarningOutcome {
        self.warnings.warn("Alice", reason, "Mod").await.unwrap()
    }
}

fn now_plus(f: &Fixture, by: TimeDelta) {
    f.clock.advance(by);
}

#[tokio::test]
async fn the_third_warning_escalates_and_the_first_two_do_not() {
    let f = Fixture::new();
    assert!(!f.warn().await.escalated);
    assert!(!f.warn().await.escalated);

    let third = f.warn().await;
    assert!(third.escalated);
    assert_eq!(third.active, 3);
}

#[tokio::test]
async fn a_fourth_warning_does_not_escalate_again() {
    // Firing on `>=` would build a permanent ban a day at a time.
    let f = Fixture::new();
    for _ in 0..3 {
        f.warn().await;
    }
    assert!(!f.warn().await.escalated);
    assert!(!f.warn().await.escalated);
}

#[test]
fn the_ban_is_one_day() {
    // The whole policy: a typo here is a month-long ban.
    assert_eq!(ESCALATION_BAN, TimeDelta::days(1));
    assert_eq!(ESCALATION_THRESHOLD, 3);
}

#[tokio::test]
async fn a_warning_past_the_decay_window_does_not_count_toward_the_ban() {
    let f = Fixture::new();
    f.warn_for("ancient").await;
    now_plus(&f, DECAY_WINDOW + TimeDelta::days(1));

    f.warn().await;
    let third = f.warn().await;

    assert!(!third.escalated);
    assert_eq!(third.active, 2);
}

#[tokio::test]
async fn a_decayed_warning_stays_on_record() {
    // Decay is about the DECISION, not the evidence.
    let f = Fixture::new();
    f.warn_for("ancient").await;
    now_plus(&f, DECAY_WINDOW + TimeDelta::days(1));

    assert_eq!(f.warnings.for_player("Alice").len(), 1);
    assert!(f.warnings.active_for("Alice").is_empty());

    let next = f.warn().await;
    assert_eq!(next.active, 1);
    assert_eq!(next.total, 2);
}

#[tokio::test]
async fn warnings_just_inside_the_window_still_count() {
    let f = Fixture::new();
    f.warn().await;
    f.warn().await;
    now_plus(&f, DECAY_WINDOW - TimeDelta::hours(1));

    assert!(f.warn().await.escalated);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn two_moderators_warning_at_once_cannot_both_see_two_active() {
    // Counted from the list AS WRITTEN, so the third escalates exactly once.
    let f = Fixture::new();
    f.warn().await;

    let (a, b) = tokio::join!(
        f.warnings.warn("Alice", "a", "ModA"),
        f.warnings.warn("Alice", "b", "ModB")
    );

    assert_eq!(f.warnings.active_for("Alice").len(), 3);
    assert_eq!(
        [a.unwrap(), b.unwrap()]
            .iter()
            .filter(|o| o.escalated)
            .count(),
        1
    );
}

#[tokio::test]
async fn one_warning_can_be_removed_by_id() {
    let f = Fixture::new();
    let first = f.warn_for("wrong player").await;
    f.warn().await;

    assert!(f.warnings.remove("Alice", &first.warning.id).await.unwrap());
    let left = f.warnings.for_player("Alice");
    assert_eq!(left.len(), 1);
    assert!(left.iter().all(|w| w.id != first.warning.id));
}

#[tokio::test]
async fn removing_a_warning_drops_the_player_below_the_threshold() {
    // An appeal succeeds and the next warning must not re-ban.
    let f = Fixture::new();
    f.warn().await;
    let mistake = f.warn_for("wrong player").await;

    f.warnings
        .remove("Alice", &mistake.warning.id)
        .await
        .unwrap();

    assert!(!f.warn().await.escalated);
}

#[tokio::test]
async fn removing_an_unknown_id_is_refused_rather_than_silently_ignored() {
    let f = Fixture::new();
    f.warn().await;
    assert!(!f.warnings.remove("Alice", "nope99").await.unwrap());
    assert_eq!(f.warnings.for_player("Alice").len(), 1);
}

#[tokio::test]
async fn clearing_removes_everything_and_reports_how_many() {
    let f = Fixture::new();
    f.warn().await;
    f.warn().await;

    assert_eq!(f.warnings.clear("Alice", "Admin").await.unwrap(), 2);
    assert!(f.warnings.for_player("Alice").is_empty());
    assert_eq!(f.warnings.clear("Alice", "Admin").await.unwrap(), 0);
}

#[tokio::test]
async fn standings_rank_by_active_warnings() {
    let f = Fixture::new();
    f.warnings.warn("Alice", "a", "Mod").await.unwrap();
    f.warnings.warn("Bob", "b", "Mod").await.unwrap();
    f.warnings.warn("Bob", "c", "Mod").await.unwrap();

    let standings = f.warnings.standings();

    assert_eq!(standings[0].player, "Bob");
    assert_eq!(standings[0].active, 2);
    assert_eq!(standings[1].player, "Alice");
}

#[tokio::test]
async fn standings_omit_players_whose_warnings_have_all_decayed() {
    let f = Fixture::new();
    f.warn().await;
    now_plus(&f, DECAY_WINDOW + TimeDelta::days(1));

    assert!(f.warnings.standings().is_empty());
}

#[test]
fn warnings_until_ban_counts_down_and_then_stops() {
    assert_eq!(warnings_until_ban(0), Some(3));
    assert_eq!(warnings_until_ban(2), Some(1));
    assert_eq!(warnings_until_ban(3), None);
    assert_eq!(warnings_until_ban(9), None);
}

#[tokio::test]
async fn an_unknown_player_has_a_clean_record() {
    let f = Fixture::new();
    assert!(f.warnings.for_player("Nobody").is_empty());
    assert!(f.warnings.active_for("Nobody").is_empty());
    assert_eq!(f.warnings.clear("Nobody", "Admin").await.unwrap(), 0);
}

#[tokio::test]
async fn names_match_ignoring_case_and_the_stored_shape_is_camel_case() {
    // Rust-only: one record per player whatever the case, in the shape the Node bot reads.
    let f = Fixture::new();
    f.warnings.warn("Alice", "a", "Mod").await.unwrap();
    f.warnings.warn("alice", "b", "Mod").await.unwrap();

    assert_eq!(f.warnings.for_player("ALICE").len(), 2);
    let json = serde_json::to_value(&f.warnings.for_player("Alice")[0]).unwrap();
    assert!(json.get("moderator").is_some());
    assert!(json["at"].is_i64());
}
