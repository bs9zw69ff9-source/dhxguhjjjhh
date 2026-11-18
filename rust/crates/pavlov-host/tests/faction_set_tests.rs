//! Port of `FactionSetTests.cs`: the built-in set, and a `FACTIONS_PATH` file replacing it.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::path::PathBuf;

use pavlov_core::factions::FactionRegistry;
use pavlov_host::factions::factions_file::{FactionsFileResult, load};

struct Dir(tempfile::TempDir);

impl Dir {
    fn new() -> Self {
        Self(tempfile::tempdir().unwrap())
    }

    fn write(&self, json: &str) -> PathBuf {
        let path = self.0.path().join("factions.json");
        std::fs::write(&path, json).unwrap();
        path
    }

    fn load(&self, json: &str) -> FactionsFileResult {
        load(&self.write(json))
    }
}

#[test]
fn the_built_in_set_is_the_default_and_is_valid() {
    assert!(std::ptr::eq(
        FactionRegistry::police(),
        FactionRegistry::default_set()
    ));
    assert!(FactionRegistry::default_set().problems().is_empty());
    assert_eq!(
        FactionRegistry::default_set().names().collect::<Vec<_>>(),
        ["GWPD"]
    );
}

#[test]
fn a_faction_file_replaces_the_built_in_set_entirely() {
    let loaded = Dir::new().load(
        r#"{ "factions": [
            { "name": "NCR", "spawnFile": "ncrspawn.txt",
              "ranks": [ { "name": "Recruit", "file": "ncrrecruit.txt", "cap": 40 },
                         { "name": "Ranger",  "file": "ncrranger.txt",  "cap": 5 } ] }
        ] }"#,
    );

    assert!(loaded.problems.is_empty(), "{:?}", loaded.problems);
    let set = loaded.set.unwrap();
    assert_eq!(set.names().collect::<Vec<_>>(), ["NCR"]);
    let ncr = set.get(Some("NCR")).unwrap();
    assert_eq!(ncr.default, "Recruit"); // the lowest rank, unstated
    assert_eq!(ncr.highest(), "Ranger");
    assert!(ncr.has_ranks());
}

#[test]
fn a_ladderless_faction_needs_only_its_one_file() {
    let loaded = Dir::new().load(
        r#"{ "factions": [ { "name": "Traders", "ranks": [ { "name": "Member", "file": "tradersspawn.txt" } ] } ] }"#,
    );
    let set = loaded.set.unwrap();
    let traders = set.get(Some("Traders")).unwrap();
    assert_eq!(traders.spawn_file, "tradersspawn.txt");
    assert!(!traders.has_ranks());
}

#[test]
fn comments_and_trailing_commas_are_accepted() {
    let loaded = Dir::new().load(
        r#"{
          // the ladders
          "factions": [
            { "name": "Legion", /* one rank for now */ "ranks": [ { "name": "Recruit", "file": "legionrecruit.txt" }, ] },
          ],
        }"#,
    );
    assert!(loaded.problems.is_empty(), "{:?}", loaded.problems);
}

#[test]
fn two_factions_sharing_a_roster_file_is_refused() {
    let loaded = Dir::new().load(
        r#"{ "factions": [
            { "name": "NCR",    "ranks": [ { "name": "Member", "file": "shared.txt" } ] },
            { "name": "Legion", "ranks": [ { "name": "Member", "file": "shared.txt" } ] }
        ] }"#,
    );
    assert!(loaded.set.is_none());
    assert!(
        loaded.problems.iter().any(|p| p.contains("shared.txt")),
        "{:?}",
        loaded.problems
    );
}

#[test]
fn a_default_rank_that_is_not_a_rank_is_refused() {
    let loaded = Dir::new().load(
        r#"{ "factions": [ { "name": "NCR", "default": "General", "ranks": [ { "name": "Recruit", "file": "ncrrecruit.txt" } ] } ] }"#,
    );
    assert!(loaded.set.is_none());
    assert!(
        loaded.problems.iter().any(|p| p.contains("General")),
        "{:?}",
        loaded.problems
    );
}

#[test]
fn a_faction_with_no_ranks_is_refused() {
    let loaded = Dir::new().load(r#"{ "factions": [ { "name": "Ghosts" } ] }"#);
    assert!(loaded.set.is_none());
    assert!(loaded.problems.iter().any(|p| p.contains("Ghosts")));
}

#[test]
fn broken_json_is_reported_with_its_position_rather_than_thrown() {
    let loaded = Dir::new().load(r#"{ "factions": [ "#);
    assert!(loaded.set.is_none());
    assert!(!loaded.problems.is_empty());
    assert!(loaded.problems[0].contains("line"), "{:?}", loaded.problems);
}

#[test]
fn a_missing_file_is_refused_rather_than_falling_back_to_the_built_ins() {
    let dir = Dir::new();
    let loaded = load(&dir.0.path().join("nope.json"));
    assert!(loaded.set.is_none());
    assert!(loaded.problems.iter().any(|p| p.contains("does not exist")));
}

#[test]
fn every_problem_is_reported_at_once() {
    let loaded = Dir::new().load(
        r#"{ "factions": [
            { "name": "A", "default": "Nope", "ranks": [ { "name": "One", "file": "a.txt" } ] },
            { "name": "B", "default": "Also", "ranks": [ { "name": "One", "file": "b.txt" } ] }
        ] }"#,
    );
    assert!(loaded.problems.len() >= 2, "{:?}", loaded.problems);
}

#[test]
fn a_file_that_still_sets_caps_loads_and_says_how_many_were_ignored() {
    let loaded = Dir::new().load(
        r#"{ "factions": [ { "name": "NCR", "ranks": [
            { "name": "Recruit", "file": "ncrrecruit.txt", "cap": 40 },
            { "name": "Trooper", "file": "ncrtrooper.txt", "cap": 25 } ] } ] }"#,
    );
    assert!(loaded.set.is_some());
    assert!(loaded.problems.is_empty());
    assert_eq!(loaded.ignored_rank_caps, 2);
}

#[test]
fn a_file_with_no_caps_reports_none_ignored() {
    let loaded = Dir::new().load(
        r#"{ "factions": [ { "name": "NCR", "ranks": [
            { "name": "Recruit", "file": "ncrrecruit.txt" },
            { "name": "Trooper", "file": "ncrtrooper.txt", "cap": 0 } ] } ] }"#,
    );
    assert_eq!(loaded.ignored_rank_caps, 0);
}
