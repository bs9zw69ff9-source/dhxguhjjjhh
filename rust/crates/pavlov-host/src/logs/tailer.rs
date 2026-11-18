//! Following a log as the server writes it (C# `Logs/LogTailer.cs`).
//!
//! POLLING, NOT A WATCHER. The server may be on a network mount or in a container where change
//! notifications are unreliable, and a watcher that silently stops firing looks exactly like a
//! quiet server.
//!
//! Four situations a naive "read from where I stopped" tailer gets wrong, each of which loses
//! data silently:
//!
//! - ROTATION. Pavlov starts a new log. A file shorter than the last offset is one signal; a
//!   changed first 64 bytes (the timestamped header) is the other, for a new log that already
//!   outgrew the old offset by the next poll.
//! - A PARTIAL LINE. A poll can land mid-write, so the text after the last newline is held.
//! - A SPLIT CHARACTER. A multi-byte UTF-8 character split across two reads is held as bytes
//!   until its other half arrives, rather than decoded as two replacement characters.
//! - A HUGE FIRST PASS. The first pass starts at the end, or (on a deliberate re-scan) reads at
//!   most the backfill cap.
//!
//! No advisory lock is taken: `std::fs::File::open` never calls `flock`, so a log the server
//! holds exclusively is still readable. The C# needed a runtime switch for that.
//!
//! The reads are blocking `std::fs` calls, bounded to [`MAX_READ`] per poll, on local files: the
//! same trade the C# makes. A caller on a busy runtime can run [`LogTailer::poll`] inside
//! `spawn_blocking`; it takes `&self` so an `Arc<LogTailer>` can be moved there.

use std::collections::HashMap;
use std::fs::File;
use std::io::{self, Read, Seek, SeekFrom};
use std::path::Path;
use std::sync::{Mutex, PoisonError};

use super::LogLine;

/// The default first-pass cap: enough to catch up across a restart, far short of a months-old log.
pub const DEFAULT_MAX_BACKFILL: u64 = 50 * 1024 * 1024;
/// How much one poll reads at most. A bigger backlog is read over the next polls.
pub const MAX_READ: u64 = 8 * 1024 * 1024;
/// How much of the start of a log identifies it.
const HEAD_BYTES: usize = 64;

#[derive(Debug, Default)]
struct FileState {
    offset: u64,
    /// A partial final line, held until its newline arrives.
    carry: String,
    /// The tail of a multi-byte character split across two reads.
    pending: Vec<u8>,
    primed: bool,
    /// The first bytes of the file as last seen: its identity, for rotation.
    head: Option<Vec<u8>>,
    /// Whether the current run of read failures was reported. WARN once per outage: the poll
    /// runs every 1.5 s, and logging every failure at Debug meant a dead pipeline said nothing.
    failure_reported: bool,
}

impl FileState {
    fn restart(&mut self) {
        self.offset = 0;
        self.carry.clear();
        self.pending.clear();
    }
}

/// The tailer. One instance follows any number of files, each with its own position.
#[derive(Debug)]
pub struct LogTailer {
    files: Mutex<HashMap<String, FileState>>,
    max_backfill: u64,
}

impl Default for LogTailer {
    fn default() -> Self {
        Self::new(DEFAULT_MAX_BACKFILL)
    }
}

impl LogTailer {
    #[must_use]
    pub fn new(max_backfill: u64) -> Self {
        Self {
            files: Mutex::new(HashMap::new()),
            max_backfill,
        }
    }

