//! Port of `FactionSourceTests.cs` (both classes): the startup line naming where the factions
//! came from, and when a missing file may fall back to a named set.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::path::Path;
use std::time::{Duration, SystemTime};

use pavlov_host::factions::factions_file::{describe_source, may_fall_back_to_set};

fn cwd() -> &'static Path {
    Path::new("/srv/bot")
}

#[test]
fn an_unset_path_says_so_rather_than_showing_nothing() {
    assert_eq!(
        describe_source(None, None, cwd()),
        "built in (neither FACTION_SET nor FACTIONS_PATH set)"
    );
}

#[test]
fn a_named_set_is_reported_by_name() {
    assert!(describe_source(None, Some("police"), cwd()).contains("police"));
}

#[test]
fn the_file_is_named_even_when_a_set_name_is_also_set() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("factions.json");
    std::fs::write(&file, "{}").unwrap();

    let described = describe_source(Some(&file), Some("police"), cwd());
    assert!(described.contains(&file.display().to_string()));
    assert!(!described.contains("police"));
}

#[test]
fn a_relative_path_is_resolved_to_the_file_actually_read() {
    // Resolved against the directory the process stands in, passed in rather than changed
    // under every other test running in this process.
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("factions.json");
    std::fs::write(&file, "{}").unwrap();

    let described = describe_source(Some(Path::new("factions.json")), None, dir.path());
    assert!(
        described.contains(&file.display().to_string()),
        "{described}"
    );
    assert!(!described.contains("built in"));
}

#[test]
fn the_last_edit_is_stamped_so_an_undeployed_change_is_visible() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("factions.json");
    std::fs::write(&file, "{}").unwrap();
    // 2026-03-04 05:06:00 UTC.
    let edited = SystemTime::UNIX_EPOCH + Duration::from_secs(1_772_600_760);
    std::fs::File::options()
        .write(true)
        .open(&file)
        .unwrap()
        .set_modified(edited)
        .unwrap();

    assert!(describe_source(Some(&file), None, cwd()).contains("2026-03-04 05:06Z"));
}

#[test]
fn a_missing_file_still_resolves_rather_than_throwing() {
    let dir = tempfile::tempdir().unwrap();
    let missing = dir.path().join("absent.json");
    assert_eq!(
        describe_source(Some(&missing), None, cwd()),
        missing.display().to_string()
    );
}

#[test]
fn the_summary_names_the_set_it_fell_back_to() {
    let dir = tempfile::tempdir().unwrap();
    let line = describe_source(Some(&dir.path().join("absent.json")), Some("police"), cwd());
    assert!(line.contains("police"));
    assert!(line.contains("does not exist"));
}

#[test]
fn a_missing_file_falls_back_to_the_set_when_one_is_named() {
    let dir = tempfile::tempdir().unwrap();
    assert!(may_fall_back_to_set(
        Some(&dir.path().join("absent.json")),
        Some("police")
    ));
}

#[test]
fn with_no_set_named_it_is_still_a_fatal_misconfiguration() {
    let dir = tempfile::tempdir().unwrap();
    let absent = dir.path().join("absent.json");
    assert!(!may_fall_back_to_set(Some(&absent), None));
    assert!(!may_fall_back_to_set(Some(&absent), Some("")));
    assert!(!may_fall_back_to_set(Some(&absent), Some("polcie"))); // a typo is not a set
    assert!(!may_fall_back_to_set(Some(&absent), Some("fallout"))); // nor is a removed one
}

#[test]
fn a_file_thatexists_is_never_skipped() {
    let dir = tempfile::tempdir().unwrap();
    let present = dir.path().join("present.json");
    std::fs::write(&present, "{}").unwrap();
    assert!(!may_fall_back_to_set(Some(&present), Some("police")));
}
