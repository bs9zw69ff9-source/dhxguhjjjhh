//! The timeline store: append-only, indexed, bounded by retention (C#
//! `Host/Events/SqliteEventStore.cs`).
//!
//! NOT A DATASET IN THE DOCUMENT STORE: that rewrites a whole JSON document per write, and
//! appending one join at ten thousand events would serialise ten thousand. Here it is an indexed
//! insert in an `events` table beside `kv` in the same `bot.db`.
//!
//! CREATE IF NOT EXISTS, and nothing else: an existing installation gains the table on first
//! start with no migration, and a rollback leaves a table nothing reads.

use std::fmt;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use chrono::{DateTime, Utc};
use pavlov_core::events::{EventCategory, EventQuery, ServerEvent};
use rusqlite::types::Value;
use rusqlite::{Connection, params_from_iter};

/// What [`EventStore::count_by`] groups on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EventGrouping {
    Kind,
    Category,
    Player,
    Server,
    Actor,
    /// The UTC calendar day, `YYYY-MM-DD`.
    Day,
    /// The UTC hour, `YYYY-MM-DD HH`.
    Hour,
}

impl EventGrouping {
    /// The SQL expression. A fixed table, never caller text.
    const fn column(self) -> &'static str {
        match self {
            Self::Kind => "kind",
            Self::Category => "category",
            Self::Player => "player",
            Self::Server => "server",
            Self::Actor => "actor",
            // `at` is milliseconds; SQLite's date functions want seconds.
            Self::Day => "strftime('%Y-%m-%d', at / 1000, 'unixepoch')",
            Self::Hour => "strftime('%Y-%m-%d %H', at / 1000, 'unixepoch')",
        }
    }
}

/// The timeline, as the services see it.
pub trait EventStore: Send + Sync {
    /// Append events, normalised, in one transaction.
    ///
    /// # Errors
    /// When the database cannot be written; nothing in the batch is stored then.
    fn append(&self, events: &[ServerEvent]) -> io::Result<()>;

    /// Matching events, newest first, ties in insertion order, at most the effective limit.
    ///
    /// # Errors
    /// When the database cannot be read.
    fn query(&self, query: &EventQuery) -> io::Result<Vec<ServerEvent>>;

    /// Every stored event.
    ///
    /// # Errors
    /// When the database cannot be read.
    fn count(&self) -> io::Result<u64>;

    /// Remove every event before `older_than`; how many went.
    ///
    /// # Errors
    /// When the database cannot be written.
    fn prune(&self, older_than: DateTime<Utc>) -> io::Result<usize>;

    /// Matching events counted per key, largest first. NULL keys are excluded: grouping by
    /// player over server restarts would otherwise report a nameless bucket as the top player.
    ///
    /// # Errors
    /// When the database cannot be read.
    fn count_by(
        &self,
        grouping: EventGrouping,
        query: &EventQuery,
    ) -> io::Result<Vec<(String, u64)>>;

    /// How many distinct non-null keys the matching events have.
    ///
    /// # Errors
    /// When the database cannot be read.
    fn count_distinct(&self, grouping: EventGrouping, query: &EventQuery) -> io::Result<u64>;
}

/// The store when the timeline is turned off: records nothing, finds nothing.
#[derive(Debug, Clone, Copy, Default)]
pub struct NullEventStore;

impl EventStore for NullEventStore {
    fn append(&self, _: &[ServerEvent]) -> io::Result<()> {
        Ok(())
    }
    fn query(&self, _: &EventQuery) -> io::Result<Vec<ServerEvent>> {
        Ok(Vec::new())
    }
    fn count(&self) -> io::Result<u64> {
        Ok(0)
    }
    fn prune(&self, _: DateTime<Utc>) -> io::Result<usize> {
        Ok(0)
    }
    fn count_by(&self, _: EventGrouping, _: &EventQuery) -> io::Result<Vec<(String, u64)>> {
        Ok(Vec::new())
    }
    fn count_distinct(&self, _: EventGrouping, _: &EventQuery) -> io::Result<u64> {
        Ok(0)
    }
}

/// The SQLite timeline.
pub struct SqliteEventStore {
    path: PathBuf,
    connection: Mutex<Connection>,
}

impl fmt::Debug for SqliteEventStore {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("SqliteEventStore")
            .field("path", &self.path)
            .finish_non_exhaustive()
    }
}

