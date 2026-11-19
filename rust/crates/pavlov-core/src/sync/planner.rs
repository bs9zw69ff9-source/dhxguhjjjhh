//! Which files to propagate to keep every install's ModSave tree identical (C#
//! `Core/Sync/ModSaveSyncPlanner.cs`).
//!
//! A PORT OF THE NODE BOT'S `syncAllModSave`, and it stays a port on purpose: this is what
//! stopped a player's caps differing between server 1 and server 2.
//!
//! CONVERGENT NEWEST-WINS. For every relative path, the copy with the most recent modified time
//! is the source, propagated to every install whose copy is older or missing. The destination is
//! stamped with the source's time, so once two installs hold the same file the sweep stops
//! touching them.
//!
//! A FILE THAT IS SIMPLY ABSENT IS COPIED IN. Absence alone never deletes anything: a new
//! install, or one whose save was never written, receives the file.
//!
//! DELETES PROPAGATE, FROM MEMORY (owner, 2026-11-19: "I cleared the modsave files and it
//! reverted back"). [`deletions`] compares this sweep with the [`ModSaveManifest`] recorded
//! after the last one. A file an install HELD then and lacks now was deleted there, and is
//! deleted everywhere else, unless another install's copy changed since that sweep: an edit
//! made after the delete (the game saving a player who rejoined) wins and is copied back.

use std::collections::{BTreeMap, HashMap, HashSet};

use indexmap::IndexMap;

/// Filesystem timestamps are not infinitely precise and a copy re-stamps the destination; this
/// stops a sub-tick difference reading as "stale" and bouncing a file between installs.
pub const DEFAULT_TOLERANCE_MS: i64 = 2000;

/// One install's ModSave tree, as relative paths to their modified time.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModSaveSnapshot {
    /// The install root, echoed back on every copy that comes from it.
    pub root: String,
    /// Relative path (forward-slashed) to last-modified, Unix milliseconds.
    pub files: HashMap<String, i64>,
}

/// What each install held after the last sweep: install root, then relative path to modified
/// time (Unix ms). Persisted between sweeps and restarts; it is what tells a deleted file from
/// one that was never there.
pub type ModSaveManifest = BTreeMap<String, BTreeMap<String, i64>>;

/// A file to delete from one install, because another install deleted it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModSaveDelete {
    pub rel_path: String,
    /// The install still holding the file.
    pub root: String,
    /// The modified time the file had when planned. It is deleted only if it still has it,
    /// so a save the game rewrote meanwhile is kept.
    pub modified_unix_ms: i64,
}

/// The files to delete because an install that held them at the last sweep no longer does.
///
/// Rules, each one a guard against deleting something nobody deleted:
///
/// - Only an install present in `previous` can be the one that deleted: a new install has no
///   history, so its missing files are copied in, never read as deletions.
/// - Callers pass only installs whose ModSave could be listed. One that could not is left out
///   and so cannot delete anything (and its record should be dropped, not kept: see
///   `ModSaveSync`).
/// - A file is deleted only where its copy is unchanged since the last sweep (within
///   `tolerance_ms`) and was recorded there. A copy that is newer, or that appeared since, is
///   an edit after the delete, and the file is left to newest-wins instead.
/// - `skip` paths are never deleted, as they are never mirrored.
#[must_use]
pub fn deletions(
    installs: &[ModSaveSnapshot],
    previous: &ModSaveManifest,
    skip: Option<&dyn Fn(&str) -> bool>,
    tolerance_ms: i64,
) -> Vec<ModSaveDelete> {
    if installs.len() < 2 {
        return Vec::new();
    }
    // Every path some install held last time and no longer has.
    let mut deleted: HashSet<&str> = HashSet::new();
    for install in installs {
        let Some(before) = previous.get(&install.root) else {
            continue;
        };
        for rel in before.keys() {
            if !install.files.contains_key(rel) && !skip.is_some_and(|s| s(rel)) {
                deleted.insert(rel.as_str());
            }
        }
    }

    let mut deletes = Vec::new();
    for rel in deleted {
        let holders: Vec<(&ModSaveSnapshot, i64)> = installs
            .iter()
            .filter_map(|i| i.files.get(rel).map(|&m| (i, m)))
            .collect();
        let untouched = holders.iter().all(|(install, modified)| {
            previous
                .get(&install.root)
                .and_then(|files| files.get(rel))
                .is_some_and(|&recorded| *modified <= recorded.saturating_add(tolerance_ms))
        });
        if !untouched {
            continue;
        }
        for (install, modified) in holders {
            deletes.push(ModSaveDelete {
                rel_path: rel.to_owned(),
                root: install.root.clone(),
                modified_unix_ms: modified,
            });
        }
    }
    deletes.sort_by(|a, b| {
        a.rel_path
            .cmp(&b.rel_path)
            .then_with(|| a.root.cmp(&b.root))
    });
    deletes
}

/// A single file to copy from one install into another.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModSaveCopy {
    pub rel_path: String,
    /// The install holding the newest copy.
    pub from_root: String,
    /// The install whose copy is stale or absent.
    pub to_root: String,
    /// The source's modified time, stamped onto the destination so the two then compare equal.
    pub modified_unix_ms: i64,
}

/// The copies that would make every install's tree hold the newest of each file.
///
/// `skip` returning true for a relative path means it is never mirrored: the RCON+ menu-access
/// files are managed live, and blindly newest-winning them wipes a player's menu.
#[must_use]
pub fn candidates(
    installs: &[ModSaveSnapshot],
    skip: Option<&dyn Fn(&str) -> bool>,
    tolerance_ms: i64,
) -> Vec<ModSaveCopy> {
    if installs.len() < 2 {
        return Vec::new();
    }

    // rel -> the install holding its newest copy, and that copy's modified time. The first
    // install to reach a time wins a tie, as in the C#.
    let mut newest: IndexMap<&str, (&str, i64)> = IndexMap::new();
    for install in installs {
        for (rel, &modified) in &install.files {
            if skip.is_some_and(|s| s(rel)) {
                continue;
            }
            let entry = newest
                .entry(rel.as_str())
                .or_insert((&install.root, modified));
            if modified > entry.1 {
                *entry = (&install.root, modified);
            }
        }
    }

    let mut copies = Vec::new();
    for (rel, (winner_root, winner_modified)) in newest {
        for install in installs {
            if install.root == winner_root {
                continue;
            }
            // Absent is a copy; present-and-recent is not. The tolerance is one-sided.
            let stale = install
                .files
                .get(rel)
                .is_none_or(|&dest| dest < winner_modified.saturating_sub(tolerance_ms));
            if stale {
                copies.push(ModSaveCopy {
                    rel_path: rel.to_owned(),
                    from_root: winner_root.to_owned(),
                    to_root: install.root.clone(),
                    modified_unix_ms: winner_modified,
                });
            }
        }
    }

    // Deterministic order, so the sweep's log and the tests read the same every run.
    copies.sort_by(|a, b| {
        a.rel_path
            .cmp(&b.rel_path)
            .then_with(|| a.to_root.cmp(&b.to_root))
    });
    copies
}
