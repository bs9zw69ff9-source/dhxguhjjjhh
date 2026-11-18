//! The game's own ban list, `blacklist.txt` (C# `Moderation/ServerBanFile.cs`): the message a
//! banned player sees, and the only way a ban made from the in-game admin menu reaches the bot.
//!
//! IMPORT BEFORE EXPORT, FROM EVERY FILE: an in-game ban made on server 2 lives only in server
//! 2's file, and exporting the store to every file first would erase it before it was read.
//! A deliberately lifted name (a tombstone) is never re-imported, and an elapsed temp ban is
//! never turned permanent.

use std::path::Path;
use std::sync::Arc;

use chrono::TimeDelta;
use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::json::Stamp;
use pavlov_core::moderation::{BanRecord, BanRules};
use pavlov_core::text::case::{IgnoreCaseMap, IgnoreCaseSet};
use pavlov_core::time::{Clock, EasternTime};
use pavlov_storage::atomic_file;
use pavlov_storage::datasets::{TEMP_BANS, UNBAN_TOMBSTONES};
use pavlov_storage::game_files::GameFileGuard;

use super::ResolveName;
use super::bans::BanFileExport;

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

/// How long a lift blocks the importer.
pub const TOMBSTONE_LIFE: TimeDelta = TimeDelta::days(7);
/// The reason an imported ban gets when the file gave none.
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

/// The earliest tombstone under either identity.
#[must_use]
pub fn lifted_at(
    tombstones: &IgnoreCaseMap<Stamp>,
    id: &str,
    name: &str,
) -> Option<chrono::DateTime<chrono::Utc>> {
    [id, name]
        .into_iter()
        .filter_map(|key| tombstones.get(key).map(|s| s.0))
        .min()
}

/// "0m", "0", "00h", "expired": no time left, which is the answer and not unreadable.
#[must_use]
pub fn denotes_elapsed(unban: Option<&str>) -> bool {
    let text = unban.unwrap_or_default().trim();
    if text.is_empty() {
        return false;
    }
    if text.eq_ignore_ascii_case("expired") {
        return true;
    }
    let digits = text.trim_end_matches(['s', 'm', 'h', 'd', 'S', 'M', 'H', 'D', ' ']);
    !digits.is_empty() && digits.bytes().all(|b| b == b'0')
}

fn flatten(text: &str) -> String {
    text.split(['\r', '\n'])
        .filter(|l| !l.is_empty())
        .collect::<Vec<_>>()
        .join(" ")
        .trim()
        .to_owned()
}

