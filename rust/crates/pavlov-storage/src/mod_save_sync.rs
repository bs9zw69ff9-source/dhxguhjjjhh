//! Keeping every install's ModSave tree identical (C# `Storage/ModSaveSync.cs`).
//!
//! The planner (`pavlov_core::sync`) decides from modified times; this reads only the files a
//! candidate touches and applies the content guards:
//!
//! - NO ONLINE GUARD (owner, 2026-11-18): the mod writes a player's in-game state to their
//!   save live, so the newest file is the current one whether they are online or not.
//! - THE MONEY GUARD: an empty-or-zero file never lands on a positive balance.
//! - THE ROSTER GUARD: an empty roster from anywhere but the folder the bot edits never wipes a
//!   filled one (a fresh server's empty file is not a `/whitelist wipe`).
//!
//! The menu tier lists in `ServerRoles` mirror like any other file: they are plain whitelists
//! now that RCON+ is gone, and the roster guard keeps an empty one from wiping a filled one.
//!
//! DELETES PROPAGATE (owner, 2026-11-19). After every sweep the files each install holds are
//! recorded in the bot's store ([`MODSAVE_SYNC_STATE`]); the next sweep deletes, everywhere,
//! a file one install had and lost, unless another copy changed since (see
//! `pavlov_core::sync::deletions`). Deleted copies are removed outright, by owner choice: there
//! is no trash folder. Two guards keep that from spreading an accident:
//!
//! - An install whose ModSave folder is missing or unreadable still RECEIVES files (that is how
//!   a fresh server gets its folder), but its record is ignored and dropped, so it deletes
//!   nothing: a reprovisioned server comes back as a new install, not as a mass delete.
//! - A file is deleted only while it still has the modified time it was planned with, so a
//!   save the game rewrote mid-sweep is kept, and wins on the next one.

use std::collections::{BTreeMap, HashMap};
use std::fs::{self, File};
use std::path::{Path, PathBuf};
use std::time::{Duration, UNIX_EPOCH};

use std::collections::HashSet;
use std::sync::Arc;

use pavlov_core::data::KeyValueBackend;
use pavlov_core::servers::installs::mod_save_path;
use pavlov_core::sync::ledger_file::would_wipe_balance;
use pavlov_core::sync::{
    DEFAULT_TOLERANCE_MS, ModSaveDelete, ModSaveManifest, ModSaveSnapshot, candidates, deletions,
};
use pavlov_core::text::case::{contains_ignore_case, starts_with_ignore_case};

use crate::atomic_file;
use crate::datasets::MODSAVE_SYNC_STATE;
use crate::game_files::{config_path, create_owned_like_parent, inside_install};

/// The sync across a box's installs.
pub struct ModSaveSync {
    installs: Vec<String>,
    enabled: bool,
    skip: Vec<String>,
    /// The roster folder the bot edits, relative to ModSave with a trailing `/`, and its install.
    roster: Option<(String, String)>,
    /// Where the last sweep's [`ModSaveManifest`] is kept. Without one, deletes cannot be told
    /// from absence, so none propagate (the old copy-only behaviour).
    state: Option<Arc<dyn KeyValueBackend>>,
}

impl std::fmt::Debug for ModSaveSync {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ModSaveSync")
            .field("installs", &self.installs)
            .field("enabled", &self.enabled)
            .finish_non_exhaustive()
    }
}

fn modified_unix_ms(path: &Path) -> Option<i64> {
    let modified = fs::metadata(path)
        .ok()
        .filter(|m| m.is_file())?
        .modified()
        .ok()?;
    let ms = match modified.duration_since(UNIX_EPOCH) {
        Ok(after) => i64::try_from(after.as_millis()).ok()?,
        Err(before) => -i64::try_from(before.duration().as_millis()).ok()?,
    };
    Some(ms)
}

fn set_modified(path: &Path, unix_ms: i64) -> std::io::Result<()> {
    let at = if unix_ms >= 0 {
        UNIX_EPOCH + Duration::from_millis(unix_ms.unsigned_abs())
    } else {
        UNIX_EPOCH - Duration::from_millis(unix_ms.unsigned_abs())
    };
    File::options().write(true).open(path)?.set_modified(at)
}

