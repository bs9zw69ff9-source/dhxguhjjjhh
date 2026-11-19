//! The game's own ban list, `blacklist.txt` (C# `Moderation/ServerBanFile.cs`), READ ONLY.
//!
//! The bot no longer writes this file or imports from it (owner, 2026-11-19): bans live in the
//! player's save (see `bans.rs`), and entries already in the file are left alone, enforced by
//! the game on its own. It is read for one thing: `/checkban` and `/unban` can say a player is
//! still listed there, which is why a lifted ban can still keep them out.

use std::path::Path;

use pavlov_core::moderation::BanRules;

use super::ResolveName;

/// One player's block.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BanFileEntry {
    pub name: String,
    pub reason: String,
    pub unban: String,
}

/// What a lookup could say.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BanFileStatus {
    /// No path configured.
    Disabled,
    /// No file exists at any path.
    Missing,
    /// A file exists and could not be read.
    Unreadable,
    /// At least one file was read.
    Read,
}

/// A lookup's answer. "Not listed" is only trustworthy when [`conclusive`](Self::conclusive).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BanFileLookup {
    pub status: BanFileStatus,
    pub path: Option<String>,
    pub entry: Option<BanFileEntry>,
}

impl BanFileLookup {
    #[must_use]
    pub fn listed(&self) -> bool {
        self.entry.is_some()
    }

    #[must_use]
    pub fn conclusive(&self) -> bool {
        self.status == BanFileStatus::Read
    }
}

/// The reason an entry gets when the file gave none.
pub const DEFAULT_REASON: &str = "Imported from the server's ban list";

fn is_metadata(line: &str) -> bool {
    let lower = line.to_lowercase();
    lower.starts_with("reason:") || lower.starts_with("unban:") || lower.starts_with("appeal:")
}

/// Both shapes: a plain one-name-per-line list, and Reason/Unban blocks separated by blank
/// lines. A plain list has no blank lines, so the WHOLE FILE arrives as one block; a block is
/// metadata-shaped only if a line after its first says Reason, Unban or Appeal.
#[must_use]
pub fn parse(contents: Option<&str>) -> Vec<BanFileEntry> {
    let normalised = contents.unwrap_or_default().replace("\r\n", "\n");
    let mut entries = Vec::new();
    for block in normalised.split("\n\n").filter(|b| !b.is_empty()) {
        let lines: Vec<&str> = block
            .split('\n')
            .map(str::trim)
            .filter(|l| !l.is_empty() && !l.starts_with('#') && !l.starts_with("//"))
            .collect();
        let Some((first, rest)) = lines.split_first() else {
            continue;
        };
        if !rest.iter().any(|l| is_metadata(l)) {
            entries.extend(
                lines
                    .iter()
                    .filter(|l| !is_metadata(l))
                    .map(|l| BanFileEntry {
                        name: (*l).to_owned(),
                        reason: DEFAULT_REASON.into(),
                        unban: "Permanent".into(),
                    }),
            );
            continue;
        }
        if is_metadata(first) {
            continue;
        }
        let mut entry = BanFileEntry {
            name: (*first).to_owned(),
            reason: DEFAULT_REASON.into(),
            unban: "Permanent".into(),
        };
        for line in rest {
            let Some((key, value)) = line.split_once(':') else {
                continue;
            };
            let value = value.trim();
            if value.is_empty() {
                continue;
            }
            match key.trim().to_lowercase().as_str() {
                "reason" => value.clone_into(&mut entry.reason),
                "unban" => value.clone_into(&mut entry.unban),
                _ => {}
            }
        }
        entries.push(entry);
    }
    entries
}

/// The resolved name for a file entry (an EOS id from an in-game ban), or the entry itself.
#[must_use]
pub fn resolve_name(entry_name: &str, resolve: Option<&ResolveName>) -> String {
    resolve
        .and_then(|r| r(entry_name))
        .filter(|n| !n.trim().is_empty())
        .unwrap_or_else(|| entry_name.to_owned())
}

/// Every server's `blacklist.txt`, read only.
pub struct ServerBanFile {
    paths: Vec<String>,
    resolve: Option<ResolveName>,
}

impl std::fmt::Debug for ServerBanFile {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServerBanFile")
            .field("paths", &self.paths)
            .finish_non_exhaustive()
    }
}

impl ServerBanFile {
    #[must_use]
    pub fn new(paths: Vec<String>, resolve: Option<ResolveName>) -> Self {
        Self {
            paths: paths.into_iter().filter(|p| !p.trim().is_empty()).collect(),
            resolve,
        }
    }

    #[must_use]
    pub fn enabled(&self) -> bool {
        !self.paths.is_empty()
    }

    #[must_use]
    pub fn paths(&self) -> &[String] {
        &self.paths
    }

    /// The player in ANY server's file: listed anywhere is refused there.
    pub async fn find(&self, player: &str) -> BanFileLookup {
        if !self.enabled() {
            return BanFileLookup {
                status: BanFileStatus::Disabled,
                path: None,
                entry: None,
            };
        }
        let mut best = BanFileStatus::Missing;
        let mut best_path = self.paths.first().cloned();
        for path in &self.paths {
            if !Path::new(path).exists() {
                continue;
            }
            let text = match tokio::fs::read_to_string(path).await {
                Ok(text) => text,
                Err(error) => {
                    tracing::warn!(path, %error, "could not read the server ban file");
                    if best != BanFileStatus::Read {
                        best = BanFileStatus::Unreadable;
                        best_path = Some(path.clone());
                    }
                    continue;
                }
            };
            let found = parse(Some(&text)).into_iter().find(|e| {
                BanRules::same_player(Some(&e.name), Some(player))
                    || BanRules::same_player(
                        Some(&resolve_name(&e.name, self.resolve.as_ref())),
                        Some(player),
                    )
            });
            if let Some(entry) = found {
                return BanFileLookup {
                    status: BanFileStatus::Read,
                    path: Some(path.clone()),
                    entry: Some(entry),
                };
            }
            best = BanFileStatus::Read;
            best_path = Some(path.clone());
        }
        BanFileLookup {
            status: best,
            path: best_path,
            entry: None,
        }
    }
}
