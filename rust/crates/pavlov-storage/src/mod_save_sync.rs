//! Keeping every install's ModSave tree identical (C# `Storage/ModSaveSync.cs`).
//!
//! The planner (`pavlov_core::sync`) decides from modified times; this reads only the files a
//! candidate touches and applies the content guards:
//!
//! - THE ONLINE GUARD: a connected player's ledger is still moving in server memory, so the
//!   sweep skips it; the join-time [`ModSaveSync::sync_player_ledger`] handles those.
//! - THE MONEY GUARD: an empty-or-zero file never lands on a positive balance.
//! - THE ROSTER GUARD: an empty roster from anywhere but the folder the bot edits never wipes a
//!   filled one (a fresh server's empty file is not a `/whitelist wipe`).
//!
//! The RCON+ menu-access files are never mirrored: they are managed live, and newest-winning
//! them wipes a player's menu.

use std::collections::{BTreeMap, HashMap};
use std::fs::{self, File};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, UNIX_EPOCH};

use pavlov_core::servers::installs::mod_save_path;
use pavlov_core::sync::ledger_file::{looks_like_ledger, would_wipe_balance};
use pavlov_core::sync::{DEFAULT_TOLERANCE_MS, ModSaveSnapshot, candidates};
use pavlov_core::text::case::{contains_ignore_case, eq_ignore_case, starts_with_ignore_case};
use pavlov_rcon::OnlineRoster;

use crate::atomic_file;
use crate::game_files::{config_path, create_owned_like_parent, inside_install};

/// Never mirrored, whatever the configuration says.
const ALWAYS_SKIP: [&str; 4] = ["menuaccess", "accessmanager", "rconplus", "rcon_plus"];

/// The sync across a box's installs.
pub struct ModSaveSync {
    installs: Vec<String>,
    online: Option<Arc<dyn OnlineRoster>>,
    enabled: bool,
    skip: Vec<String>,
    /// The roster folder the bot edits, relative to ModSave with a trailing `/`, and its install.
    roster: Option<(String, String)>,
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
/// their modified time. A tree that cannot be listed contributes nothing.
fn list_tree(root: &Path) -> HashMap<String, i64> {
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
    files
}

fn read_text(path: &Path) -> Option<String> {
    fs::read(path)
        .ok()
        .map(|b| String::from_utf8_lossy(&b).into_owned())
}

fn file_stem(rel: &str) -> &str {
    let name = rel.rsplit('/').next().unwrap_or(rel);
    name.rsplit_once('.').map_or(name, |(stem, _)| stem)
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
        online: Option<Arc<dyn OnlineRoster>>,
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
        let skip = ALWAYS_SKIP
            .iter()
            .map(|s| (*s).to_owned())
            .chain(
                extra_skip
                    .iter()
                    .map(|s| s.trim().to_owned())
                    .filter(|s| !s.is_empty()),
            )
            .collect();
        Self {
            installs,
            online,
            enabled,
            skip,
            roster,
        }
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

    fn is_online(&self, player: &str) -> bool {
        self.online.as_ref().is_some_and(|roster| {
            roster.is_trustworthy() && roster.online().iter().any(|p| eq_ignore_case(p, player))
        })
    }

    /// One pass over every install: how many files were copied. Blocking file I/O.
    #[must_use]
    pub fn sweep(&self) -> usize {
        if !self.can_sync() {
            return 0;
        }
        let snapshots: Vec<ModSaveSnapshot> = self
            .installs
            .iter()
            .map(|root| ModSaveSnapshot {
                root: root.clone(),
                files: list_tree(Path::new(&mod_save_path(root))),
            })
            .collect();
        let skip = |rel: &str| self.skipped(rel);
        let planned = candidates(&snapshots, Some(&skip), DEFAULT_TOLERANCE_MS);

        // Grouped by source so its content is read, and the online guard applied, once.
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

            if looks_like_ledger(Some(&text)) && self.is_online(file_stem(&rel)) {
                continue;
            }

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
        copied
    }

    /// Push one player's newest ledger to every other install, at join time - the path that
    /// syncs ONLINE players, which the sweep leaves alone. Still never wipes a balance.
    pub fn sync_player_ledger(&self, player_name: &str) {
        if !self.can_sync() {
            return;
        }
        let file: String = player_name
            .chars()
            .filter(|c| !c.is_control() && *c != '/' && *c != '\\')
            .collect();
        let file = file.trim();
        if file.is_empty() {
            return;
        }
        let rel = format!("{file}.txt");

        let best = self
            .installs
            .iter()
            .filter_map(|root| {
                let path = Path::new(&mod_save_path(root)).join(&rel);
                modified_unix_ms(&path).map(|m| (root, m))
            })
            .fold(None, |best: Option<(&String, i64)>, (root, m)| match best {
                Some((_, b)) if b >= m => best,
                _ => Some((root, m)),
            });
        // No install has a ledger for them yet: the game makes it, not the bot.
        let Some((best_root, best_modified)) = best else {
            return;
        };
        let Ok(content) = fs::read(Path::new(&mod_save_path(best_root)).join(&rel)) else {
            return;
        };
        let text = String::from_utf8_lossy(&content).into_owned();

        let mut copied = 0;
        for root in &self.installs {
            if root == best_root {
                continue;
            }
            let dest = Path::new(&mod_save_path(root)).join(&rel);
            if modified_unix_ms(&dest)
                .is_some_and(|m| m >= best_modified.saturating_sub(DEFAULT_TOLERANCE_MS))
            {
                continue;
            }
            if would_wipe_balance(Some(&text), read_text(&dest).as_deref()) {
                continue;
            }
            if self.write_mirrored(root, &dest, &content, best_modified) {
                copied += 1;
            }
        }
        if copied > 0 {
            tracing::debug!(
                player = player_name,
                copied,
                "ModSave sync propagated a ledger"
            );
        }
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
        if let Some(path) = mod_save_setting.filter(|p| !p.is_empty()) {
            if !inside_install(path, &self.installs) {
                lines.push(format!("WARNING: MODSAVE_PATH ({path}) is not inside any install, so balance writes are not mirrored out. Point it inside one of the installs above"));
            }
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
