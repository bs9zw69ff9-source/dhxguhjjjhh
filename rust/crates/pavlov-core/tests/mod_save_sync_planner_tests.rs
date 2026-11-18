//! Port of `ModSaveSyncPlannerTests.cs`: the newest-wins decision that keeps every install's
//! ModSave tree identical. The cases that must produce NO copy matter most, because a sync that
//! mirrors the wrong direction moves a balance backwards.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::sync::{DEFAULT_TOLERANCE_MS, ModSaveCopy, ModSaveSnapshot, candidates};

fn install(root: &str, files: &[(&str, i64)]) -> ModSaveSnapshot {
    ModSaveSnapshot {
        root: root.into(),
        files: files.iter().map(|(r, m)| ((*r).to_owned(), *m)).collect(),
    }
}

fn plan(installs: &[ModSaveSnapshot]) -> Vec<ModSaveCopy> {
    candidates(installs, None, DEFAULT_TOLERANCE_MS)
}

#[test]
fn the_newest_copy_is_propagated_to_the_stale_install() {
    let a = install("/a", &[("econ/Bob.txt", 5_000)]);
    let b = install("/b", &[("econ/Bob.txt", 1_000)]);

    let copies = plan(&[a, b]);

    assert_eq!(
        copies,
        [ModSaveCopy {
            rel_path: "econ/Bob.txt".into(),
            from_root: "/a".into(),
            to_root: "/b".into(),
            modified_unix_ms: 5_000,
        }]
    );
}

#[test]
fn a_file_missing_from_one_install_is_copied_into_it() {
    let a = install("/a", &[("econ/Bob.txt", 5_000)]);
    let b = install("/b", &[]);

    let copies = plan(&[a, b]);

    assert_eq!(copies.len(), 1);
    assert_eq!(copies[0].to_root, "/b");
}

#[test]
fn copies_reach_every_other_install_not_just_one() {
    let a = install("/a", &[("f.txt", 9_000)]);
    let b = install("/b", &[("f.txt", 1_000)]);
    let c = install("/c", &[]);

    let copies = plan(&[a, b, c]);

    assert_eq!(copies.len(), 2);
    assert!(copies.iter().all(|x| x.from_root == "/a"));
    let mut to: Vec<&str> = copies.iter().map(|x| x.to_root.as_str()).collect();
    to.sort_unstable();
    assert_eq!(to, ["/b", "/c"]);
}

#[test]
fn a_destination_within_the_tolerance_window_is_left_alone() {
    // A copy re-stamps the destination from the source, so two copies a tick apart must read as
    // equal - otherwise they bounce between installs on every sweep forever.
    let a = install("/a", &[("f.txt", 10_000)]);
    let b = install("/b", &[("f.txt", 10_000 - (DEFAULT_TOLERANCE_MS - 1))]);

    assert!(plan(&[a, b]).is_empty());
}

#[test]
fn a_destination_older_than_the_tolerance_is_copied() {
    let a = install("/a", &[("f.txt", 10_000)]);
    let b = install("/b", &[("f.txt", 10_000 - (DEFAULT_TOLERANCE_MS + 1))]);

    let copies = plan(&[a, b]);
    assert_eq!(copies.len(), 1);
    assert_eq!(copies[0].to_root, "/b");
}

#[test]
fn skipped_paths_are_never_mirrored() {
    let a = install("/a", &[("rconplus/menuaccess.txt", 9_000)]);
    let b = install("/b", &[("rconplus/menuaccess.txt", 1_000)]);
    let skip = |rel: &str| rel.contains("menuaccess");

    assert!(candidates(&[a, b], Some(&skip), DEFAULT_TOLERANCE_MS).is_empty());
}

#[test]
fn one_install_is_nothing_to_sync() {
    let a = install("/a", &[("f.txt", 9_000)]);

    assert!(plan(&[a]).is_empty());
}

#[test]
fn identical_trees_produce_no_copies() {
    let a = install("/a", &[("f.txt", 5_000), ("g.txt", 6_000)]);
    let b = install("/b", &[("f.txt", 5_000), ("g.txt", 6_000)]);

    assert!(plan(&[a, b]).is_empty());
}

#[test]
fn the_output_is_deterministically_ordered() {
    let a = install("/a", &[("b.txt", 9_000), ("a.txt", 9_000)]);
    let b = install("/b", &[]);

    let copies = plan(&[a, b]);

    let rels: Vec<&str> = copies.iter().map(|c| c.rel_path.as_str()).collect();
    assert_eq!(rels, ["a.txt", "b.txt"]);
}
