//! Player data kept aside when a server is deleted, and put back when its slot is provisioned
//! again (C# `Servers/PreservedPlayerData.cs`).
//!
//! ModSave holds the faction rosters and the caps ledgers; an `rm -rf` that took them with it
//! is how a delete-and-reprovision wiped every whitelist.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use chrono::{DateTime, Utc};

/// What is kept, inside `Pavlov/Saved/Config`.
pub const ITEMS: [&str; 4] = ["ModSave", "mods.txt", "whitelist.txt", "blacklist.txt"];

/// What moved, and what was left where it was (with why).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PreservedMove {
    pub moved: Vec<String>,
    pub skipped: Vec<String>,
}

/// `<parent>/.pavlov-preserved/<install folder>`.
#[must_use]
pub fn preserved_dir(install_dir: &str) -> PathBuf {
    let trimmed = Path::new(install_dir.trim_end_matches('/'));
    let parent = trimmed.parent().unwrap_or_else(|| Path::new("/"));
    parent
        .join(".pavlov-preserved")
        .join(trimmed.file_name().unwrap_or_default())
}

fn config_dir(install_dir: &str) -> PathBuf {
    Path::new(install_dir).join("Pavlov/Saved/Config")
}

fn non_empty_dir(path: &Path) -> bool {
    fs::read_dir(path).is_ok_and(|mut d| d.next().is_some())
}

fn has_content(path: &Path) -> bool {
    if path.is_dir() {
        non_empty_dir(path)
    } else {
        fs::metadata(path).is_ok_and(|m| m.is_file() && m.len() > 0)
    }
}

/// Move the player data out of an install before it is deleted. An earlier, non-empty
/// preserved copy is renamed aside with a timestamp rather than overwritten.
///
/// # Errors
/// When a move fails: the caller must then NOT delete the install.
pub fn preserve(install_dir: &str, now: DateTime<Utc>) -> io::Result<PreservedMove> {
    let config = config_dir(install_dir);
    let target = preserved_dir(install_dir);
    let present: Vec<&str> = ITEMS
        .into_iter()
        .filter(|item| config.join(item).exists())
        .collect();
    if present.is_empty() {
        return Ok(PreservedMove::default());
    }
    if target.is_dir() && non_empty_dir(&target) {
        let mut aside = target.as_os_str().to_owned();
        aside.push(format!("-{}", now.format("%Y%m%d-%H%M%S")));
        fs::rename(&target, PathBuf::from(aside))?;
    }
    fs::create_dir_all(&target)?;
    for item in &present {
        fs::rename(config.join(item), target.join(item))?;
    }
    Ok(PreservedMove {
        moved: present.into_iter().map(str::to_owned).collect(),
        skipped: Vec::new(),
    })
}

/// Put kept data back into a freshly provisioned install. Anything the new install already
/// has WITH CONTENT is left alone and reported; the kept folder is removed once empty.
///
/// # Errors
/// When a move fails.
pub fn restore(install_dir: &str) -> io::Result<PreservedMove> {
    let source = preserved_dir(install_dir);
    if !source.is_dir() {
        return Ok(PreservedMove::default());
    }
    let config = config_dir(install_dir);
    fs::create_dir_all(&config)?;
    let mut result = PreservedMove::default();
    for item in ITEMS {
        let from = source.join(item);
        if !from.exists() {
            continue;
        }
        let to = config.join(item);
        if has_content(&to) {
            result.skipped.push(format!(
                "{item} (the new install already has one with content)"
            ));
            continue;
        }
        if to.is_dir() {
            fs::remove_dir(&to)?;
        } else if to.exists() {
            fs::remove_file(&to)?;
        }
        fs::rename(&from, &to)?;
        result.moved.push(item.to_owned());
    }
    if !non_empty_dir(&source) {
        fs::remove_dir(&source)?;
    }
    Ok(result)
}