    /// Whatever has been appended to `path` since the last call.
    ///
    /// `from_start` reads the whole file (up to the backfill cap) on the first pass instead of
    /// starting at the end, for a deliberate re-scan.
    pub fn poll(&self, path: &str, from_start: bool) -> Vec<LogLine> {
        let mut files = self.files.lock().unwrap_or_else(PoisonError::into_inner);
        let state = files.entry(path.to_owned()).or_default();

        let length = match std::fs::metadata(path) {
            Ok(meta) if meta.is_file() => meta.len(),
            // Not there yet, or not a file: the server may not have started.
            _ => return Vec::new(),
        };

        if !state.primed {
            state.primed = true;
            record_head(path, state);
            // The END on a normal first pass: starting at zero re-announces every join in the file.
            state.offset = if from_start {
                length.saturating_sub(self.max_backfill)
            } else {
                length
            };
            if from_start && length > self.max_backfill {
                tracing::info!(
                    path,
                    size_mb = length / 1_048_576,
                    cap_mb = self.max_backfill / 1_048_576,
                    "log is larger than the backfill cap - reading only its tail"
                );
            }
        }

        if length < state.offset {
            // Rotated or truncated. Seeking past the end of the new file reads nothing forever.
            tracing::info!(
                path,
                was = state.offset,
                now = length,
                "log shrank - treating it as rotated and re-reading from the start"
            );
            state.restart();
            state.head = None;
        }

        if length == state.offset {
            return Vec::new();
        }

        match read_new(path, state, length) {
            Ok(lines) => {
                if state.failure_reported {
                    state.failure_reported = false;
                    tracing::info!(path, "log is readable again");
                }
                lines
            }
            Err(error) => {
                if state.failure_reported {
                    tracing::debug!(path, %error, "still cannot read the log");
                } else {
                    state.failure_reported = true;
                    tracing::warn!(
                        path,
                        %error,
                        "CANNOT READ the log. Nothing in the log pipeline works while this persists - \
                         no joins, kills, address tracking or ban-evasion checks. Check the bot can read \
                         the file and that PAVLOV_LOGS points at the right one"
                    );
                }
                Vec::new()
            }
        }
    }

    /// Forget one file's position (or every file's), so the next poll re-primes.
    pub fn reset(&self, path: Option<&str>) {
        let mut files = self.files.lock().unwrap_or_else(PoisonError::into_inner);
        match path {
            Some(path) => {
                files.remove(path);
            }
            None => files.clear(),
        }
    }

    #[must_use]
    pub fn offset_of(&self, path: &str) -> u64 {
        self.files
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .get(path)
            .map_or(0, |s| s.offset)
    }
}

fn read_new(path: &str, state: &mut FileState, length: u64) -> io::Result<Vec<LogLine>> {
    let mut file = File::open(path)?;

    if rotated(&mut file, state)? {
        tracing::info!(
            path,
            "log was replaced by a new one - reading it from the start"
        );
        state.restart();
    }

    file.seek(SeekFrom::Start(state.offset))?;
    let want = length.saturating_sub(state.offset).min(MAX_READ);
    let mut buffer = Vec::with_capacity(usize::try_from(want).unwrap_or(0));
    let read = file.by_ref().take(want).read_to_end(&mut buffer)?;
    if read == 0 {
        return Ok(Vec::new());
    }
    state.offset += read as u64;

    let mut bytes = std::mem::take(&mut state.pending);
    bytes.extend_from_slice(&buffer);
    let (decoded, rest) = decode_utf8(&bytes);
    state.pending = rest.to_vec();

    let mut text = std::mem::take(&mut state.carry);
    text.push_str(&decoded);

    // Whatever follows the final newline is a line still being written: held, not consumed.
    let mut parts: Vec<&str> = text.split('\n').collect();
    let partial = parts.pop().unwrap_or_default().to_owned();
    let lines = parts
        .into_iter()
        .map(|l| l.trim_end_matches('\r'))
        .filter(|l| !l.is_empty())
        .map(|l| LogLine::new(path, l))
        .collect();
    state.carry = partial;
    Ok(lines)
}

/// Decode as much of `bytes` as is complete. Invalid sequences become U+FFFD; an incomplete
/// sequence at the very end is handed back to wait for the next read.
fn decode_utf8(bytes: &[u8]) -> (String, &[u8]) {
    let mut out = String::with_capacity(bytes.len());
    let mut rest = bytes;
    loop {
        match std::str::from_utf8(rest) {
            Ok(valid) => {
                out.push_str(valid);
                return (out, &[]);
            }
            Err(error) => {
                let (valid, after) = rest.split_at(error.valid_up_to());
                // `valid_up_to` marks a proven-valid prefix, so this cannot fail.
                out.push_str(std::str::from_utf8(valid).unwrap_or_default());
                match error.error_len() {
                    None => return (out, after),
                    Some(bad) => {
                        out.push(char::REPLACEMENT_CHARACTER);
                        rest = &after[bad..];
                    }
                }
            }
        }
    }
}

/// Note the head of the log as it is now. Best effort: an unreadable file is caught later.
fn record_head(path: &str, state: &mut FileState) {
    if let Ok(mut file) = File::open(path) {
        let _ = rotated(&mut file, state);
    }
}

