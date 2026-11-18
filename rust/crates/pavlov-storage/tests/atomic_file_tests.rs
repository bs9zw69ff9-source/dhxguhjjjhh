//! Port of `AtomicFileTests.cs`: replacing a game file must not take it away from the game.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::fs;
use std::os::unix::fs::PermissionsExt;

use pavlov_storage::atomic_file::{OwnershipPlan, plan, write_text};
use pavlov_storage::ownership;

/// The conventional unprivileged uid/gid, standing in for the game's steam user.
const NOBODY: u32 = 65534;

fn files(dir: &std::path::Path) -> Vec<String> {
    let mut names: Vec<String> = fs::read_dir(dir)
        .unwrap()
        .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
        .collect();
    names.sort();
    names
}

#[test]
fn content_is_replaced_and_no_temp_file_is_left() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("Alice.txt");
    fs::write(&path, "100").unwrap();

    write_text(&path, "250").unwrap();

    assert_eq!(fs::read_to_string(&path).unwrap(), "250");
    assert_eq!(files(dir.path()), ["Alice.txt"]);
}

#[test]
fn the_mode_of_the_replaced_file_is_kept() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("blacklist.txt");
    fs::write(&path, "").unwrap();
    fs::set_permissions(&path, fs::Permissions::from_mode(0o660)).unwrap();

    write_text(&path, "someone").unwrap();

    assert_eq!(
        fs::metadata(&path).unwrap().permissions().mode() & 0o7777,
        0o660
    );
}

#[test]
fn ownership_can_be_read_at_all() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("probe.txt");
    fs::write(&path, "").unwrap();

    assert!(ownership::get(&path).is_ok());
}

#[test]
fn as_root_the_replaced_file_keeps_the_games_owner() {
    // Only root can give a file to another user, so this proves it only when run as root.
    if !ownership::is_privileged() {
        return;
    }
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("Alice.txt");
    fs::write(&path, "100").unwrap();
    ownership::set(&path, Some(NOBODY), Some(NOBODY)).unwrap();

    write_text(&path, "250").unwrap();

    assert_eq!(ownership::get(&path).unwrap(), (NOBODY, NOBODY));
}

#[test]
fn as_root_a_new_file_takes_its_directorys_owner() {
    if !ownership::is_privileged() {
        return;
    }
    let dir = tempfile::tempdir().unwrap();
    ownership::set(dir.path(), Some(NOBODY), Some(NOBODY)).unwrap();
    let path = dir.path().join("NewRoster.txt");

    write_text(&path, "").unwrap();

    assert_eq!(ownership::get(&path).unwrap(), (NOBODY, NOBODY));
}

#[test]
fn as_root_the_file_goes_back_to_its_owner() {
    assert_eq!(
        plan(true, (1000, 1000), (0, 0)),
        Some(OwnershipPlan {
            uid: Some(1000),
            gid: 1000,
            group_write: false
        })
    );
}

#[test]
fn unprivileged_the_file_goes_to_the_games_group_and_stays_group_writable() {
    assert_eq!(
        plan(false, (1000, 1000), (1001, 1001)),
        Some(OwnershipPlan {
            uid: None,
            gid: 1000,
            group_write: true
        })
    );
}

#[test]
fn nothing_changes_when_the_owner_already_matches() {
    assert_eq!(plan(false, (1001, 1001), (1001, 1001)), None);
}
