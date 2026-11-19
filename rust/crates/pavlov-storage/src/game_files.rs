//! The rules for writing into a game install (C# `Storage/GameFiles.cs`, `GameFileGuard.cs`,
//! `GameDirectories.cs`).
//!
//! - The bot NEVER CREATES A DIRECTORY inside a game install: a missing one means the
//!   configured path is wrong, and building it produces a tree the game never reads.
//! - `IGNORE_PATHS` names files and trees another bot or process owns; the bot reads them and
//!   never writes them.

use std::collections::HashSet;
use std::fs;
use std::io;
use std::path::{Component, Path, PathBuf};
use std::sync::{Mutex, PoisonError};

use crate::ownership;

/// The reply for a write the bot refuses because the game server owns the file.
#[must_use]
pub fn refusal(what: &str) -> String {
    format!(
        "{what} is a file the game server owns, and the bot does not write those. Edit it on the server instead."
    )
}

/// `path` made absolute and normalised lexically (`.` and `..` resolved without touching the
/// disk), like .NET's `Path.GetFullPath`.
fn full_path(path: &Path) -> Option<PathBuf> {
    let absolute = std::path::absolute(path).ok()?;
    let mut out = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::ParentDir => {
                out.pop();
            }
            Component::CurDir => {}
            other => out.push(other),
        }
    }
    Some(out)
}

/// Why `path` cannot be written, or `None` when its directory exists.
#[must_use]
pub fn problem(path: Option<&str>) -> Option<String> {
    let Some(path) = path.filter(|p| !p.trim().is_empty()) else {
        return Some("no path configured".into());
    };
    let Some(full) = full_path(Path::new(path)) else {
        return Some("not a usable path".into());
    };
    let Some(directory) = full.parent().filter(|d| !d.as_os_str().is_empty()) else {
        return Some("no directory in the path".into());
    };
    if directory.is_dir() {
        None
    } else {
        Some(format!(
            "{} does not exist - the bot never creates a directory inside a game install, because a missing one means the configured path is wrong",
            directory.display()
        ))
    }
}

/// Whether `path` is one of `installs`, or inside one. A sibling sharing a prefix
/// (`pavlovserver2` beside `pavlovserver`) is NOT inside it.
#[must_use]
pub fn inside_install(path: &str, installs: &[String]) -> bool {
    let Some(full) = full_path(Path::new(path)) else {
        return false;
    };
    installs
        .iter()
        .any(|install| full_path(Path::new(install)).is_some_and(|root| full.starts_with(&root)))
}

/// The `Pavlov/Saved/Config` directory of an install.
#[must_use]
pub fn config_path(install_root: &str) -> PathBuf {
    Path::new(install_root).join("Pavlov/Saved/Config")
}

/// Create `directory` and every missing parent, each owned like the nearest existing ancestor,
/// so a directory the bot makes inside an install still belongs to the game.
///
/// # Errors
/// When a level cannot be created.
pub fn create_owned_like_parent(directory: &Path) -> io::Result<()> {
    let mut missing = Vec::new();
    let mut existing = full_path(directory)
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "not a usable path"))?;
    while !existing.is_dir() {
        missing.push(existing.clone());
        if !existing.pop() {
            return Err(io::Error::new(
                io::ErrorKind::NotFound,
                format!("no existing parent for {}", directory.display()),
            ));
        }
    }
    let owner = ownership::get(&existing).ok();
    while let Some(level) = missing.pop() {
        fs::create_dir(&level).or_else(|e| {
            if e.kind() == io::ErrorKind::AlreadyExists {
                Ok(())
            } else {
                Err(e)
            }
        })?;
        if let Some((uid, gid)) = owner
            && let Err(error) = ownership::set(&level, Some(uid), Some(gid))
        {
            tracing::warn!(path = %level.display(), %error, "could not give a new directory to the game's owner");
        }
    }
    Ok(())
}

/// `IGNORE_PATHS`: files and trees this bot must not write.
#[derive(Debug, Default)]
pub struct GameFileGuard {
    ignored: Vec<String>,
    /// Paths already reported, so a refusal is logged once per path.
    reported: Mutex<HashSet<String>>,
}

impl GameFileGuard {
    /// A guard over `ignored`. Blank entries - what a trailing comma in `.env` produces - are
    /// dropped: an empty string turned into a full path is the CURRENT DIRECTORY, and would
    /// refuse every write the bot makes.
    #[must_use]
    pub fn new(ignored: &[String]) -> Self {
        Self {
            ignored: ignored
                .iter()
                .map(|p| p.trim())
                .filter(|p| !p.is_empty())
                .map(str::to_owned)
                .collect(),
            reported: Mutex::new(HashSet::new()),
        }
    }

    /// The guard that ignores nothing.
    #[must_use]
    pub fn none() -> Self {
        Self::default()
    }

    /// Whether anything is ignored.
    #[must_use]
    pub fn any(&self) -> bool {
        !self.ignored.is_empty()
    }

    #[must_use]
    pub fn paths(&self) -> &[String] {
        &self.ignored
    }

    /// Whether `path` is covered by `IGNORE_PATHS`.
    #[must_use]
    pub fn is_ignored(&self, path: Option<&str>) -> bool {
        path.is_some_and(|p| !p.is_empty() && self.any() && inside_install(p, &self.ignored))
    }

    /// Why `path` cannot be written, or `None` when it can. Composes the older rule: the
    /// directory must exist.
    #[must_use]
    pub fn problem(&self, path: Option<&str>) -> Option<String> {
        if let Some(p) = path.filter(|p| self.is_ignored(Some(p))) {
            // SAID ONCE PER PATH: the ban export runs every five minutes.
            let first = self
                .reported
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .insert(p.to_owned());
            if first {
                tracing::info!(
                    path = p,
                    "not writing - it is covered by IGNORE_PATHS; another bot or process owns this file. Reads are unaffected"
                );
            }
            return Some("this path is in IGNORE_PATHS - another bot or process owns it".into());
        }
        problem(path)
    }
}
