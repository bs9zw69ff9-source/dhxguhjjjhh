//! Port of `EventStoreTests.cs`: the timeline store - append-only, indexed, bounded by
//! retention. `append_cost_does_not_grow_with_the_table_size` is the test that would fail if
//! somebody moved the timeline back into the document store.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::Instant;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::events::{EventCategory, EventQuery, ServerEvent};
use pavlov_storage::{EventGrouping, EventStore, SqliteEventStore};

struct Fixture {
    _dir: tempfile::TempDir,
    store: SqliteEventStore,
}

fn fixture() -> Fixture {
    let dir = tempfile::tempdir().unwrap();
    let store = SqliteEventStore::open(dir.path().join("bot.db")).unwrap();
    Fixture { _dir: dir, store }
}

struct E {
    kind: &'static str,
    category: EventCategory,
    player: Option<&'static str>,
    server: Option<&'static str>,
    actor: Option<&'static str>,
    at: Option<DateTime<Utc>>,
}

impl Default for E {
    fn default() -> Self {
        Self {
            kind: "player.join",
            category: EventCategory::Player,
            player: Some("Alice"),
            server: Some("server1"),
            actor: None,
            at: None,
        }
    }
}

fn event(e: E) -> ServerEvent {
    ServerEvent {
        at: e.at.unwrap_or_else(Utc::now),
        category: e.category,
        kind: e.kind.into(),
        player: e.player.map(Into::into),
        server: e.server.map(Into::into),
        actor: e.actor.map(Into::into),
        detail: Some("detail".into()),
    }
}

fn kinds(events: &[ServerEvent]) -> Vec<&str> {
    events.iter().map(|e| e.kind.as_str()).collect()
}

fn query(f: impl FnOnce(&mut EventQuery)) -> EventQuery {
    let mut q = EventQuery::new(10);
    f(&mut q);
    q
}

// ---- appending and reading back ----

#[test]
fn an_appended_event_reads_back() {
    let f = fixture();
    f.store.append(&[event(E::default())]).unwrap();

    let found = f.store.query(&EventQuery::new(10)).unwrap();

    assert_eq!(found.len(), 1);
    assert_eq!(found[0].kind, "player.join");
    assert_eq!(found[0].player.as_deref(), Some("Alice"));
    assert_eq!(found[0].server.as_deref(), Some("server1"));
    assert_eq!(found[0].category, EventCategory::Player);
}

#[test]
fn events_come_back_newest_first() {
    let f = fixture();
    let now = Utc::now();
    f.store
        .append(&[
            event(E {
                kind: "first",
                at: Some(now - TimeDelta::minutes(10)),
                ..E::default()
            }),
            event(E {
                kind: "second",
                at: Some(now - TimeDelta::minutes(5)),
                ..E::default()
            }),
            event(E {
                kind: "third",
                at: Some(now),
                ..E::default()
            }),
        ])
        .unwrap();

    assert_eq!(
        kinds(&f.store.query(&EventQuery::new(10)).unwrap()),
        ["third", "second", "first"]
    );
}

#[test]
fn events_sharing_an_instant_keep_insertion_order() {
    // A batch shares a millisecond; the id breaks the tie so a ban never shows before its join.
    let f = fixture();
    let instant = Utc::now();
    let at = |kind| {
        event(E {
            kind,
            at: Some(instant),
            ..E::default()
        })
    };
    f.store
        .append(&[at("join"), at("warn"), at("ban")])
        .unwrap();

    assert_eq!(
        kinds(&f.store.query(&EventQuery::new(10)).unwrap()),
        ["ban", "warn", "join"]
    );
}

// ---- filtering ----

#[test]
fn filtering_by_player_is_case_insensitive() {
    let f = fixture();
    f.store
        .append(&[
            event(E::default()),
            event(E {
                player: Some("Bob"),
                ..E::default()
            }),
        ])
        .unwrap();

    let found = f
        .store
        .query(&query(|q| q.player = Some("alice".into())))
        .unwrap();

    assert_eq!(found.len(), 1);
    assert_eq!(found[0].player.as_deref(), Some("Alice"));
}

#[test]
fn filtering_by_actor_finds_what_one_staff_member_did() {
    let f = fixture();
    f.store
        .append(&[
            event(E {
                actor: Some("ModOne"),
                kind: "staff.ban",
                category: EventCategory::Staff,
                ..E::default()
            }),
            event(E {
                player: Some("Bob"),
                actor: Some("ModTwo"),
                kind: "staff.kick",
                category: EventCategory::Staff,
                ..E::default()
            }),
        ])
        .unwrap();

    let found = f
        .store
        .query(&query(|q| q.actor = Some("modone".into())))
        .unwrap();

    assert_eq!(kinds(&found), ["staff.ban"]);
}

#[test]
fn filtering_by_category_and_server_narrows() {
    let f = fixture();
    f.store
        .append(&[
            event(E {
                category: EventCategory::Security,
                ..E::default()
            }),
            event(E::default()),
            event(E {
                category: EventCategory::Security,
                server: Some("server2"),
                ..E::default()
            }),
        ])
        .unwrap();

    let both = f
        .store
        .query(&query(|q| {
            q.category = Some(EventCategory::Security);
            q.server = Some("server1".into());
        }))
        .unwrap();
    assert_eq!(both.len(), 1);
    let security = f
        .store
        .query(&query(|q| q.category = Some(EventCategory::Security)))
        .unwrap();
    assert_eq!(security.len(), 2);
}

