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
//! IT NEVER PROPOSES A DELETE. A file present in one install and not another is copied IN. A
//! sync that deleted would turn one install's missing file into everyone's missing file.

use std::collections::HashMap;

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