fn sql(error: rusqlite::Error) -> io::Error {
    io::Error::other(error)
}

fn non_negative(n: i64) -> u64 {
    u64::try_from(n).unwrap_or(0)
}

/// The WHERE clauses and their bound values. Every value is a PARAMETER: a filter originates in
/// a Discord option, and an in-game name is about as attacker-influenced as a string gets.
fn filters(query: &EventQuery) -> (Vec<&'static str>, Vec<Value>) {
    let mut clauses = Vec::new();
    let mut values = Vec::new();
    if let Some(since) = query.since {
        clauses.push("at >= ?");
        values.push(Value::Integer(since.timestamp_millis()));
    }
    if let Some(until) = query.until {
        clauses.push("at < ?");
        values.push(Value::Integer(until.timestamp_millis()));
    }
    if let Some(category) = query.category {
        clauses.push("category = ?");
        values.push(Value::Integer(i64::from(category.code())));
    }
    let text = |v: &Option<String>| {
        v.as_deref()
            .map(str::trim)
            .filter(|t| !t.is_empty())
            .map(str::to_owned)
    };
    if let Some(player) = text(&query.player) {
        clauses.push("player = ? COLLATE NOCASE");
        values.push(Value::Text(player));
    }
    if let Some(server) = text(&query.server) {
        clauses.push("server = ?");
        values.push(Value::Text(server));
    }
    if let Some(actor) = text(&query.actor) {
        clauses.push("actor = ? COLLATE NOCASE");
        values.push(Value::Text(actor));
    }
    (clauses, values)
}

fn where_clause(clauses: &[&str]) -> String {
    if clauses.is_empty() {
        String::new()
    } else {
        format!(" WHERE {}", clauses.join(" AND "))
    }
}

impl SqliteEventStore {
    /// Open (or create) the timeline in the database at `database_path`.
    ///
    /// # Errors
    /// When the directory or the database cannot be created or configured.
    pub fn open(database_path: impl Into<PathBuf>) -> io::Result<Self> {
        let path = database_path.into();
        if let Some(directory) = std::path::absolute(&path)?.parent() {
            fs::create_dir_all(directory)?;
        }
        let connection = Connection::open(&path).map_err(sql)?;
        connection
            .pragma_update(None, "journal_mode", "WAL")
            .map_err(sql)?;
        connection
            .busy_timeout(Duration::from_millis(5000))
            .map_err(sql)?;
        connection
            .pragma_update(None, "synchronous", "NORMAL")
            .map_err(sql)?;
        /* AT IS LAST IN EVERY COMPOSITE: the filter column is the equality test and time the
        range. COLLATE NOCASE on player and actor, because a NOCASE comparison does not use an
        index built with the default collation - silently. */
        connection
            .execute_batch(
                "CREATE TABLE IF NOT EXISTS events (
                     id       INTEGER PRIMARY KEY AUTOINCREMENT,
                     at       INTEGER NOT NULL,
                     category INTEGER NOT NULL,
                     kind     TEXT    NOT NULL,
                     player   TEXT,
                     server   TEXT,
                     actor    TEXT,
                     detail   TEXT
                 );
                 CREATE INDEX IF NOT EXISTS ix_events_at ON events(at DESC);
                 CREATE INDEX IF NOT EXISTS ix_events_player_at ON events(player COLLATE NOCASE, at DESC);
                 CREATE INDEX IF NOT EXISTS ix_events_server_at ON events(server, at DESC);
                 CREATE INDEX IF NOT EXISTS ix_events_category_at ON events(category, at DESC);
                 CREATE INDEX IF NOT EXISTS ix_events_actor_at ON events(actor COLLATE NOCASE, at DESC);",
            )
            .map_err(sql)?;
        Ok(Self {
            path,
            connection: Mutex::new(connection),
        })
    }

    /// The database file.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    fn lock(&self) -> MutexGuard<'_, Connection> {
        self.connection
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
    }
}

