//! One snapshot of every roster file, saved and restored from `/configure` (C#
//! `Factions/WhitelistBackup.cs`). Stored as the Node bot stores it: `{savedAt, files}`.
//!
//! - A PARTIAL READ IS NOT SAVED: a snapshot missing a file would restore it as absent, which
//!   is how a backup deletes a faction.
//! - Empty rosters never replace a populated snapshot: that is the wipe the snapshot is for.
//! - A file name from the snapshot that is not a plain `.txt` name (a path, a traversal) is
//!   refused rather than written wherever it points.

use std::sync::Arc;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::data::{SerializedStore, StoreError};
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::Clock;
use pavlov_storage::datasets::FACTION_BACKUP;
use serde::{Deserialize, Serialize};

use super::roster::RosterService;

/// How old the snapshot may get before the daily auto-save replaces it.
pub const AUTO_SAVE_AGE: TimeDelta = TimeDelta::hours(23);

/// The stored snapshot.
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize, Deserialize)]
pub struct FactionSnapshot {
    #[serde(rename = "savedAt", default)]
    pub saved_at: i64,
    #[serde(default)]
    pub files: Option<IgnoreCaseMap<Vec<String>>>,
}

impl FactionSnapshot {
    #[must_use]
    pub fn names(&self) -> usize {
        self.files
            .as_ref()
            .map_or(0, |f| f.values().map(Vec::len).sum())
    }

    #[must_use]
    pub fn at(&self) -> DateTime<Utc> {
        DateTime::from_timestamp_millis(self.saved_at).unwrap_or(DateTime::UNIX_EPOCH)
    }
}

/// What a save or load did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WhitelistBackupResult {
    pub ok: bool,
    pub message: String,
}

impl WhitelistBackupResult {
    fn ok(message: String) -> Self {
        Self { ok: true, message }
    }
    fn refused(message: impl Into<String>) -> Self {
        Self {
            ok: false,
            message: message.into(),
        }
    }
}

/// A plain `.txt` file name: no directory part, not `.` or `..`.
#[must_use]
pub fn is_roster_file_name(file: &str) -> bool {
    !file.trim().is_empty()
        && !file.contains('/')
        && !file.contains('\\')
        && file.to_ascii_lowercase().ends_with(".txt")
        && file != "."
        && file != ".."
}

/// The backup.
pub struct WhitelistBackup {
    store: Arc<SerializedStore>,
    rosters: Arc<RosterService>,
    clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for WhitelistBackup {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WhitelistBackup").finish_non_exhaustive()
    }
}

const NO_FOLDER: &str = "The roster folder (`FACTION_ROLES_PATH`) is not set or does not exist";

impl WhitelistBackup {
    #[must_use]
    pub fn new(
        store: Arc<SerializedStore>,
        rosters: Arc<RosterService>,
        clock: Arc<dyn Clock>,
    ) -> Self {
        Self {
            store,
            rosters,
            clock,
        }
    }

    /// The snapshot, when there is a usable one.
    #[must_use]
    pub fn current(&self) -> Option<FactionSnapshot> {
        let snapshot: Option<FactionSnapshot> =
            self.store.read(FACTION_BACKUP, None).ok().flatten();
        snapshot.filter(|s| s.saved_at > 0 && s.files.as_ref().is_some_and(|f| !f.is_empty()))
    }

    /// Save every roster file.
    ///
    /// # Errors
    /// When the snapshot cannot be written.
    pub async fn save(&self) -> Result<WhitelistBackupResult, StoreError> {
        if !self.rosters.enabled() {
            return Ok(WhitelistBackupResult::refused(format!(
                "{NO_FOLDER}, so there is nothing to save."
            )));
        }
        let mut files: IgnoreCaseMap<Vec<String>> = IgnoreCaseMap::new();
        let mut unreadable = Vec::new();
        for file in self.rosters.list_roster_files() {
            match self.rosters.read(&file) {
                Some(lines) => {
                    files.insert(file, lines);
                }
                None => unreadable.push(file),
            }
        }
        if !unreadable.is_empty() {
            return Ok(WhitelistBackupResult::refused(format!(
                "Not saved: {} roster file(s) could not be read ({}). The existing snapshot is untouched.",
                unreadable.len(),
                unreadable.join(", ")
            )));
        }
        let count = files.len();
        let snapshot = FactionSnapshot {
            saved_at: self.clock.now().timestamp_millis(),
            files: Some(files),
        };
        if snapshot.names() == 0 {
            if let Some(existing) = self.current().filter(|e| e.names() > 0) {
                return Ok(WhitelistBackupResult::refused(format!(
                    "Not saved: every roster is empty, and saving would replace the snapshot from {} UTC holding **{}** name(s). Load it first if the rosters were wiped.",
                    existing.at().format("%Y-%m-%d %H:%M"),
                    existing.names()
                )));
            }
        }
        self.store.write(FACTION_BACKUP, &snapshot).await?;
        tracing::info!(
            files = count,
            names = snapshot.names(),
            "whitelist snapshot saved"
        );
        Ok(WhitelistBackupResult::ok(format!(
            "Snapshot saved: **{count}** roster file(s), **{}** name(s). Loading it overwrites the current rosters.",
            snapshot.names()
        )))
    }

    /// Restore the snapshot over the current rosters.
    pub async fn load(&self) -> WhitelistBackupResult {
        let Some(snapshot) = self.current() else {
            return WhitelistBackupResult::refused(
                "There is no snapshot to restore. Save one first.",
            );
        };
        if !self.rosters.enabled() {
            return WhitelistBackupResult::refused(format!(
                "{NO_FOLDER}, so nothing can be restored into it."
            ));
        }
        let mut restored = 0;
        let mut failed = Vec::new();
        for (file, lines) in snapshot.files.iter().flat_map(IgnoreCaseMap::iter) {
            if !is_roster_file_name(file) {
                failed.push(format!("{file} (not a roster file name)"));
                continue;
            }
            if self.rosters.write(file, lines.clone(), true).await {
                restored += 1;
            } else {
                failed.push(file.to_owned());
            }
        }
        tracing::warn!(at = %snapshot.at(), restored, failed = failed.len(), "whitelist snapshot restored");
        let message = format!(
            "Restored the snapshot from {} UTC: **{restored}** roster file(s), **{}** name(s).",
            snapshot.at().format("%Y-%m-%d %H:%M"),
            snapshot.names()
        );
        if failed.is_empty() {
            WhitelistBackupResult::ok(message)
        } else {
            WhitelistBackupResult {
                ok: restored > 0,
                message: format!("{message}\nNot restored: {}", failed.join(", ")),
            }
        }
    }

    /// The daily save, when the snapshot is older than [`AUTO_SAVE_AGE`].
    pub async fn auto_save(&self) {
        if !self.rosters.enabled() {
            return;
        }
        if self
            .current()
            .is_some_and(|e| self.clock.now() - e.at() < AUTO_SAVE_AGE)
        {
            return;
        }
        match self.save().await {
            Ok(result) if result.ok => {
                tracing::info!(message = %result.message, "auto-saved whitelists")
            }
            Ok(result) => {
                tracing::warn!(message = %result.message, "auto whitelist snapshot skipped")
            }
            Err(error) => tracing::warn!(%error, "auto whitelist snapshot failed"),
        }
    }
}