/// Every file under `root` (not `.tmp` writes in flight), as forward-slashed relative paths to
/// their modified time. `None` when `root` itself cannot be listed (missing or unreadable): that
/// is "this install is not here", which must never read as "every file was deleted".
fn list_tree(root: &Path) -> Option<HashMap<String, i64>> {
    fs::read_dir(root).ok()?;
    let mut files = HashMap::new();
    let mut pending = vec![root.to_path_buf()];
    while let Some(dir) = pending.pop() {
        let Ok(entries) = fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                pending.push(path);
                continue;
            }
            if path.to_string_lossy().ends_with(".tmp") {
                continue;
            }
            let (Some(modified), Ok(rel)) = (modified_unix_ms(&path), path.strip_prefix(root))
            else {
                continue;
            };
            let rel: Vec<String> = rel
                .components()
                .map(|c| c.as_os_str().to_string_lossy().into_owned())
                .collect();
            files.insert(rel.join("/"), modified);
        }
    }
    Some(files)
}

/// A relative path made of plain names only: no `..`, no root, no prefix. Every path deleted
/// comes from a listing, but the check holds even if the stored manifest were tampered with.
fn plain_relative(rel: &str) -> bool {
    !rel.is_empty()
        && Path::new(rel)
            .components()
            .all(|c| matches!(c, std::path::Component::Normal(_)))
}

fn read_text(path: &Path) -> Option<String> {
    fs::read(path)
        .ok()
        .map(|b| String::from_utf8_lossy(&b).into_owned())
}

fn folder_name(path: &str) -> String {
    Path::new(path)
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default()
}

impl ModSaveSync {
    /// `roster_directory` is the faction roster folder the bot edits (inside one install's
    /// ModSave); `extra_skip` is `MODSAVE_SYNC_SKIP_EXTRA`.
    #[must_use]
    pub fn new(
        installs: Vec<String>,
        enabled: bool,
        extra_skip: &[String],
        roster_directory: Option<&str>,
    ) -> Self {
        let roster = roster_directory.filter(|d| !d.is_empty()).and_then(|dir| {
            let full = std::path::absolute(dir).ok()?;
            installs.iter().find_map(|root| {
                let modsave = std::path::absolute(mod_save_path(root)).ok()?;
                let rel = full.strip_prefix(&modsave).ok()?;
                if rel.as_os_str().is_empty() {
                    return None;
                }
                let rel: Vec<String> = rel
                    .components()
                    .map(|c| c.as_os_str().to_string_lossy().into_owned())
                    .collect();
                Some((format!("{}/", rel.join("/")), root.clone()))
            })
        });
        let skip = extra_skip
            .iter()
            .map(|s| s.trim().to_owned())
            .filter(|s| !s.is_empty())
            .collect();
        Self {
            installs,
            enabled,
            skip,
            roster,
            state: None,
        }
    }

    /// Remember each sweep's result in `state`, so deletes propagate. The bot passes its store.
    #[must_use]
    pub fn with_state(mut self, state: Arc<dyn KeyValueBackend>) -> Self {
        self.state = Some(state);
        self
    }

    #[must_use]
    pub fn enabled(&self) -> bool {
        self.enabled
    }

    /// Enabled, and more than one install to sync between.
    #[must_use]
    pub fn can_sync(&self) -> bool {
        self.enabled && self.installs.len() > 1
    }

    fn skipped(&self, rel: &str) -> bool {
        self.skip.iter().any(|term| contains_ignore_case(rel, term))
    }

    /// Every install's ModSave as it is now, and the roots whose ModSave could not be listed.
    /// Those take part as empty, so they receive files, but they are never a source of
    /// deletes (see [`Self::sweep`]).
    fn snapshots(&self) -> (Vec<ModSaveSnapshot>, HashSet<String>) {
        let mut absent = HashSet::new();
        let snapshots = self
            .installs
            .iter()
            .map(|root| {
                let files = list_tree(Path::new(&mod_save_path(root))).unwrap_or_else(|| {
                    absent.insert(root.clone());
                    HashMap::new()
                });
                ModSaveSnapshot {
                    root: root.clone(),
                    files,
                }
            })
            .collect();
        (snapshots, absent)
    }

