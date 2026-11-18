//! Port of `RosterFileCreationTests.cs`: creating the roster files the factions expect, at
//! startup, so the whole set is visible on disk from the first start. The report always says
//! what it did, because "off", "nothing to do" and "could not write" all used to be silence.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::path::Path;
use std::sync::Arc;

use indexmap::IndexMap;
use pavlov_core::factions::{FactionDefinition, FactionSet};
use pavlov_host::factions::RosterService;
use pavlov_storage::game_files::GameFileGuard;

fn faction(
    name: &str,
    spawn: &str,
    ranks: &[(&str, &str)],
    subclasses: &[(&str, &str)],
) -> FactionDefinition {
    FactionDefinition {
        name: name.into(),
        order: ranks.iter().map(|(r, _)| (*r).to_owned()).collect(),
        default: ranks[0].0.into(),
        rank_files: ranks
            .iter()
            .map(|(r, f)| ((*r).to_owned(), (*f).to_owned()))
            .collect(),
        spawn_file: spawn.into(),
        subclasses: subclasses
            .iter()
            .map(|(s, f)| ((*s).to_owned(), (*f).to_owned()))
            .collect::<IndexMap<_, _>>(),
        former_names: Vec::new(),
    }
}

fn two_factions() -> FactionSet {
    FactionSet::of([
        faction(
            "NCR",
            "ncrspawn.txt",
            &[("Recruit", "ncrrecruit.txt"), ("Trooper", "ncrtrooper.txt")],
            &[("Ranger", "ncrranger.txt")],
        ),
        faction(
            "Legion",
            "legionspawn.txt",
            &[("Recruit", "legionrecruit.txt")],
            &[],
        ),
    ])
}

fn service(dir: &Path) -> RosterService {
    RosterService::new(Some(dir.to_path_buf()), dir.join("_bak")).with_factions(two_factions())
}

#[test]
fn every_roster_file_a_faction_owns_is_listed() {
    assert_eq!(
        RosterService::roster_files_of(&two_factions()),
        [
            "legionrecruit.txt",
            "legionspawn.txt",
            "ncrranger.txt",
            "ncrrecruit.txt",
            "ncrspawn.txt",
            "ncrtrooper.txt",
        ]
    );
}

#[test]
fn a_file_serving_as_both_spawn_and_rank_is_listed_once() {
    let set = FactionSet::of([faction(
        "Gambino",
        "gambinospawn.txt",
        &[("Member", "gambinospawn.txt")],
        &[],
    )]);
    assert_eq!(RosterService::roster_files_of(&set), ["gambinospawn.txt"]);
}

#[test]
fn missing_files_are_created_empty_and_existing_ones_are_left_alone() {
    let dir = tempfile::tempdir().unwrap();
    let populated = dir.path().join("ncrspawn.txt");
    std::fs::write(&populated, "SomeExistingPlayer\n").unwrap();

    let service = service(dir.path());
    let report = service.ensure_roster_files();

    assert_eq!(report.created, 5); // six owned, one already there
    assert_eq!(report.present, 1);
    assert_eq!(report.expected, 6);
    assert!(report.failed.is_empty());
    for file in RosterService::roster_files_of(&two_factions()) {
        assert!(dir.path().join(&file).exists(), "{file} should exist");
    }
    assert_eq!(
        std::fs::read_to_string(&populated).unwrap(),
        "SomeExistingPlayer\n"
    );

    // A second start creates nothing, and says so in terms of files that ARE there.
    let again = service.ensure_roster_files();
    assert_eq!(again.created, 0);
    assert_eq!(again.present, 6);
    assert!(again.summary.contains("6 already there"));
}

#[test]
fn no_roster_directory_means_no_files_and_no_directory_created() {
    let dir = tempfile::tempdir().unwrap();
    let missing = dir.path().join("pavlov-absent");

    let report = service(&missing).ensure_roster_files();

    assert_eq!(report.created, 0);
    assert!(
        !missing.exists(),
        "the roster directory must never be created"
    );
    assert!(report.summary.contains("FACTION_ROLES_PATH"));
    assert!(report.summary.contains(missing.to_str().unwrap()));
    assert!(report.summary.contains("does not exist"));
}

#[test]
fn no_roster_path_configured_says_so_rather_than_reporting_zero() {
    let service =
        RosterService::new(None, "/nonexistent/_bak".into()).with_factions(two_factions());

    let report = service.ensure_roster_files();

    assert_eq!(report.created, 0);
    assert_eq!(report.expected, 6);
    assert!(report.summary.contains("FACTION_ROLES_PATH not set"));
}

#[test]
fn a_file_the_guard_refuses_is_reported_by_name() {
    let dir = tempfile::tempdir().unwrap();
    let guarded = dir.path().join("ncrspawn.txt");
    let guard = Arc::new(GameFileGuard::new(&[guarded.to_str().unwrap().to_owned()]));

    let report = service(dir.path()).with_guard(guard).ensure_roster_files();

    assert_eq!(report.created, 5);
    assert!(!guarded.exists());
    assert_eq!(report.failed.len(), 1);
    assert!(report.failed[0].starts_with("ncrspawn.txt: "));
    assert!(report.summary.contains("COULD NOT BE CREATED"));
}

#[test]
fn a_missing_roster_directory_inside_an_install_is_created_with_its_files() {
    // THE REPROVISION CASE: Pavlov/Saved/Config exists, ModSave does not.
    let root = tempfile::tempdir().unwrap();
    let config = root.path().join("pavlovserver/Pavlov/Saved/Config");
    std::fs::create_dir_all(&config).unwrap();
    let rosters = config.join("ModSave/FactionRoles");

    let report = RosterService::new(Some(rosters.clone()), root.path().join("_bak"))
        .with_factions(two_factions())
        .ensure_roster_files();

    assert!(rosters.is_dir());
    assert_eq!(report.created, report.expected);
    for file in RosterService::roster_files_of(&two_factions()) {
        assert!(rosters.join(&file).exists(), "{file} was not created");
    }
    assert!(report.summary.contains("directory created"));
}

#[test]
fn an_install_with_no_config_folder_is_not_built_out() {
    let root = tempfile::tempdir().unwrap();
    let base = root.path().join("pavlovserver");
    let rosters = base.join("Pavlov/Saved/Config/ModSave/FactionRoles");

    let report = RosterService::new(Some(rosters), root.path().join("_bak"))
        .with_factions(two_factions())
        .ensure_roster_files();

    assert_eq!(report.created, 0);
    assert!(!base.exists());
    assert!(report.summary.contains("is the server installed"));
}