/// Whether the open file is a different log from the one last read. Records its head the first
/// time, and whenever it changes.
fn rotated(file: &mut File, state: &mut FileState) -> io::Result<bool> {
    file.seek(SeekFrom::Start(0))?;
    let mut head = Vec::with_capacity(HEAD_BYTES);
    file.by_ref()
        .take(HEAD_BYTES as u64)
        .read_to_end(&mut head)?;

    let Some(previous) = &state.head else {
        state.head = Some(head);
        return Ok(false);
    };
    // The recorded head was short because the file was: extend it.
    if previous.len() < HEAD_BYTES && head.len() > previous.len() && head.starts_with(previous) {
        state.head = Some(head);
        return Ok(false);
    }
    let comparable = previous.len().min(head.len());
    if head[..comparable] == previous[..comparable] {
        return Ok(false);
    }
    state.head = Some(head);
    Ok(true)
}

/// Where the Pavlov logs are: `PAVLOV_LOGS` if set, else one `Pavlov.log` under each install.
///
/// Only files that exist are returned: a tailer pointed at a stale or wrong path reads nothing
/// forever, which looks exactly like a quiet server. What was found is logged on the success
/// path too, so "which log is this bot reading" always has an answer.
#[must_use]
pub fn discover(configured: Option<&str>, install_roots: &[String]) -> Vec<String> {
    if let Some(configured) = configured.filter(|c| !c.trim().is_empty()) {
        let (found, missing): (Vec<String>, Vec<String>) = split_list(configured)
            .into_iter()
            .partition(|p| Path::new(p).is_file());
        if found.is_empty() {
            tracing::warn!(
                configured,
                "PAVLOV_LOGS is set but none of those files exist - log-derived features \
                 (ban evasion, join feed, kill stats) will do nothing"
            );
        } else {
            tracing::info!(count = found.len(), paths = %found.join(", "), "tailing Pavlov logs from PAVLOV_LOGS");
            for skipped in &missing {
                tracing::warn!(path = %skipped, "PAVLOV_LOGS names a file that does not exist - skipping it");
            }
        }
        return found;
    }

    // One log per install, discovered the same way as the ban files and whitelists. The old
    // fixed list skipped pavlovserver1, so the middle server of three was invisible.
    let candidates: Vec<String> = if install_roots.is_empty() {
        [
            "/home/steam/pavlovserver",
            "/home/steam/pavlovserver1",
            "/home/steam/pavlovserver2",
            "/opt/pavlovserver",
        ]
        .iter()
        .map(|root| pavlov_core::servers::installs::log_path(root))
        .collect()
    } else {
        install_roots
            .iter()
            .map(|root| pavlov_core::servers::installs::log_path(root))
            .collect()
    };
    let found = existing_distinct(candidates);
    if found.is_empty() {
        tracing::warn!(
            "no Pavlov.log found and PAVLOV_LOGS is not set - log-derived features are off"
        );
    } else {
        tracing::info!(count = found.len(), paths = %found.join(", "), "found Pavlov logs");
    }
    found
}

/// A comma-separated list, trimmed, blanks dropped.
pub(crate) fn split_list(configured: &str) -> Vec<String> {
    configured
        .split(',')
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .map(str::to_owned)
        .collect()
}

/// The paths that exist as files, first occurrence kept, order preserved.
pub(crate) fn existing_distinct(paths: Vec<String>) -> Vec<String> {
    let mut seen = std::collections::HashSet::new();
    paths
        .into_iter()
        .filter(|p| seen.insert(p.clone()))
        .filter(|p| Path::new(p).is_file())
        .collect()
}

#[cfg(test)]
mod tests {
    #![allow(clippy::unwrap_used)]
    use super::decode_utf8;

    #[test]
    fn an_incomplete_character_is_held_and_an_invalid_one_replaced() {
        let e_acute = "é".as_bytes();
        let split = [b'a', e_acute[0]];
        let (text, rest) = decode_utf8(&split);
        assert_eq!(text, "a");
        assert_eq!(rest, &e_acute[..1]);

        let invalid = [b'a', 0xFF, b'b'];
        let (text, rest) = decode_utf8(&invalid);
        assert_eq!(text, "a\u{FFFD}b");
        assert!(rest.is_empty());
    }
}
