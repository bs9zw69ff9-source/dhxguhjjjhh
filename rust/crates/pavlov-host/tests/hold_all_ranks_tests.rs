//! Port of `HoldAllRanksTests.cs`: a member set to hold ranks keeps every rank file at or below
//! their own (each one grants its gear in game), and the set only ever shrinks on a demotion.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_core::factions::{FactionDefinition, FactionRegistry};
use pavlov_host::factions::RosterService;

fn nypd() -> FactionDefinition {
    FactionRegistry::get(Some("NYPD")).unwrap().clone()
}

struct Fixture {
    dir: tempfile::TempDir,
}

impl Fixture {
    fn new() -> Self {
        Self {
            dir: tempfile::tempdir().unwrap(),
        }
    }

    fn service(&self, holding: bool) -> RosterService {
        let service =
            RosterService::new(Some(self.dir.path().to_path_buf()), self.dir.path().into())
                .with_factions(FactionRegistry::police().clone());
        if holding {
            service.with_holds_all_ranks(Arc::new(|_| true))
        } else {
            service
        }
    }

    fn contents(&self, file: &str) -> Vec<String> {
        std::fs::read_to_string(self.dir.path().join(file))
            .map(|t| {
                t.lines()
                    .filter(|l| !l.trim().is_empty())
                    .map(str::to_owned)
                    .collect()
            })
            .unwrap_or_default()
    }

    fn holds(&self, rank: &str, name: &str) -> bool {
        self.contents(&nypd().rank_files[rank])
            .iter()
            .any(|n| n == name)
    }
}

#[tokio::test]
async fn a_promotion_keeps_every_rank_below_the_new_one() {
    let f = Fixture::new();
    let rosters = f.service(true);
    rosters.join(&nypd(), "Alice").await;

    rosters.change_rank(&nypd(), "Alice", 1).await;
    rosters.change_rank(&nypd(), "Alice", 1).await;

    assert!(f.holds("Cadet", "Alice"));
    assert!(f.holds("Patrolman", "Alice"));
    assert!(f.holds("Corporal", "Alice"));
    assert!(!f.holds("Sergeant", "Alice"));
}

#[tokio::test]
async fn without_the_flag_a_promotion_still_moves_them_between_files() {
    let f = Fixture::new();
    let rosters = f.service(false);
    rosters.join(&nypd(), "Bob").await;

    rosters.change_rank(&nypd(), "Bob", 1).await;

    assert!(!f.holds("Cadet", "Bob"));
    assert!(f.holds("Patrolman", "Bob"));
    let mentions: usize = nypd()
        .rank_files
        .values()
        .map(|file| f.contents(file).iter().filter(|n| *n == "Bob").count())
        .sum();
    assert_eq!(mentions, 1);
}

#[tokio::test]
async fn a_demotion_still_takes_back_what_is_now_above_them() {
    let f = Fixture::new();
    let rosters = f.service(true);
    rosters.join(&nypd(), "Cara").await;
    rosters.change_rank(&nypd(), "Cara", 1).await;
    rosters.change_rank(&nypd(), "Cara", 1).await;

    rosters.change_rank(&nypd(), "Cara", -1).await;

    assert!(!f.holds("Corporal", "Cara"));
    assert!(f.holds("Patrolman", "Cara"));
    assert!(f.holds("Cadet", "Cara"));
}

#[tokio::test]
async fn their_rank_is_still_reported_as_the_highest_one_they_hold() {
    // Reporting the lowest would make every promotion move them from Cadet again, forever.
    let f = Fixture::new();
    let rosters = f.service(true);
    rosters.join(&nypd(), "Dana").await;
    rosters.change_rank(&nypd(), "Dana", 1).await;
    rosters.change_rank(&nypd(), "Dana", 1).await;

    assert_eq!(rosters.find("Dana").unwrap().rank, "Corporal");
}

#[tokio::test]
async fn leaving_clears_every_rank_they_held() {
    let f = Fixture::new();
    let rosters = f.service(true);
    rosters.join(&nypd(), "Eve").await;
    rosters.change_rank(&nypd(), "Eve", 1).await;

    rosters.leave(&nypd(), "Eve").await;

    for file in nypd().rank_files.values() {
        assert!(!f.contents(file).iter().any(|n| n == "Eve"), "{file}");
    }
    assert!(!f.contents(&nypd().spawn_file).iter().any(|n| n == "Eve"));
}

#[tokio::test]
async fn they_are_still_in_the_spawn_file() {
    let f = Fixture::new();
    let rosters = f.service(true);
    rosters.join(&nypd(), "Frank").await;
    rosters.change_rank(&nypd(), "Frank", 1).await;

    assert!(f.contents(&nypd().spawn_file).iter().any(|n| n == "Frank"));
}
