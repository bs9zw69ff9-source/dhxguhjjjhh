//! Port of `GameFileGuardTests.cs`. REAL DIRECTORIES, because the guard COMPOSES the older rule
//! (the bot never writes into a directory that does not exist); against imaginary paths every
//! case would be refused for that reason instead.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::fs;
use std::path::Path;

use pavlov_storage::game_files::GameFileGuard;

fn guarding(paths: &[&Path]) -> GameFileGuard {
    let paths: Vec<String> = paths
        .iter()
        .map(|p| p.to_string_lossy().into_owned())
        .collect();
    GameFileGuard::new(&paths)
}

fn dir(root: &Path, parts: &[&str]) -> std::path::PathBuf {
    let path = parts
        .iter()
        .fold(root.to_path_buf(), |p, part| p.join(part));
    fs::create_dir_all(&path).unwrap();
    path
}

fn file(root: &Path, parts: &[&str]) -> String {
    let (name, folders) = parts.split_last().unwrap();
    dir(root, folders).join(name).to_string_lossy().into_owned()
}

#[test]
fn a_file_inside_an_ignored_directory_is_refused() {
    let root = tempfile::tempdir().unwrap();
    let guard = guarding(&[&dir(root.path(), &["pavlovserver"])]);

    let problem = guard
        .problem(Some(&file(
            root.path(),
            &["pavlovserver", "Pavlov", "Saved", "Config", "blacklist.txt"],
        )))
        .unwrap();

    assert!(problem.contains("IGNORE_PATHS"));
}

#[test]
fn the_ignored_path_itself_is_refused() {
    // Naming a FILE rather than a directory has to work.
    let root = tempfile::tempdir().unwrap();
    let path = file(root.path(), &["pavlovserver", "blacklist.txt"]);

    assert!(guarding(&[Path::new(&path)]).problem(Some(&path)).is_some());
}

#[test]
fn a_sibling_directory_with_the_same_prefix_is_not_covered() {
    let root = tempfile::tempdir().unwrap();
    let guard = guarding(&[&dir(root.path(), &["pavlovserver"])]);

    assert_eq!(
        guard.problem(Some(&file(
            root.path(),
            &["pavlovserver-backup", "blacklist.txt"]
        ))),
        None
    );
    assert_eq!(
        guard.problem(Some(&file(
            root.path(),
            &["pavlovserver2", "blacklist.txt"]
        ))),
        None
    );
}

#[test]
fn nothing_is_refused_when_nothing_is_ignored() {
    let none = GameFileGuard::none();
    assert!(!none.any());
    assert!(none.paths().is_empty());
}

#[test]
fn blank_entries_are_dropped_rather_than_matching_everything() {
    // An empty string turned into a full path is the CURRENT DIRECTORY.
    let root = tempfile::tempdir().unwrap();
    let guard = GameFileGuard::new(&[String::new(), "   ".into()]);

    assert!(!guard.any());
    assert_eq!(
        guard.problem(Some(&file(root.path(), &["pavlovserver", "blacklist.txt"]))),
        None
    );
}

#[test]
fn the_existing_directory_rule_still_applies() {
    let root = tempfile::tempdir().unwrap();
    let guard = guarding(&[&dir(root.path(), &["somewhere-else"])]);

    let missing = root.path().join("no-such-directory").join("file.txt");
    let problem = guard.problem(Some(&missing.to_string_lossy())).unwrap();

    assert!(!problem.contains("IGNORE_PATHS"));
    assert!(problem.contains("does not exist"));
}

#[test]
fn a_path_that_is_not_usable_is_reported_rather_than_thrown() {
    let root = tempfile::tempdir().unwrap();
    let guard = guarding(&[&dir(root.path(), &["pavlovserver"])]);

    assert!(guard.problem(Some("")).is_some());
    assert!(guard.problem(None).is_some());
}

#[test]
fn ignoring_a_parent_directory_also_covers_the_rosters_inside_it() {
    let root = tempfile::tempdir().unwrap();
    let modsave = dir(root.path(), &["ModSave"]);
    let rosters = dir(root.path(), &["ModSave", "FactionRoles"]);
    let roster = rosters
        .join("policecadet.txt")
        .to_string_lossy()
        .into_owned();

    assert!(guarding(&[&modsave]).problem(Some(&roster)).is_some());

    // Naming the files instead leaves the rosters writable, which is the documented fix.
    let blacklist = modsave.join("blacklist.txt");
    let by_file = guarding(&[&blacklist]);
    assert_eq!(by_file.problem(Some(&roster)), None);
    assert!(
        by_file
            .problem(Some(&blacklist.to_string_lossy()))
            .is_some()
    );
}

#[test]
fn pavlovserver_and_pavlovserver2_are_separate_trees() {
    let root = tempfile::tempdir().unwrap();
    let first = dir(root.path(), &["pavlovserver"]);
    let second = dir(root.path(), &["pavlovserver2"]);
    let in_first = first.join("roster.txt").to_string_lossy().into_owned();
    let in_second = second.join("roster.txt").to_string_lossy().into_owned();

    let normal_bot = guarding(&[&second]); // owns the first install
    let clone = guarding(&[&first]); // owns the second

    assert_eq!(normal_bot.problem(Some(&in_first)), None);
    assert!(normal_bot.problem(Some(&in_second)).is_some());
    assert_eq!(clone.problem(Some(&in_second)), None);
    assert!(clone.problem(Some(&in_first)).is_some());
}
