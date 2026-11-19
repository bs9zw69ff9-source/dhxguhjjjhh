//! Every dataset as a row in one SQLite file, with a short read cache (C#
//! `Storage/SqliteKeyValueBackend.cs`).
//!
//! SQLite rather than JSON files because a file is not atomic across a crash and the bot writes
//! constantly; WAL gives durability the file layout never had, and one file is one backup. A
//! key-value table rather than real tables because the datasets are documents whose shapes
//! change as features land, and every query is "the whole dataset".
//!
//! THE CACHE EXPIRES, because this process is not the only writer: an older build sharing the
//! same `bot.db` (the rollback target) writes it too, and an entry held for the life of the
//! process made those writes invisible until the next local write overwrote them. Writes from
//! this process update the entry at once, so the TTL only bounds how long a FOREIGN write stays
//! unseen.

use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use pavlov_core::data::KeyValueBackend;
use rusqlite::{Connection, OptionalExtension, params};

use crate::atomic_file;

const CACHE_TTL: Duration = Duration::from_secs(5);

struct Inner {
    connection: Connection,
    cache: HashMap<String, (String, Instant)>,
}

/// The SQLite backend.
pub struct SqliteKeyValueBackend {
    path: PathBuf,
    inner: Mutex<Inner>,
}

impl std::fmt::Debug for SqliteKeyValueBackend {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SqliteKeyValueBackend")
            .field("path", &self.path)
            .finish_non_exhaustive()
    }
}

fn sql_error(error: rusqlite::Error) -> io::Error {
    io::Error::other(error)
}

fn check_key(key: &str) -> io::Result<()> {
    if key.trim().is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "a blank dataset name",
        ));
    }
    Ok(())
}

impl SqliteKeyValueBackend {
    /// Open (or create) the database, in WAL mode, with the `kv` table.
    ///
    /// # Errors
    /// When the directory or the database cannot be created or configured.
    pub fn open(database_path: impl Into<PathBuf>) -> io::Result<Self> {
        let path = database_path.into();
        if let Some(directory) = std::path::absolute(&path)?.parent() {
            fs::create_dir_all(directory)?;
        }
        let connection = Connection::open(&path).map_err(sql_error)?;
        // Survive a crash mid-write; wait for a lock rather than failing; FULL is redundant
        // under WAL for this workload.
        connection
            .pragma_update(None, "journal_mode", "WAL")
            .map_err(sql_error)?;
        connection
            .busy_timeout(Duration::from_millis(5000))
            .map_err(sql_error)?;
        connection
            .pragma_update(None, "synchronous", "NORMAL")
            .map_err(sql_error)?;
        connection
            .execute(
                "CREATE TABLE IF NOT EXISTS kv (file TEXT PRIMARY KEY, data TEXT NOT NULL)",
                [],
            )
            .map_err(sql_error)?;
        Ok(Self {
            path,
            inner: Mutex::new(Inner {
                connection,
                cache: HashMap::new(),
            }),
        })
    }

    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn read_locked(inner: &mut Inner, key: &str) -> io::Result<Option<String>> {
        if let Some((data, at)) = inner.cache.get(key)
            && at.elapsed() <= CACHE_TTL
        {
            return Ok(Some(data.clone()));
        }
        let data: Option<String> = inner
            .connection
            .query_row("SELECT data FROM kv WHERE file = ?1", [key], |row| {
                row.get(0)
            })
            .optional()
            .map_err(sql_error)?;
        match &data {
            Some(d) => {
                inner
                    .cache
                    .insert(key.to_owned(), (d.clone(), Instant::now()));
            }
            // A row that has been DELETED must not keep serving its old value.
            None => {
                inner.cache.remove(key);
            }
        }
        Ok(data)
    }

    fn write_locked(inner: &mut Inner, key: &str, json: &str) -> io::Result<()> {
        inner
            .connection
            .execute(
                "INSERT INTO kv(file, data) VALUES(?1, ?2) \
                 ON CONFLICT(file) DO UPDATE SET data = excluded.data",
                params![key, json],
            )
            .map_err(sql_error)?;
        // Cached only AFTER the write lands, so it never serves a value that was not persisted.
        inner
            .cache
            .insert(key.to_owned(), (json.to_owned(), Instant::now()));
        Ok(())
    }