    /// The manifest the last sweep recorded. Unreadable reads as empty, which only means no
    /// delete propagates this sweep: the safe direction.
    fn load_manifest(&self) -> ModSaveManifest {
        let Some(state) = &self.state else {
            return ModSaveManifest::new();
        };
        match state.read(MODSAVE_SYNC_STATE) {
            Ok(None) => ModSaveManifest::new(),
            Ok(Some(raw)) => serde_json::from_str(&raw).unwrap_or_else(|error| {
                tracing::warn!(%error, "ModSave sync state is unreadable; no deletes propagate until it is rewritten");
                ModSaveManifest::new()
            }),
            Err(error) => {
                tracing::warn!(%error, "ModSave sync state could not be read; no deletes propagate this sweep");
                ModSaveManifest::new()
            }
        }
    }

    /// Record what every listable install holds now. An install that still cannot be listed is
    /// left out, so it comes back as a new install rather than as a mass delete.
    fn save_manifest(&self, previous: &ModSaveManifest) {
        let Some(state) = &self.state else {
            return;
        };
        let (snapshots, absent) = self.snapshots();
        let now: ModSaveManifest = snapshots
            .into_iter()
            .filter(|s| !absent.contains(&s.root))
            .map(|s| (s.root, s.files.into_iter().collect()))
            .collect();
        if &now == previous {
            return;
        }
        let written = serde_json::to_string(&now)
            .map_err(std::io::Error::other)
            .and_then(|json| state.write(MODSAVE_SYNC_STATE, &json));
        if let Err(error) = written {
            tracing::warn!(%error, "ModSave sync state could not be saved; a delete made before the next save will not propagate");
        }
    }

    /// Delete one planned copy, only if it is still the file that was planned.
    fn delete_mirrored(&self, delete: &ModSaveDelete) -> bool {
        if !plain_relative(&delete.rel_path) {
            tracing::warn!(rel = %delete.rel_path, "ModSave sync refused to delete a path that is not a plain relative path");
            return false;
        }
        let path = Path::new(&mod_save_path(&delete.root)).join(&delete.rel_path);
        if modified_unix_ms(&path) != Some(delete.modified_unix_ms) {
            tracing::info!(path = %path.display(), "ModSave sync kept a file that changed while it was being deleted");
            return false;
        }
        match fs::remove_file(&path) {
            Ok(()) => {
                tracing::info!(rel = %delete.rel_path, install = folder_name(&delete.root), "ModSave sync deleted a file another install deleted");
                true
            }
            Err(error) => {
                tracing::warn!(path = %path.display(), %error, "ModSave sync could not delete");
                false
            }
        }
    }

    /// One pass over every install: how many files were copied. Deletions are logged. Blocking
    /// file I/O.
    #[must_use]
    pub fn sweep(&self) -> usize {
        if !self.can_sync() {
            return 0;
        }
        let (snapshots, absent) = self.snapshots();
        let previous = self.load_manifest();
        let skip = |rel: &str| self.skipped(rel);

        // An install with no listable ModSave lost nothing on purpose: its record must not
        // read every file it held as deleted.
        let mut history = previous.clone();
        history.retain(|root, _| !absent.contains(root));
        let planned_deletes = deletions(&snapshots, &history, Some(&skip), DEFAULT_TOLERANCE_MS);
        let deleted_paths: HashSet<&str> = planned_deletes
            .iter()
            .map(|d| d.rel_path.as_str())
            .collect();
        let deleted = planned_deletes
            .iter()
            .filter(|delete| self.delete_mirrored(delete))
            .count();
        if deleted > 0 {
            tracing::info!(deleted, "ModSave sync propagated deletions");
        }

        // A path being deleted is not also copied back from a copy that survived its delete.
        let skip_or_deleted = |rel: &str| self.skipped(rel) || deleted_paths.contains(rel);
        let planned = candidates(&snapshots, Some(&skip_or_deleted), DEFAULT_TOLERANCE_MS);

        // Grouped by source so its content is read once.
        let mut by_source: BTreeMap<(String, String), Vec<_>> = BTreeMap::new();
        for copy in planned {
            by_source
                .entry((copy.rel_path.clone(), copy.from_root.clone()))
                .or_default()
                .push(copy);
        }

        let mut copied = 0;
        for ((rel, from_root), copies) in by_source {
            let source = Path::new(&mod_save_path(&from_root)).join(&rel);
            let Ok(content) = fs::read(&source) else {
                continue;
            };
            let text = String::from_utf8_lossy(&content).into_owned();

            for copy in copies {
                let dest = Path::new(&mod_save_path(&copy.to_root)).join(&rel);
                if would_wipe_balance(Some(&text), read_text(&dest).as_deref()) {
                    tracing::debug!(%rel, install = folder_name(&copy.to_root), "ModSave sync refused to overwrite a balance with nothing");
                    continue;
                }
                if self.would_blank_roster(&rel, &from_root, &text, &dest) {
                    tracing::warn!(%rel, install = folder_name(&copy.to_root), source = folder_name(&from_root), "ModSave sync refused to overwrite a filled roster with an empty one");
                    continue;
                }
                if self.write_mirrored(&copy.to_root, &dest, &content, copy.modified_unix_ms) {
                    copied += 1;
                }
            }
        }
        if copied > 0 {
            tracing::info!(
                copied,
                installs = self.installs.len(),
                "ModSave sync propagated files"
            );
        }
        self.save_manifest(&previous);
        copied
    }