#[test]
fn the_time_window_is_respected() {
    let f = fixture();
    let now = Utc::now();
    f.store
        .append(&[
            event(E {
                kind: "old",
                at: Some(now - TimeDelta::hours(48)),
                ..E::default()
            }),
            event(E {
                kind: "recent",
                at: Some(now - TimeDelta::minutes(30)),
                ..E::default()
            }),
        ])
        .unwrap();

    let found = f
        .store
        .query(&query(|q| q.since = Some(now - TimeDelta::hours(6))))
        .unwrap();

    assert_eq!(kinds(&found), ["recent"]);
}

#[test]
fn a_hostile_name_is_a_parameter_not_sql() {
    let f = fixture();
    f.store.append(&[event(E::default())]).unwrap();

    let hostile = f
        .store
        .query(&query(|q| {
            q.player = Some("'; DROP TABLE events; --".into())
        }))
        .unwrap();

    assert!(hostile.is_empty());
    // The table is still there, which is the actual assertion.
    assert_eq!(f.store.count().unwrap(), 1);
}

// ---- limits ----

#[test]
fn the_limit_is_honoured_and_capped() {
    let f = fixture();
    let many: Vec<ServerEvent> = (0..50).map(|_| event(E::default())).collect();
    f.store.append(&many).unwrap();

    assert_eq!(f.store.query(&EventQuery::new(5)).unwrap().len(), 5);
    // Asking for more than the ceiling gets the ceiling, not everything.
    let huge = EventQuery::new(EventQuery::MAX_LIMIT * 10);
    assert_eq!(f.store.query(&huge).unwrap().len(), 50);
    assert_eq!(huge.effective_limit(), EventQuery::MAX_LIMIT);
}

// ---- retention ----

#[test]
fn pruning_removes_only_what_is_past_the_horizon() {
    let f = fixture();
    let now = Utc::now();
    f.store
        .append(&[
            event(E {
                kind: "ancient",
                at: Some(now - TimeDelta::days(120)),
                ..E::default()
            }),
            event(E {
                kind: "recent",
                at: Some(now - TimeDelta::days(1)),
                ..E::default()
            }),
        ])
        .unwrap();

    assert_eq!(f.store.prune(now - TimeDelta::days(90)).unwrap(), 1);
    assert_eq!(
        kinds(&f.store.query(&EventQuery::new(10)).unwrap()),
        ["recent"]
    );
}

// ---- normalisation ----

#[test]
fn overlong_fields_are_truncated_not_rejected() {
    let f = fixture();
    let mut long = event(E::default());
    long.detail = Some("x".repeat(ServerEvent::MAX_DETAIL * 3));
    f.store.append(&[long]).unwrap();

    let found = f.store.query(&EventQuery::new(10)).unwrap();

    assert_eq!(
        found[0].detail.as_ref().map(String::len),
        Some(ServerEvent::MAX_DETAIL)
    );
}

#[test]
fn blank_fields_become_null() {
    let f = fixture();
    f.store
        .append(&[event(E {
            player: Some("   "),
            server: Some(""),
            actor: None,
            ..E::default()
        })])
        .unwrap();

    let found = f.store.query(&EventQuery::new(10)).unwrap();

    assert_eq!(found[0].player, None);
    assert_eq!(found[0].server, None);
    assert_eq!(found[0].actor, None);
}

#[test]
fn grouping_counts_skip_nulls_and_name_categories_by_code() {
    let f = fixture();
    f.store
        .append(&[
            event(E::default()),
            event(E::default()),
            event(E {
                player: None,
                category: EventCategory::Server,
                kind: "server.restart",
                ..E::default()
            }),
        ])
        .unwrap();

    let by_player = f
        .store
        .count_by(EventGrouping::Player, &EventQuery::new(10))
        .unwrap();
    assert_eq!(by_player, [("Alice".to_owned(), 2)]);
    let by_category = f
        .store
        .count_by(EventGrouping::Category, &EventQuery::new(10))
        .unwrap();
    assert_eq!(by_category, [("1".to_owned(), 2), ("0".to_owned(), 1)]);
    assert_eq!(
        f.store
            .count_distinct(EventGrouping::Kind, &EventQuery::new(10))
            .unwrap(),
        2
    );
    assert_eq!(
        f.store
            .count_by(EventGrouping::Day, &EventQuery::new(10))
            .unwrap()
            .len(),
        1
    );
}

// ---- the reason this is a table ----

#[test]
fn append_cost_does_not_grow_with_the_table_size() {
    // A TIMING TEST, so the threshold is enormous: it fails only if appends became
    // super-linear, which is what a return to document storage would look like.
    let f = fixture();
    let batch_millis = || {
        let batch: Vec<ServerEvent> = (0..200).map(|_| event(E::default())).collect();
        let clock = Instant::now();
        f.store.append(&batch).unwrap();
        clock.elapsed().as_secs_f64() * 1000.0
    };

    let first = batch_millis();
    for _ in 0..50 {
        batch_millis();
    }
    let last = batch_millis();

    assert!(f.store.count().unwrap() > 10_000);
    assert!(
        last < first.max(50.0) * 10.0,
        "appending got {:.1}x slower as the table grew",
        last / first.max(0.01)
    );
}
