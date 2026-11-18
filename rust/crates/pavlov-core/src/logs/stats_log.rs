//! Reading Pavlov's `Stats.log`, which records kills as structured JSON (C#
//! `Core/Logs/StatsLog.cs`).
//!
//! A BETTER SOURCE THAN `Pavlov.log` FOR THE SAME EVENT: each kill has its own timestamp (a
//! reassembled `Pavlov.log` record had only the poll's clock), a headshot flag, and it is
//! written whether or not `bVerboseLogging` is on.
//!
//! THE FORMAT is a header line, a tab-indented JSON object, and a closing brace in the first
//! column:
//!
//! ```text
//! [2026.09.11-20.01.15] StatManagerLog: {
//!     "KillData":
//!     {
//!         "Killer": "Holosight1",
//!         ...
//!     }
//! }
//! ```
//!
//! so the reader is a state machine over lines: the file is tailed incrementally and a block
//! routinely arrives split across two polls.
//!
//! THE BRACKET STAMP IS UTC. A `RoundState` block also carries its own local "Timestamp"
//! (four hours off in September: Eastern). The bracket is used because it does not move twice
//! a year.

use chrono::{DateTime, NaiveDateTime, Utc};
use serde_json::Value;

use crate::patterns::static_regex;
use crate::text::case::eq_ignore_case;

static_regex!(STAMP = r"^[0-9]{4}\.[0-9]{2}\.[0-9]{2}-[0-9]{2}\.[0-9]{2}\.[0-9]{2}$");

/// A block bigger than this many characters is garbage: a rotation mid-block leaves an
/// opening line whose close never arrives, and without a cap the buffer would hold a whole
/// log. Real blocks are a few hundred bytes.
const MAX_BLOCK_CHARS: usize = 8 * 1024;

const MARKER: &str = "] StatManagerLog: {";

/// One kill.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatsKill {
    pub killer: String,
    pub killed: String,
    /// `None` when the game recorded "None": a fall, drowning, the world.
    pub weapon: Option<String>,
    pub headshot: bool,
    /// From the block's own bracket stamp, not the clock when it was read.
    pub at: DateTime<Utc>,
}

impl StatsKill {
    /// Killer and killed are the same person.
    #[must_use]
    pub fn suicide(&self) -> bool {
        self.killer == self.killed
    }
}

/// A round changing state: "Starting", "Started", "Ended" - whatever the game writes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatsRoundState {
    pub state: String,
    pub at: DateTime<Utc>,
}

/// A completed record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StatsRecord {
    Kill(StatsKill),
    Round(StatsRoundState),
}

/// The line-by-line reader. One per tailed file.
#[derive(Debug, Default)]
pub struct StatsLogReader {
    block: String,
    block_chars: usize,
    block_at: Option<DateTime<Utc>>,
    open: bool,
    discarded: usize,
}

impl StatsLogReader {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Blocks abandoned because a new one started before they closed, or they grew too big.
    #[must_use]
    pub fn discarded(&self) -> usize {
        self.discarded
    }

    /// Feed one line in; the record it completed, if any.
    pub fn read(&mut self, line: &str) -> Option<StatsRecord> {
        if let Some(at) = header(line) {
            /* A new header while a block is open means the previous one never closed - a
            rotation or a truncated write. Half a JSON object cannot be parsed, and carrying
            it forward would corrupt the next one too. */
            if self.open {
                self.discarded += 1;
            }
            self.block.clear();
            self.block.push('{');
            self.block_chars = 1;
            self.block_at = Some(at);
            self.open = true;
            return None;
        }

        if !self.open {
            return None;
        }

        if self.block_chars > MAX_BLOCK_CHARS {
            self.discarded += 1;
            self.open = false;
            self.block.clear();
            return None;
        }

        // The wrapper's closing brace sits in the first column; everything inside is indented.
        if line.starts_with('}') {
            self.block.push('}');
            self.open = false;
            let json = std::mem::take(&mut self.block);
            return parse(&json, self.block_at?);
        }

        self.block.push('\n');
        self.block.push_str(line);
        self.block_chars += 1 + line.chars().count();
        None
    }
}

/// The timestamp on a block-opening line, or `None` if this is not one.
fn header(line: &str) -> Option<DateTime<Utc>> {
    if line.len() < 2 || !line.starts_with('[') || !line.ends_with(MARKER) {
        return None;
    }
    let close = line.find(']')?;
    timestamp(&line[1..close])
}

/// "2026.09.11-20.01.15" as UTC, exactly that shape.
#[must_use]
pub fn timestamp(text: &str) -> Option<DateTime<Utc>> {
    if !STAMP.is_match(text) {
        return None;
    }
    NaiveDateTime::parse_from_str(text, "%Y.%m.%d-%H.%M.%S")
        .ok()
        .map(|t| t.and_utc())
}

/// One complete block. Unknown record types, and blocks that will not parse, are skipped
/// quietly: this runs inside log ingestion, and one malformed record must not cost the batch.
fn parse(json: &str, at: DateTime<Utc>) -> Option<StatsRecord> {
    let root: Value = serde_json::from_str(json).ok()?;
    let root = root.as_object()?;

    if let Some(kill) = root.get("KillData").and_then(Value::as_object) {
        // Both names are needed: a kill naming nobody cannot be reported as anything.
        let killer = text(kill, "Killer")?;
        let killed = text(kill, "Killed")?;
        let weapon = text(kill, "KilledBy").filter(|w| !eq_ignore_case(w, "None"));
        return Some(StatsRecord::Kill(StatsKill {
            killer,
            killed,
            weapon,
            headshot: kill.get("Headshot") == Some(&Value::Bool(true)),
            at,
        }));
    }

    let state = text(root.get("RoundState")?.as_object()?, "State")?;
    Some(StatsRecord::Round(StatsRoundState { state, at }))
}

fn text(object: &serde_json::Map<String, Value>, name: &str) -> Option<String> {
    object
        .get(name)
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .map(str::to_owned)
}
