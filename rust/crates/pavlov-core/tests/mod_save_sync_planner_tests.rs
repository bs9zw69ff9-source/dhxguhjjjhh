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

// ---- Deletes propagate from the last sweep's manifest (owner, 2026-11-19) ----

use pavlov_core::sync::{ModSaveDelete, ModSaveManifest, deletions};

fn manifest(entries: &[(&str, &[(&str, i64)])]) -> ModSaveManifest {
    entries
        .iter()
        .map(|(root, files)| {
            (
                (*root).to_owned(),
                files.iter().map(|(r, m)| ((*r).to_owned(), *m)).collect(),
            )
        })
        .collect()
}

fn deletes(installs: &[ModSaveSnapshot], previous: &ModSaveManifest) -> Vec<ModSaveDelete> {
    deletions(installs, previous, None, DEFAULT_TOLERANCE_MS)
}

#[test]
fn a_file_deleted_in_one_install_is_deleted_in_the_others() {
    let previous = manifest(&[
        ("/a", &[("Bob.json", 5_000)]),
        ("/b", &[("Bob.json", 5_000)]),
        ("/c", &[("Bob.json", 5_000)]),
    ]);
    let a = install("/a", &[]);
    let b = install("/b", &[("Bob.json", 5_000)]);
    let c = install("/c", &[("Bob.json", 5_000)]);

    assert_eq!(
        deletes(&[a, b, c], &previous),
        [
            ModSaveDelete {
                rel_path: "Bob.json".into(),
                root: "/b".into(),
                modified_unix_ms: 5_000
            },
            ModSaveDelete {
                rel_path: "Bob.json".into(),
                root: "/c".into(),
                modified_unix_ms: 5_000
            },
        ]
    );
}

#[test]
fn without_history_a_missing_file_is_never_a_delete() {
    let a = install("/a", &[]);
    let b = install("/b", &[("Bob.json", 5_000)]);

    assert!(deletes(&[a, b], &ModSaveManifest::new()).is_empty());
}

#[test]
fn a_new_install_with_no_record_cannot_delete_anything() {
    let previous = manifest(&[("/a", &[("Bob.json", 5_000)])]);
    let a = install("/a", &[("Bob.json", 5_000)]);
    let fresh = install("/fresh", &[]);

    assert!(deletes(&[a, fresh], &previous).is_empty());
}

#[test]
fn an_edit_after_the_delete_wins_and_nothing_is_deleted() {
    let previous = manifest(&[
        ("/a", &[("Bob.json", 5_000)]),
        ("/b", &[("Bob.json", 5_000)]),
    ]);
    let a = install("/a", &[]);
    let b = install("/b", &[("Bob.json", 5_000 + DEFAULT_TOLERANCE_MS + 1)]);

    assert!(deletes(&[a.clone(), b.clone()], &previous).is_empty());
    // ...and newest-wins then copies the edit back into the install that deleted it.
    assert_eq!(plan(&[a, b]).len(), 1);
}

#[test]
fn a_change_inside_the_tolerance_still_counts_as_untouched() {
    let previous = manifest(&[
        ("/a", &[("Bob.json", 5_000)]),
        ("/b", &[("Bob.json", 5_000)]),
    ]);
    let a = install("/a", &[]);
    let b = install("/b", &[("Bob.json", 5_000 + DEFAULT_TOLERANCE_MS)]);

    assert_eq!(deletes(&[a, b], &previous).len(), 1);
}

#[test]
fn a_copy_that_appeared_since_the_last_sweep_is_kept() {
    let previous = manifest(&[("/a", &[("Bob.json", 5_000)]), ("/b", &[])]);
    let a = install("/a", &[]);
    let b = install("/b", &[("Bob.json", 9_000)]);

    assert!(deletes(&[a, b], &previous).is_empty());
}

#[test]
fn an_install_that_sits_the_sweep_out_deletes_nothing() {
    // /a held the file and is not passed in (its ModSave could not be listed).
    let previous = manifest(&[
        ("/a", &[("Bob.json", 5_000)]),
        ("/b", &[("Bob.json", 5_000)]),
        ("/c", &[("Bob.json", 5_000)]),
    ]);
    let b = install("/b", &[("Bob.json", 5_000)]);
    let c = install("/c", &[("Bob.json", 5_000)]);

    assert!(deletes(&[b, c], &previous).is_empty());
}

#[test]
fn skipped_paths_are_never_deleted() {
    let previous = manifest(&[
        ("/a", &[("keep/Bob.json", 5_000)]),
        ("/b", &[("keep/Bob.json", 5_000)]),
    ]);
    let a = install("/a", &[]);
    let b = install("/b", &[("keep/Bob.json", 5_000)]);
    let skip = |rel: &str| rel.starts_with("keep/");

    assert!(deletions(&[a, b], &previous, Some(&skip), DEFAULT_TOLERANCE_MS).is_empty());
}

#[test]
fn a_file_gone_everywhere_needs_no_delete() {
    let previous = manifest(&[
        ("/a", &[("Bob.json", 5_000)]),
        ("/b", &[("Bob.json", 5_000)]),
    ]);

    assert!(deletes(&[install("/a", &[]), install("/b", &[])], &previous).is_empty());
}