    fn would_blank_roster(&self, rel: &str, from_root: &str, source: &str, dest: &Path) -> bool {
        let Some((prefix, roster_install)) = &self.roster else {
            return false;
        };
        if !starts_with_ignore_case(rel, prefix) || from_root == roster_install {
            return false;
        }
        if !source.trim().is_empty() {
            return false;
        }
        read_text(dest).is_some_and(|existing| !existing.trim().is_empty())
    }

    /// Copy into an install. A missing folder is created, but ONLY inside a real install
    /// (`Pavlov/Saved/Config` exists), owned like Config so the game can still write into it.
    /// The source's modified time is carried over so the two copies then compare equal.
    fn write_mirrored(&self, install: &str, dest: &Path, content: &[u8], modified: i64) -> bool {
        let Some(directory) = dest.parent() else {
            return false;
        };
        if !directory.is_dir() {
            if !config_path(install).is_dir() {
                return false;
            }
            if let Err(error) = create_owned_like_parent(directory) {
                tracing::warn!(path = %dest.display(), %error, "ModSave sync could not create a folder");
                return false;
            }
            tracing::info!(directory = %directory.display(), install = folder_name(install), "ModSave sync created a folder");
        }
        if let Err(error) = atomic_file::write(dest, content) {
            tracing::warn!(path = %dest.display(), %error, "ModSave sync could not write");
            return false;
        }
        // Best effort: the right bytes landed, and convergence just waits for the next change.
        if let Err(error) = set_modified(dest, modified) {
            tracing::debug!(path = %dest.display(), %error, "could not carry the modified time over");
        }
        true
    }

    /// Lines for `/diagnose`: whether sync is on, across what, and anything that will stop it.
    #[must_use]
    pub fn diagnose(&self, mod_save_setting: Option<&str>) -> Vec<String> {
        if !self.enabled {
            return vec!["cross-install ModSave sync is OFF (MODSAVE_SYNC=off)".into()];
        }
        if self.installs.len() < 2 {
            return vec![format!(
                "only {} install found, so cross-install sync does nothing. If you run more, set PAVLOV_BASES to list them",
                self.installs.len()
            )];
        }
        let mut lines = vec![format!(
            "cross-install ModSave sync across {} installs: {}",
            self.installs.len(),
            self.installs
                .iter()
                .map(|i| folder_name(i))
                .collect::<Vec<_>>()
                .join(", ")
        )];
        if let Some(path) = mod_save_setting.filter(|p| !p.is_empty())
            && !inside_install(path, &self.installs)
        {
            lines.push(format!("WARNING: MODSAVE_PATH ({path}) is not inside any install, so balance writes are not mirrored out. Point it inside one of the installs above"));
        }
        for root in &self.installs {
            let dir = PathBuf::from(mod_save_path(root));
            if dir.is_dir() && !writable(&dir) {
                lines.push(format!("WARNING: ModSave for {} ({}) is not writable - sync into it will fail. Check ownership and permissions", folder_name(root), dir.display()));
            }
        }
        lines
    }
}

fn writable(directory: &Path) -> bool {
    let probe = directory.join(format!(".modsync-probe.{}", std::process::id()));
    let ok = fs::write(&probe, "").is_ok();
    let _ = fs::remove_file(&probe);
    ok
}
