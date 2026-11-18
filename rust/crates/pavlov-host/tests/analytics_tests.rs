//! `AnalyticsService` against a real SQLite event table: the numbers come out of SQL, and
//! automated actors never reach the staff leaderboard. (The C# has no test file for it.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use chrono::{TimeDelta, Utc};
use pavlov_core::events::{EventCategory, ServerEvent};
use pavlov_host::analytics::{AnalyticsService, ServerAnalytics};
use pavlov_storage::{EventStore, SqliteEventStore};

fn event(
    category: EventCategory,
    kind: &str,
    player: Option<&str>,
    actor: Option<&str>,
    hours_ago: i64,
) -> ServerEvent {
    ServerEvent {
        at: Utc::now() - TimeDelta::hours(hours_ago),
        category,
        kind: kind.into(),
        player: player.map(Into::into),
        server: Some("server1".into()),
        actor: actor.map(Into::into),
        detail: None,
    }
}

fn service(events: &[ServerEvent]) -> (tempfile::TempDir, AnalyticsService) {
    let dir = tempfile::tempdir().unwrap();
    let store = SqliteEventStore::open(dir.path().join("bot.db")).unwrap();
    store.append(events).unwrap();
    (dir, AnalyticsService::new(Arc::new(store)))
}

#[test]
fn the_overview_counts_joins_players_staff_and_bans_inside_the_window() {
    let (_dir, analytics) = service(&[
        event(EventCategory::Player, "player.join", Some("Alice"), None, 1),
        event(EventCategory::Player, "player.join", Some("Alice"), None, 2),
        event(EventCategory::Player, "player.join", Some("Bob"), None, 3),
        event(
            EventCategory::Player,
            "player.join",
            Some("Old"),
            None,
            24 * 10,
        ),
        event(
            EventCategory::Staff,
            "staff.ban",
            Some("Bob"),
            Some("mod1"),
            1,
        ),
        event(
            EventCategory::Staff,
            "staff.kick",
            Some("Bob"),
            Some("mod1"),
            1,
        ),
        event(
            EventCategory::Security,
            "security.vpn",
            Some("Bob"),
            Some("auto"),
            1,
        ),
    ]);
    let overview = analytics
        .overview(TimeDelta::days(7), None, Utc::now())
        .unwrap();
    assert_eq!(overview.joins, 3);
    assert_eq!(overview.unique_players, 2);
    assert_eq!(overview.staff_actions, 2);
    assert_eq!(overview.bans, 1);
    assert_eq!(overview.security_detections, 1);
    assert_eq!(overview.servers, [("server1".to_owned(), 3)]);
    assert!(overview.busiest_hour.is_some());
}

#[test]
fn automated_actors_are_left_off_the_leaderboard() {
    let (_dir, analytics) = service(&[
        event(EventCategory::Staff, "staff.ban", None, Some("mod1"), 1),
        event(EventCategory::Staff, "staff.ban", None, Some("mod1"), 1),
        event(EventCategory::Staff, "staff.kick", None, Some("mod2"), 1),
        event(
            EventCategory::Security,
            "security.ban",
            None,
            Some("auto"),
            1,
        ),
        event(
            EventCategory::Security,
            "security.ban",
            None,
            Some("System"),
            1,
        ),
    ]);
    let rows = analytics
        .staff_leaderboard(TimeDelta::days(7), Utc::now())
        .unwrap();
    assert_eq!(rows, [("mod1".to_owned(), 2), ("mod2".to_owned(), 1)]);
}

#[test]
fn one_moderators_activity_breaks_down_by_kind() {
    let (_dir, analytics) = service(&[
        event(EventCategory::Staff, "staff.ban", None, Some("mod1"), 1),
        event(EventCategory::Staff, "staff.ban", None, Some("mod1"), 1),
        event(EventCategory::Staff, "staff.kick", None, Some("mod1"), 1),
        event(EventCategory::Staff, "staff.kick", None, Some("mod2"), 1),
    ]);
    let activity = analytics
        .activity_of("mod1", TimeDelta::days(7), Utc::now())
        .unwrap();
    assert_eq!(activity.total, 3);
    assert_eq!(activity.actions[0], ("staff.ban".to_owned(), 2));
}

#[test]
fn joins_per_day_is_zero_for_a_window_under_a_day() {
    let mut day = ServerAnalytics::empty(TimeDelta::hours(12));
    day.joins = 10;
    assert!(day.joins_per_day().abs() < f64::EPSILON);
    let mut week = ServerAnalytics::empty(TimeDelta::days(7));
    week.joins = 14;
    assert!((week.joins_per_day() - 2.0).abs() < 1e-9);
}