impl EventStore for SqliteEventStore {
    fn append(&self, events: &[ServerEvent]) -> io::Result<()> {
        if events.is_empty() {
            return Ok(());
        }
        let mut connection = self.lock();
        let transaction = connection.transaction().map_err(sql)?;
        {
            // Prepared once and rebound per row: preparing it 5,000 times is most of a backfill.
            let mut insert = transaction
                .prepare_cached(
                    "INSERT INTO events (at, category, kind, player, server, actor, detail)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
                )
                .map_err(sql)?;
            for raw in events {
                let e = raw.normalised();
                insert
                    .execute(rusqlite::params![
                        e.at.timestamp_millis(),
                        e.category.code(),
                        e.kind,
                        e.player,
                        e.server,
                        e.actor,
                        e.detail
                    ])
                    .map_err(sql)?;
            }
        }
        transaction.commit().map_err(sql)
    }

    fn query(&self, query: &EventQuery) -> io::Result<Vec<ServerEvent>> {
        let (clauses, mut values) = filters(query);
        values.push(Value::Integer(i64::from(query.effective_limit())));
        let statement = format!(
            "SELECT at, category, kind, player, server, actor, detail FROM events{} \
             ORDER BY at DESC, id DESC LIMIT ?",
            where_clause(&clauses)
        );
        let connection = self.lock();
        let mut prepared = connection.prepare(&statement).map_err(sql)?;
        let rows = prepared
            .query_map(params_from_iter(values), |row| {
                let at: i64 = row.get(0)?;
                let code: i32 = row.get(1)?;
                Ok((
                    at,
                    code,
                    ServerEvent {
                        at: DateTime::UNIX_EPOCH,
                        category: EventCategory::Staff,
                        kind: row.get(2)?,
                        player: row.get(3)?,
                        server: row.get(4)?,
                        actor: row.get(5)?,
                        detail: row.get(6)?,
                    },
                ))
            })
            .map_err(sql)?;
        rows.map(|row| {
            let (at, code, mut event) = row.map_err(sql)?;
            event.at = DateTime::from_timestamp_millis(at).unwrap_or(DateTime::UNIX_EPOCH);
            // A category a newer build added reads as Staff, the fallback every action lands in.
            event.category = EventCategory::from_code(code).unwrap_or(EventCategory::Staff);
            Ok(event)
        })
        .collect()
    }

    fn count(&self) -> io::Result<u64> {
        let n: i64 = self
            .lock()
            .query_row("SELECT COUNT(*) FROM events", [], |row| row.get(0))
            .map_err(sql)?;
        Ok(non_negative(n))
    }

    fn prune(&self, older_than: DateTime<Utc>) -> io::Result<usize> {
        let removed = self
            .lock()
            .execute(
                "DELETE FROM events WHERE at < ?1",
                [older_than.timestamp_millis()],
            )
            .map_err(sql)?;
        if removed > 0 {
            tracing::info!(removed, cutoff = %older_than, "pruned timeline events");
        }
        Ok(removed)
    }

    fn count_by(
        &self,
        grouping: EventGrouping,
        query: &EventQuery,
    ) -> io::Result<Vec<(String, u64)>> {
        let column = grouping.column();
        let (mut clauses, mut values) = filters(query);
        let not_null = format!("{column} IS NOT NULL");
        clauses.push(&not_null);
        values.push(Value::Integer(i64::from(query.effective_limit())));
        let statement = format!(
            "SELECT {column} AS k, COUNT(*) FROM events{} GROUP BY k ORDER BY COUNT(*) DESC LIMIT ?",
            where_clause(&clauses)
        );
        let connection = self.lock();
        let mut prepared = connection.prepare(&statement).map_err(sql)?;
        let rows = prepared
            .query_map(params_from_iter(values), |row| {
                let key = match row.get::<_, Value>(0)? {
                    Value::Text(t) => t,
                    Value::Integer(i) => i.to_string(),
                    Value::Real(r) => r.to_string(),
                    Value::Null | Value::Blob(_) => String::new(),
                };
                Ok((key, non_negative(row.get(1)?)))
            })
            .map_err(sql)?;
        rows.collect::<Result<_, _>>().map_err(sql)
    }

    fn count_distinct(&self, grouping: EventGrouping, query: &EventQuery) -> io::Result<u64> {
        let column = grouping.column();
        let (mut clauses, values) = filters(query);
        let not_null = format!("{column} IS NOT NULL");
        clauses.push(&not_null);
        let statement = format!(
            "SELECT COUNT(DISTINCT {column}) FROM events{}",
            where_clause(&clauses)
        );
        let n: i64 = self
            .lock()
            .query_row(&statement, params_from_iter(values), |row| row.get(0))
            .map_err(sql)?;
        Ok(non_negative(n))
    }
}
