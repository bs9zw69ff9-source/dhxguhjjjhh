//! Port of `SpawnFactionTests.cs`: ladderless factions, and the membership index that ties a
//! Discord account to a roster entry.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::collections::HashSet;

use chrono::Utc;
use common::file_store;
use indexmap::IndexMap;
use pavlov_core::factions::{FactionDefinition, FactionRegistry};
use pavlov_core::text::case::fold_case;
use pavlov_host::factions::{FactionMember, FactionMembers};

fn spawn_only(name: &str) -> FactionDefinition {
    let file = format!("{}spawn.txt", name.to_lowercase());
    FactionDefinition {
        name: name.into(),
        order: vec!["Member".into()],
        default: "Member".into(),
        spawn_file: file.clone(),
        rank_files: IndexMap::from([("Member".to_owned(), file)]),
        subclasses: IndexMap::new(),
        former_names: vec![],
    }
}

struct Rig {
    _dir: tempfile::TempDir,
    members: FactionMembers,
}

fn members() -> Rig {
    let dir = tempfile::tempdir().unwrap();
    let members = FactionMembers::new(file_store(dir.path()));
    Rig { _dir: dir, members }
}

fn member(faction: &str, name: &str, by: &str) -> FactionMember {
    FactionMember {
        faction: faction.into(),
        name: name.into(),
        at: Utc::now(),
        by: by.into(),
        holds_all_ranks: false,
    }
}

#[test]
fn the_mafias_are_gone_from_the_police_bot() {
    for name in ["Gambino", "Colombo"] {
        assert!(FactionRegistry::police().get(Some(name)).is_none());
        assert!(FactionRegistry::get(Some(name)).is_none());
        assert!(
            !FactionRegistry::police()
                .names()
                .any(|n| n.eq_ignore_ascii_case(name))
        );
    }
}

#[test]
fn a_one_rank_faction_has_no_ladder() {
    let faction = spawn_only("Gambino");
    assert!(!faction.has_ranks());
    assert_eq!(faction.order.len(), 1);
    assert_eq!(faction.rank_files[&faction.default], faction.spawn_file);
}

#[test]
fn nypd_still_has_its_ladder() {
    // NYPD is the GWPD now: looked up by its former name.
    let nypd = FactionRegistry::get(Some("NYPD")).unwrap();
    assert!(nypd.has_ranks());
    assert_eq!(nypd.order.len(), 8);
}

#[test]
fn each_faction_writes_its_own_file() {
    let files: Vec<String> = FactionRegistry::police()
        .all()
        .flat_map(|f| f.rank_files.values().map(|v| fold_case(v)))
        .collect();
    let distinct: HashSet<&String> = files.iter().collect();
    assert_eq!(distinct.len(), files.len());
}

#[tokio::test]
async fn a_membership_is_found_by_discord_account() {
    let r = members();
    r.members
        .remember(42, member("Gambino", "Butter Life", "mod"))
        .await
        .unwrap();

    let found = r.members.of(42).unwrap();
    assert_eq!(found.faction, "Gambino");
    assert_eq!(found.name, "Butter Life");
}

#[tokio::test]
async fn recording_again_replaces_rather_than_duplicates() {
    let r = members();
    r.members
        .remember(42, member("Gambino", "Old Name", "mod"))
        .await
        .unwrap();
    r.members
        .remember(42, member("NYPD", "New Name", "mod"))
        .await
        .unwrap();

    assert_eq!(r.members.of(42).unwrap().faction, "NYPD");
    assert_eq!(r.members.in_faction("NYPD").len(), 1);
    assert!(r.members.in_faction("Gambino").is_empty());
}

#[tokio::test]
async fn forgetting_leaves_nothing_behind() {
    let r = members();
    r.members
        .remember(42, member("Colombo", "Alice", "mod"))
        .await
        .unwrap();
    r.members.forget(42).await.unwrap();
    assert!(r.members.of(42).is_none());
}

#[tokio::test]
async fn forgetting_somebody_with_no_record_is_harmless() {
    let r = members();
    r.members.forget(999).await.unwrap();
    assert!(r.members.of(999).is_none());
}

#[tokio::test]
async fn a_snowflake_survives_the_round_trip() {
    const BIG: u64 = 1_014_251_293_159_731_310;
    let r = members();
    r.members
        .remember(BIG, member("NYPD", "Alice", "mod"))
        .await
        .unwrap();

    assert_eq!(r.members.in_faction("NYPD")[0].0, BIG);
    assert_eq!(r.members.of(BIG).unwrap().name, "Alice");
}

#[tokio::test]
async fn the_index_follows_the_roster_files() {
    let r = members();
    r.members
        .remember(1, member("NYPD", "StillHere", "mod"))
        .await
        .unwrap();
    r.members
        .remember(2, member("NYPD", "RemovedByHand", "mod"))
        .await
        .unwrap();

    let dropped = r
        .members
        .forget_missing(&["StillHere".to_owned()])
        .await
        .unwrap();

    assert_eq!(dropped, 1);
    assert!(r.members.of(1).is_some());
    assert!(r.members.of(2).is_none());
}

#[tokio::test]
async fn an_empty_roster_does_not_wipe_the_index_silently() {
    let r = members();
    r.members
        .remember(1, member("NYPD", "Alice", "mod"))
        .await
        .unwrap();
    assert_eq!(r.members.forget_missing(&[]).await.unwrap(), 1);
}

#[tokio::test]
async fn wiping_a_faction_forgets_only_that_factions_records() {
    let r = members();
    r.members
        .remember(1, member("NYPD", "Officer", "owner"))
        .await
        .unwrap();
    r.members
        .remember(2, member("NYPD", "Sergeant", "owner"))
        .await
        .unwrap();
    r.members
        .remember(3, member("Gambino", "Wiseguy", "owner"))
        .await
        .unwrap();

    assert_eq!(r.members.forget_faction("NYPD").await.unwrap(), 2);
    assert!(r.members.of(1).is_none());
    assert!(r.members.of(2).is_none());
    assert!(r.members.of(3).is_some());
}

#[tokio::test]
async fn forgetting_a_faction_with_no_records_changes_nothing() {
    let r = members();
    r.members
        .remember(1, member("NYPD", "Officer", "owner"))
        .await
        .unwrap();
    assert_eq!(r.members.forget_faction("Colombo").await.unwrap(), 0);
    assert!(r.members.of(1).is_some());
}

#[tokio::test]
async fn forgetting_a_faction_ignores_case() {
    let r = members();
    r.members
        .remember(1, member("NYPD", "Officer", "owner"))
        .await
        .unwrap();
    assert_eq!(r.members.forget_faction("nypd").await.unwrap(), 1);
}
