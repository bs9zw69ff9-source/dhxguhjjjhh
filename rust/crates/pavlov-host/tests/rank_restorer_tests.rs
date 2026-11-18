//! Port of `RankRestorerTests.cs`: a served suspension puts the member back at EXACTLY their
//! recorded rank, once.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use indexmap::IndexMap;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::factions::{FactionDefinition, FactionRegistry, FactionSet};
use pavlov_host::factions::rank_restorer::Suspensions;
use pavlov_host::factions::{RankRestorer, RankSuspension, RosterService};
use pavlov_storage::datasets::RANK_SUSPENSIONS;

fn now() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 6, 1, 12, 0, 0).unwrap()
}

fn gambino() -> FactionDefinition {
    FactionDefinition {
        name: "Gambino".into(),
        order: vec!["Member".into()],
        default: "Member".into(),
        rank_files: IndexMap::from([("Member".to_owned(), "gambinospawn.txt".to_owned())]),
        spawn_file: "gambinospawn.txt".into(),
        subclasses: IndexMap::new(),
        former_names: Vec::new(),
    }
}

struct Fixture {
    _dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    rosters: Arc<RosterService>,
    nypd: FactionDefinition,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let nypd = FactionRegistry::get(Some("NYPD")).unwrap().clone();
        let rosters = Arc::new(
            RosterService::new(Some(dir.path().to_path_buf()), dir.path().join("_bak"))
                .with_factions(FactionSet::of([gambino(), nypd.clone()])),
        );
        let _ = rosters.ensure_roster_files();
        Self {
            _dir: dir,
            store: Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new()))),
            rosters,
            nypd,
        }
    }

    fn restorer(&self) -> RankRestorer {
        RankRestorer::new(Arc::clone(&self.store), Arc::clone(&self.rosters))
    }

    async fn suspend(&self, player: &str, restore_to: &str, until: DateTime<Utc>) {
        let s = RankSuspension {
            player: player.into(),
            faction: "NYPD".into(),
            restore_to: restore_to.into(),
            suspended_by: "admin".into(),
            until,
        };
        self.store
            .update(
                RANK_SUSPENSIONS,
                Suspensions::new,
                move |mut all: Suspensions| {
                    all.insert(s.player.clone(), s);
                    Some(all)
                },
            )
            .await
            .unwrap();
    }

    fn suspensions(&self) -> Suspensions {
        self.store
            .read(RANK_SUSPENSIONS, Suspensions::new())
            .unwrap()
    }

    fn rank_of(&self, player: &str) -> Option<String> {
        self.rosters.find(player).map(|m| m.rank)
    }

    async fn put_at(&self, player: &str, rank: &str) {
        self.rosters.join(&self.nypd, player).await;
        if rank != self.nypd.default {
            self.rosters.set_rank(&self.nypd, player, rank).await;
        }
    }
}

#[tokio::test]
async fn a_removed_member_comes_back_at_exactly_their_rank() {
    let f = Fixture::new();
    f.suspend("Alice", "Sergeant", now() - TimeDelta::minutes(1))
        .await;

    assert_eq!(f.restorer().restore_expired(now()).await, 1);

    assert_eq!(f.rank_of("Alice").as_deref(), Some("Sergeant"));
    assert!(f.suspensions().is_empty());
}

#[tokio::test]
async fn a_member_re_whitelisted_during_the_suspension_is_not_promoted_past_their_rank() {
    let f = Fixture::new();
    f.put_at("Alice", "Corporal").await;
    f.suspend("Alice", "Sergeant", now() - TimeDelta::minutes(1))
        .await;

    f.restorer().restore_expired(now()).await;

    assert_eq!(f.rank_of("Alice").as_deref(), Some("Sergeant"));
}

#[tokio::test]
async fn a_member_already_above_their_rank_is_left_alone() {
    let f = Fixture::new();
    f.put_at("Alice", "Captain").await;
    f.suspend("Alice", "Sergeant", now() - TimeDelta::minutes(1))
        .await;

    f.restorer().restore_expired(now()).await;

    assert_eq!(f.rank_of("Alice").as_deref(), Some("Captain"));
    assert!(f.suspensions().is_empty());
}

#[tokio::test]
async fn running_again_changes_nothing() {
    // A suspension left in place used to be restored again every 30 seconds.
    let f = Fixture::new();
    f.suspend("Alice", "Sergeant", now() - TimeDelta::minutes(1))
        .await;

    for _ in 0..3 {
        f.restorer().restore_expired(now()).await;
    }

    assert_eq!(f.rank_of("Alice").as_deref(), Some("Sergeant"));
}

#[tokio::test]
async fn a_member_who_joined_another_faction_is_not_reported_restored() {
    let f = Fixture::new();
    f.rosters.join(&gambino(), "Alice").await;
    f.suspend("Alice", "Sergeant", now() - TimeDelta::minutes(1))
        .await;

    f.restorer().restore_expired(now()).await;

    assert_eq!(f.rosters.find("Alice").unwrap().faction, "Gambino");
    assert!(f.suspensions().is_empty()); // settled: nothing to retry
}

#[tokio::test]
async fn a_suspension_still_running_is_untouched() {
    let f = Fixture::new();
    f.suspend("Alice", "Sergeant", now() + TimeDelta::hours(1))
        .await;

    assert_eq!(f.restorer().restore_expired(now()).await, 0);

    assert!(f.rank_of("Alice").is_none());
    assert_eq!(f.suspensions().len(), 1);
}
