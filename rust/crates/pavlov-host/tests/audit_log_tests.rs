//! Port of `AuditLogTests.cs` (all but the staff-board test, which joins with the boards in the
//! Discord crate).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::collections::HashSet;
use std::sync::Arc;

use chrono::{TimeDelta, Utc};
use common::file_store;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::time::SystemClock;
use pavlov_host::moderation::audit::MAX_ENTRIES;
use pavlov_host::moderation::{AuditLog, ModAction};
use pavlov_storage::datasets::MOD_LOG;

fn audit() -> (tempfile::TempDir, AuditLog) {
    let dir = tempfile::tempdir().unwrap();
    let log = AuditLog::new(file_store(dir.path()), Arc::new(SystemClock));
    (dir, log)
}

#[tokio::test]
async fn actions_are_recorded_and_read_back() {
    let (_dir, audit) = audit();
    audit
        .record("tempban", "Dana", "Alice", Some("Cheating"))
        .await
        .unwrap();

    let all = audit.all();
    assert_eq!(all.len(), 1);
    assert_eq!(all[0].action, "tempban");
    assert_eq!(all[0].moderator, "Dana");
    assert_eq!(all[0].player, "Alice");
    assert_eq!(all[0].reason.as_deref(), Some("Cheating"));
}

#[tokio::test]
async fn by_moderator_is_most_recent_first() {
    let (_dir, audit) = audit();
    audit.record("kick", "Dana", "Alice", None).await.unwrap();
    audit.record("tempban", "Dana", "Bob", None).await.unwrap();
    audit.record("kick", "Sam", "Carol", None).await.unwrap();

    let dana = audit.by("Dana", None);
    assert_eq!(dana.len(), 2);
    assert_eq!(dana[0].player, "Bob");
}

fn ties(times: impl Iterator<Item = chrono::DateTime<Utc>>, count: usize) -> bool {
    times.collect::<HashSet<_>>().len() < count
}

#[tokio::test]
async fn actions_in_the_same_millisecond_still_read_newest_first() {
    let (_dir, audit) = audit();
    for i in 0..20 {
        audit
            .record("kick", "Dana", &format!("Player{i}"), None)
            .await
            .unwrap();
    }

    let dana = audit.by("Dana", None);
    // Self-validating: if no two shared a millisecond, this run tested nothing.
    assert!(
        ties(dana.iter().map(|a| a.at), dana.len()),
        "no two entries shared a millisecond"
    );
    let expected: Vec<String> = (0..20).rev().map(|i| format!("Player{i}")).collect();
    assert_eq!(
        dana.iter().map(|a| a.player.clone()).collect::<Vec<_>>(),
        expected
    );
}

#[tokio::test]
async fn against_a_player_is_also_newest_first_within_a_millisecond() {
    let (_dir, audit) = audit();
    for i in 0..20 {
        audit
            .record("kick", &format!("Mod{i}"), "Alice", None)
            .await
            .unwrap();
    }

    let against = audit.against("Alice");
    assert!(
        ties(against.iter().map(|a| a.at), against.len()),
        "no two entries shared a millisecond"
    );
    let expected: Vec<String> = (0..20).rev().map(|i| format!("Mod{i}")).collect();
    assert_eq!(
        against
            .iter()
            .map(|a| a.moderator.clone())
            .collect::<Vec<_>>(),
        expected
    );
}

#[tokio::test]
async fn by_moderator_can_be_windowed() {
    let (_dir, audit) = audit();
    audit.record("kick", "Dana", "Alice", None).await.unwrap();
    assert_eq!(
        audit
            .by("Dana", Some(Utc::now() - TimeDelta::minutes(5)))
            .len(),
        1
    );
    assert!(
        audit
            .by("Dana", Some(Utc::now() + TimeDelta::minutes(5)))
            .is_empty()
    );
}

#[tokio::test]
async fn against_a_player_finds_everything_done_to_them() {
    let (_dir, audit) = audit();
    audit.record("kick", "Dana", "Alice", None).await.unwrap();
    audit.record("tempban", "Sam", "alice", None).await.unwrap(); // case-insensitive
    assert_eq!(audit.against("Alice").len(), 2);
}

#[tokio::test]
async fn the_log_is_bounded_and_trims_the_oldest_first() {
    // Seeded at the limit in one write, then pushed past it through the real path: the same
    // trim, without five thousand rewrites of a growing log.
    let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())));
    let seeded: Vec<ModAction> = (0..MAX_ENTRIES)
        .map(|i| ModAction {
            action: "kick".into(),
            moderator: "Dana".into(),
            player: format!("Player{i}"),
            reason: None,
            at: Utc::now(),
        })
        .collect();
    store.write(MOD_LOG, &seeded).await.unwrap();
    let audit = AuditLog::new(store, Arc::new(SystemClock));
    for i in MAX_ENTRIES..MAX_ENTRIES + 20 {
        audit
            .record("kick", "Dana", &format!("Player{i}"), None)
            .await
            .unwrap();
    }

    let all = audit.all();
    assert_eq!(all.len(), MAX_ENTRIES);
    assert!(!all.iter().any(|a| a.player == "Player0"));
    assert!(
        all.iter()
            .any(|a| a.player == format!("Player{}", MAX_ENTRIES + 19))
    );
}

#[tokio::test]
async fn a_reason_cannot_carry_a_newline_into_the_record() {
    let (_dir, audit) = audit();
    audit
        .record("tempban", "Dana", "Alice", Some("line one\nline two"))
        .await
        .unwrap();
    assert_eq!(audit.all()[0].reason.as_deref(), Some("line one line two"));
}