    /// Whether a dataset exists at all - distinct from it being empty.
    ///
    /// # Errors
    /// When the database cannot be read.
    pub fn has(&self, key: &str) -> io::Result<bool> {
        Ok(self.read(key)?.is_some())
    }

    /// Every dataset name, sorted.
    ///
    /// # Errors
    /// When the database cannot be read.
    pub fn keys(&self) -> io::Result<Vec<String>> {
        let inner = self.lock();
        let mut statement = inner
            .connection
            .prepare("SELECT file FROM kv ORDER BY file")
            .map_err(sql_error)?;
        let rows = statement
            .query_map([], |row| row.get::<_, String>(0))
            .map_err(sql_error)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(sql_error)
    }

    /// Seed a dataset only if it is absent, so a fresh install has valid empty datasets. A seed
    /// that overwrote live data would wipe every ban on restart.
    ///
    /// # Errors
    /// When the database cannot be read or written.
    pub fn seed_if_missing(&self, key: &str, json: &str) -> io::Result<bool> {
        check_key(key)?;
        let mut inner = self.lock();
        if Self::read_locked(&mut inner, key)?.is_some() {
            return Ok(false);
        }
        Self::write_locked(&mut inner, key, json)?;
        Ok(true)
    }

    /// Import a legacy JSON file, once. The file is PARSED first: copying it in unvalidated
    /// would make a corrupt file authoritative. `Ok(false)` when there is nothing to import,
    /// the dataset already exists, or the file does not parse (logged).
    ///
    /// # Errors
    /// When the database cannot be read or written.
    pub fn import_json_file(&self, key: &str, file: &Path) -> io::Result<bool> {
        if self.has(key)? || !file.exists() {
            return Ok(false);
        }
        let raw = match fs::read_to_string(file) {
            Ok(raw) => raw,
            Err(error) => {
                tracing::warn!(file = %file.display(), %error, "could not migrate a legacy dataset file");
                return Ok(false);
            }
        };
        if let Err(error) = serde_json::from_str::<serde::de::IgnoredAny>(&raw) {
            tracing::warn!(file = %file.display(), %error, "a legacy dataset file does not parse; not imported");
            return Ok(false);
        }
        self.write(key, &raw)?;
        Ok(true)
    }

    /// Write every dataset out as pretty JSON, atomically, one file each - a current,
    /// human-readable backup. One failing dataset does not abort the rest.
    ///
    /// A DAMAGED DATASET NEVER REPLACES ITS BACKUP: one in `unreadable`, or one that no longer
    /// parses, goes to `<key>.json.corrupt` so the last good export survives beside it.
    ///
    /// # Errors
    /// When the directory cannot be created or the dataset list cannot be read.
    pub fn export_to_json(&self, directory: &Path, unreadable: &[String]) -> io::Result<usize> {
        fs::create_dir_all(directory)?;
        let mut exported = 0;
        for key in self.keys()? {
            let Some(data) = self.read(&key)? else {
                continue;
            };
            let (pretty, parses) = match serde_json::from_str::<serde_json::Value>(&data) {
                Ok(value) => (
                    serde_json::to_string_pretty(&value).unwrap_or_else(|_| data.clone()),
                    true,
                ),
                Err(_) => (data.clone(), false),
            };
            let mut path = directory.join(format!("{key}.json"));
            if !parses || unreadable.contains(&key) {
                path = directory.join(format!("{key}.json.corrupt"));
                tracing::warn!(%key, path = %path.display(), "dataset is damaged - exported beside the last good copy");
            }
            match atomic_file::write_text(&path, &pretty) {
                Ok(()) => exported += 1,
                Err(error) => tracing::warn!(%key, %error, "JSON export failed"),
            }
        }
        Ok(exported)
    }
}

impl KeyValueBackend for SqliteKeyValueBackend {
    fn read(&self, key: &str) -> io::Result<Option<String>> {
        check_key(key)?;
        Self::read_locked(&mut self.lock(), key)
    }

    fn write(&self, key: &str, json: &str) -> io::Result<()> {
        check_key(key)?;
        Self::write_locked(&mut self.lock(), key, json)
    }
}