/// Every server's `blacklist.txt`, kept identical.
pub struct ServerBanFile {
    paths: Vec<String>,
    store: Arc<SerializedStore>,
    clock: Arc<dyn Clock>,
    resolve: Option<ResolveName>,
    guard: Arc<GameFileGuard>,
    warned: std::sync::Mutex<std::collections::HashSet<String>>,
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
    pub fn new(
        paths: Vec<String>,
        store: Arc<SerializedStore>,
        clock: Arc<dyn Clock>,
        resolve: Option<ResolveName>,
        guard: Arc<GameFileGuard>,
    ) -> Self {
        Self {
            paths: paths.into_iter().filter(|p| !p.trim().is_empty()).collect(),
            store,
            clock,
            resolve,
            guard,
            warned: std::sync::Mutex::new(std::collections::HashSet::new()),
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

    /// The stored bans, refusing an UNREADABLE list rather than reading it as empty: exported
    /// as empty, it would erase every in-game ban from every server's file, and those live
    /// nowhere else.
    fn bans(&self) -> Result<Vec<BanRecord>, String> {
        let bans = self
            .store
            .read::<Vec<BanRecord>>(TEMP_BANS, Vec::new())
            .map_err(|e| e.to_string())?;
        if self.store.unreadable().iter().any(|k| k == TEMP_BANS) {
            return Err(
                "the stored ban list cannot be read - not rewriting the server ban files from it"
                    .into(),
            );
        }
        Ok(bans)
    }

    /// The store's active bans to every file. The count of bans written (once, not per file),
    /// or 0 when no file could be written.
    ///
    /// # Errors
    /// When the store cannot be read: nothing is written then, so a read failure can never
    /// empty every server's list.
    pub async fn export(&self) -> Result<usize, String> {
        if !self.enabled() {
            return Ok(0);
        }
        let now = self.clock.now();
        let all = self.bans()?;
        let mut active = BanRules::active(&all, now);
        active.sort_by(|a, b| pavlov_core::text::case::cmp_ignore_case(&a.player_id, &b.player_id));
        let mut body = String::new();
        for ban in &active {
            body.push_str(&ban.player_id);
            body.push('\n');
            body.push_str("Reason: ");
            body.push_str(&flatten(ban.reason.as_deref().unwrap_or("No reason given")));
            body.push('\n');
            body.push_str("Unban: ");
            match (ban.permanent, ban.expires) {
                (false, Some(expires)) => body.push_str(&EasternTime::time_left(expires, now)),
                _ => body.push_str("Permanent"),
            }
            body.push_str("\n\n");
        }
        let mut wrote = false;
        for path in &self.paths {
            /* NOT create_dir_all: the game owns this directory and already made it. A missing
            one means the path is wrong, and building it makes a second tree the game never
            reads. Warned once per file: this runs on a timer. */
            if let Some(problem) = self.guard.problem(Some(path)) {
                let first = self
                    .warned
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .insert(path.clone());
                if first {
                    tracing::error!(path, problem, "not writing the server ban file");
                }
                continue;
            }
            // Keeps the game's ownership of the file (see atomic_file).
            match atomic_file::write_text_async(path.into(), body.clone()).await {
                Ok(()) => wrote = true,
                Err(error) => tracing::warn!(path, %error, "could not write the server ban file"),
            }
        }
        Ok(if wrote { active.len() } else { 0 })
    }

    /// In-game bans the store does not know, from every file. The number added.
    ///
    /// # Errors
    /// When the store fails.
    pub async fn import(&self) -> Result<usize, String> {
        if !self.enabled() {
            return Ok(0);
        }
        let mut parsed = Vec::new();
        for path in &self.paths {
            if !Path::new(path).exists() {
                continue;
            }
            match tokio::fs::read_to_string(path).await {
                Ok(text) => parsed.extend(parse(Some(&text))),
                // One unreadable file must not stop the others.
                Err(error) => {
                    tracing::debug!(path, %error, "could not read the server ban file for import")
                }
            }
        }
        if parsed.is_empty() {
            return Ok(0);
        }
        let now = self.clock.now();
        let lifted: IgnoreCaseMap<Stamp> = self
            .store
            .read(UNBAN_TOMBSTONES, IgnoreCaseMap::default())
            .map_err(|e| e.to_string())?;
        let mut added = 0_usize;
        let resolve = self.resolve.as_ref();
        self.store
            .update(TEMP_BANS, Vec::new, |mut bans: Vec<BanRecord>| {
                let mut known: IgnoreCaseSet = bans.iter().map(|b| b.player_id.clone()).collect();
                for entry in &parsed {
                    /* RESOLVED FIRST: the id and the name are one identity, and comparing the
                    file's id against stored names re-created the same ban every sync. */
                    let player = resolve_name(&entry.name, resolve);
                    let resolved = player != entry.name;
                    if known.contains(&player) || known.contains(&entry.name) {
                        continue;
                    }
                    known.insert(player.clone());
                    known.insert(entry.name.clone());
                    // Either identity's tombstone counts: staff type the name they can see.
                    if let Some(when) = lifted_at(&lifted, &entry.name, &player) {
                        if now - when < TOMBSTONE_LIFE {
                            tracing::info!(player = %player, "not re-importing a ban that was deliberately lifted - use the ban commands to ban them again");
                            continue;
                        }
                    }
                    // NO TIME LEFT MEANS EXPIRED, NOT FOREVER.
                    if denotes_elapsed(Some(&entry.unban)) {
                        tracing::info!(name = %entry.name, unban = %entry.unban, "skipping an in-game ban whose time has already elapsed");
                        continue;
                    }
                    /* Unreadable becomes PERMANENT with the value recorded: a guessed length
                    frees somebody early or holds them too long, and the note is fixable. */
                    let span = EasternTime::parse_ban_span(Some(&entry.unban));
                    let permanent = span.is_none();
                    let mut reason = entry.reason.clone();
                    if permanent && !entry.unban.eq_ignore_ascii_case("Permanent") {
                        reason = format!("{reason} [unreadable unban value: {}]", entry.unban);
                    }
                    bans.push(BanRecord {
                        player_id: player,
                        // The id from the file, only when resolution actually happened.
                        unique_id: resolved.then(|| entry.name.clone()),
                        reason: Some(reason),
                        moderator: Some("in-game".into()),
                        at: Some(now),
                        expires: span.map(|s| now + s),
                        permanent,
                        duration_label: Some(if permanent {
                            "Permanent".into()
                        } else {
                            entry.unban.clone()
                        }),
                        ..BanRecord::default()
                    });
                    added += 1;
                }
                (added > 0).then_some(bans)
            })
            .await
            .map_err(|e| e.to_string())?;
        if added > 0 {
            tracing::info!(count = added, "imported bans from the in-game ban list");
        }
        Ok(added)
    }

    /// Import, THEN export: the order is load-bearing.
    ///
    /// # Errors
    /// When the store fails.
    pub async fn sync(&self) -> Result<(), String> {
        self.import().await?;
        self.export().await.map(|_| ())
    }
}

impl BanFileExport for ServerBanFile {
    fn export(&self) -> BoxFuture<'_, Result<usize, String>> {
        Box::pin(ServerBanFile::export(self))
    }
}
