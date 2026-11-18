//! Port of `HoldRanksEndToEndTests.cs`: the hold-ranks preference from the member index through
//! the roster writer, wired exactly as the composition root wires it (resolved per call, by name).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;

use chrono::Utc;
use common::file_store;
use pavlov_core::factions::{FactionDefinition, FactionRegistry};
use pavlov_host::factions::{FactionMember, FactionMembers, RosterService};

const DISCORD: u64 = 424_242_424_242_424_242;

fn nypd() -> FactionDefinition {
    FactionRegistry::get(Some("NYPD")).unwrap().clone()
}

struct Fixture {
    _dir: tempfile::TempDir,
    members: Arc<FactionMembers>,
    rosters: RosterService,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let members = Arc::new(FactionMembers::new(file_store(&dir.path().join("data"))));
        let index = Arc::clone(&members);
        let rosters = RosterService::new(Some(dir.path().to_path_buf()), dir.path().join("_bak"))
            .with_factions(FactionRegistry::police().clone())
            .with_holds_all_ranks(Arc::new(move |name| index.holds_all_ranks(name)));
        Self {
            _dir: dir,
            members,
            rosters,
        }
    }

    async fn remember(&self, hold: bool) {
        self.members
            .remember(
                DISCORD,
                FactionMember {
                    faction: "NYPD".into(),
                    name: "Alice".into(),
                    at: Utc::now(),
                    by: "Mod".into(),
                    holds_all_ranks: hold,
                },
            )
            .await
            .unwrap();
    }

    fn holds(&self, rank_index: usize) -> bool {
        let nypd = nypd();
        self.rosters
            .read(&nypd.rank_files[&nypd.order[rank_index]])
            .unwrap_or_default()
            .iter()
            .any(|n| n == "Alice")
    }
}

#[tokio::test]
async fn the_flag_survives_the_store() {
    let f = Fixture::new();
    f.remember(true).await;

    assert!(f.members.of(DISCORD).unwrap().holds_all_ranks);
    assert!(f.members.holds_all_ranks("Alice"));
}

#[tokio::test]
async fn the_flag_is_found_by_name_whatever_the_casing() {
    let f = Fixture::new();
    f.remember(true).await;

    assert!(f.members.holds_all_ranks("alice"));
    assert!(f.members.holds_all_ranks("ALICE"));
}

#[tokio::test]
async fn a_promotion_keeps_every_rank_below() {
    let f = Fixture::new();
    f.remember(true).await;
    f.rosters.join(&nypd(), "Alice").await;

    f.rosters.change_rank(&nypd(), "Alice", 1).await;
    f.rosters.change_rank(&nypd(), "Alice", 1).await;

    for i in 0..3 {
        assert!(f.holds(i), "missing from rank {i}");
    }
}

#[tokio::test]
async fn without_the_flag_only_the_current_rank_is_held() {
    let f = Fixture::new();
    f.remember(false).await;
    f.rosters.join(&nypd(), "Alice").await;

    f.rosters.change_rank(&nypd(), "Alice", 1).await;

    assert!(f.holds(1));
    assert!(!f.holds(0));
}

#[tokio::test]
async fn re_recording_without_the_flag_is_what_turns_it_off() {
    // THE MECHANISM: an omitted option read as false wrote false over a set true.
    let f = Fixture::new();
    f.remember(true).await;
    assert!(f.members.holds_all_ranks("Alice"));

    f.remember(false).await;

    assert!(!f.members.holds_all_ranks("Alice"));
}

/// What `/whitelist add` does with the optional `hold_ranks`: `None` keeps what is stored.
fn effective(supplied: Option<bool>, members: &FactionMembers) -> bool {
    supplied
        .or_else(|| members.of(DISCORD).map(|m| m.holds_all_ranks))
        .unwrap_or(false)
}

#[tokio::test]
async fn an_omitted_option_now_keeps_whatever_was_already_set() {
    let f = Fixture::new();
    f.remember(true).await;
    assert!(effective(None, &f.members));
}

#[tokio::test]
async fn an_explicit_false_still_turns_it_off() {
    let f = Fixture::new();
    f.remember(true).await;
    assert!(!effective(Some(false), &f.members));
}

#[tokio::test]
async fn a_promotion_after_the_flag_is_cleared_strips_the_ranks_below() {
    let f = Fixture::new();
    f.remember(true).await;
    f.rosters.join(&nypd(), "Alice").await;
    f.rosters.change_rank(&nypd(), "Alice", 1).await;
    assert!(f.holds(0));

    f.remember(false).await;
    f.rosters.change_rank(&nypd(), "Alice", 1).await;

    assert!(!f.holds(0));
    assert!(!f.holds(1));
    assert!(f.holds(2));
}

#[tokio::test]
async fn forgetting_a_faction_and_the_missing_counts_members() {
    // Rust-only: the bulk forgets report how many went, and only those.
    let f = Fixture::new();
    f.remember(true).await;
    f.members
        .remember(
            7,
            FactionMember {
                faction: "Other".into(),
                name: "Bob".into(),
                at: Utc::now(),
                by: "Mod".into(),
                holds_all_ranks: false,
            },
        )
        .await
        .unwrap();

    assert_eq!(f.members.in_faction("nypd").len(), 1);
    assert_eq!(f.members.owner_of("BOB"), Some(7));
    assert_eq!(
        f.members.forget_missing(&["alice".into()]).await.unwrap(),
        1
    );
    assert_eq!(f.members.forget_faction("NYPD").await.unwrap(), 1);
    assert!(f.members.of(DISCORD).is_none());
}
