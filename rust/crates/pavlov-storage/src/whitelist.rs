//! A game's `whitelist.txt` (and `mods.txt`, the same format): one name per line, edited in
//! place without losing what an operator wrote (C# `Storage/WhitelistFile.cs`).
//!
//! THE FILE IS HAND-MAINTAINED. Comments say who somebody is and blank lines separate groups;
//! an edit adds or drops one line and keeps every other byte. Writes are atomic, gated per file,
//! refused when they would gut the list (an empty whitelist locks every player out), and never
//! create a directory.

use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, PoisonError};

use pavlov_core::data::RosterWriteGuard;
use pavlov_core::text::case::{eq_ignore_case, fold_case};

use crate::atomic_file;
use crate::game_files::GameFileGuard;

/// The longest entry written.
const MAX_ENTRY: usize = 64;

/// How an edit went.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WhitelistResult {
    pub path: String,
    pub ok: bool,
    /// False for "already in the state asked for": nothing was written.
    pub changed: bool,
    pub error: Option<String>,
}

impl WhitelistResult {
    fn ok(path: &str, changed: bool) -> Self {
        Self {
            path: path.to_owned(),
            ok: true,
            changed,
            error: None,
        }
    }

    fn failed(path: &str, error: impl Into<String>) -> Self {
        Self {
            path: path.to_owned(),
            ok: false,
            changed: false,
            error: Some(error.into()),
        }
    }

    /// The install the file belongs to: `.../pavlovserver2/Pavlov/Saved/Config/whitelist.txt`
    /// is `pavlovserver2`.
    #[must_use]
    pub fn install(&self) -> String {
        install_of(&self.path)
    }
}

fn install_of(path: &str) -> String {
    Path::new(path)
        .ancestors()
        .nth(4)
        .and_then(Path::file_name)
        .map(|n| n.to_string_lossy().into_owned())
        .filter(|n| !n.is_empty())
        .unwrap_or_else(|| path.to_owned())
}

/// A name as it is written: line breaks become spaces, control characters go (they would be
/// invisible in the file and the reply), trimmed, at most 64 characters. VERBATIM otherwise -
/// the file is matched against the player's actual in-game name.
#[must_use]
pub fn entry(raw: Option<&str>) -> String {
    let text: String = raw
        .unwrap_or_default()
        .chars()
        .map(|c| if c == '\r' || c == '\n' { ' ' } else { c })
        .filter(|c| !c.is_control())
        .collect();
    let text = text.trim();
    if text.chars().count() > MAX_ENTRY {
        text.chars()
            .take(MAX_ENTRY)
            .collect::<String>()
            .trim()
            .to_owned()
    } else {
        text.to_owned()
    }
}

/// The entries in these lines: trimmed, without blanks, `#` comments or `//` comments.
#[must_use]
pub fn entries<S: AsRef<str>>(lines: &[S]) -> Vec<String> {
    lines
        .iter()
        .map(|l| l.as_ref().trim())
        .filter(|l| !l.is_empty() && !l.starts_with('#') && !l.starts_with("//"))
        .map(str::to_owned)
        .collect()
}

fn sanitise(name: &str) -> String {
    let cleaned: String = name.chars().filter(|c| *c != '/' && *c != '\0').collect();
    let cleaned = cleaned.trim();
    if cleaned.is_empty() {
        "install".to_owned()
    } else {
        cleaned.to_owned()
    }
}

/// Reads and edits whitelist-format files.
#[derive(Debug)]
pub struct WhitelistFile {
    guard: Arc<GameFileGuard>,
    /// Where the copy taken before each edit goes (C#: `whitelist_bak` in the working directory).
    backup_directory: PathBuf,
    gates: Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
}

impl WhitelistFile {
    #[must_use]
    pub fn new(guard: Arc<GameFileGuard>, backup_directory: impl Into<PathBuf>) -> Self {
        Self {
            guard,
            backup_directory: backup_directory.into(),
            gates: Mutex::new(HashMap::new()),
        }
    }

    /// The entries in `path`; empty when it is not there or cannot be read (logged).
    #[must_use]
    pub fn read(&self, path: &str) -> Vec<String> {
        match fs::read_to_string(path) {
            Ok(text) => entries(&text.lines().collect::<Vec<_>>()),
            Err(e) if e.kind() == io::ErrorKind::NotFound => Vec::new(),
            Err(error) => {
                tracing::warn!(path, %error, "could not read a whitelist");
                Vec::new()
            }
        }
    }

    /// Add `entry`, unless it is already there.
    pub async fn add(&self, path: &str, entry: &str) -> WhitelistResult {
        self.edit(path, entry, false).await
    }

    /// Remove every line that is `entry`, unless none is.
    pub async fn remove(&self, path: &str, entry: &str) -> WhitelistResult {
        self.edit(path, entry, true).await
    }

    fn gate(&self, path: &str) -> Arc<tokio::sync::Mutex<()>> {
        Arc::clone(
            self.gates
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .entry(fold_case(path))
                .or_default(),
        )
    }

    async fn edit(&self, path: &str, entry: &str, removing: bool) -> WhitelistResult {
        if entry.trim().is_empty() {
            return WhitelistResult::failed(path, "there was no usable name to write");
        }
        if let Some(problem) = self.guard.problem(Some(path)) {
            tracing::warn!(path, problem, "not writing a whitelist");
            return WhitelistResult::failed(path, problem);
        }

        let gate = self.gate(path);
        let _held = gate.lock().await;

        let original: Vec<String> = match fs::read_to_string(path) {
            Ok(text) => text.lines().map(str::to_owned).collect(),
            Err(e) if e.kind() == io::ErrorKind::NotFound => Vec::new(),
            Err(error) => {
                tracing::error!(path, %error, "whitelist read failed");
                return WhitelistResult::failed(path, error.to_string());
            }
        };
        let before = entries(&original);

        let present = before.iter().any(|e| eq_ignore_case(e, entry));
        if present != removing {
            // Already in the state asked for. Rewriting a file the game reads live to change
            // nothing is all risk and no benefit.
            return WhitelistResult::ok(path, false);
        }

        let next: Vec<String> = if removing {
            original
                .iter()
                .filter(|l| !eq_ignore_case(l.trim(), entry))
                .cloned()
                .collect()
        } else {
            original.iter().cloned().chain([entry.to_owned()]).collect()
        };

        let after = entries(&next);
        let verdict = RosterWriteGuard::evaluate(Some(&before), Some(&after), true, false);
        if !verdict.is_allowed() {
            let why = verdict
                .explanation
                .unwrap_or_else(|| "the write was refused".into());
            tracing::error!(path, reason = %why, "REFUSED a whitelist write; the file is untouched");
            return WhitelistResult::failed(path, why);
        }

        self.backup(path, &original);

        // ATOMIC: a truncated whitelist locks out everybody below the cut.
        let text = format!("{}\n", next.join("\n"));
        if let Err(error) = atomic_file::write_text_async(PathBuf::from(path), text).await {
            tracing::error!(path, %error, "whitelist write failed");
            return WhitelistResult::failed(path, error.to_string());
        }
        tracing::info!(
            path,
            action = if removing { "remove" } else { "add" },
            entry,
            "whitelist edited"
        );
        WhitelistResult::ok(path, true)
    }

    /// A copy named for the install AND the file, so three servers do not overwrite each other's.
    fn backup(&self, path: &str, contents: &[String]) {
        let file = Path::new(path)
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default();
        let target = self.backup_directory.join(format!(
            "{}-{}.bak",
            sanitise(&install_of(path)),
            sanitise(&file)
        ));
        let result = fs::create_dir_all(&self.backup_directory)
            .and_then(|()| fs::write(&target, format!("{}\n", contents.join("\n"))));
        if let Err(error) = result {
            tracing::warn!(path, %error, "could not write the whitelist backup");
        }
    }
}
