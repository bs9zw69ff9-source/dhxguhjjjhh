//! Setting and clearing keys in a `.env` without leaving the old lines behind (C#
//! `Core/Provisioning/EnvFileEditor.cs`).
//!
//! APPEND-ONLY WAS NOT ENOUGH. A slot provisioned and deleted a few times left half a dozen
//! `RCON_HOST_2` lines stacked up, correct under last-key-wins and unreadable. So this rewrites:
//! every existing assignment of a named key is REMOVED - all of them - and the new value is
//! written once at the end. Clearing a key removes its lines and writes nothing.
//!
//! EVERY OTHER LINE IS PRESERVED BYTE FOR BYTE. The file is hand-maintained and full of comments
//! explaining why settings are what they are.

use chrono::NaiveDate;
use regex::{Regex, RegexBuilder};

use crate::patterns::static_regex;

/// The marker on comment lines this writes, so its own headers are cleaned up on the next pass
/// rather than accumulating beside the keys they used to describe.
pub const MARKER: &str = "# pavlov-bot:";

static_regex!(MARKER_COMMENT = r"(?i)^\s*# pavlov-bot:");

/// A line assigning `key` in either form the dotenv grammar accepts: an optional `export`, then
/// `KEY=` or `KEY: `. Case-insensitive because the reader is, so clearing `RCON_HOST_2` clears
/// `rcon_host_2` too.
fn assignment(key: &str) -> Option<Regex> {
    RegexBuilder::new(&format!(
        r"^\s*(?:export\s+)?{}\s*(?:=|:\s)",
        regex::escape(key)
    ))
    .case_insensitive(true)
    .build()
    .ok()
}

/// `contents` with these keys set - or removed, where the value is `None` - and nothing of their
/// previous lines left. `note` describes the change, on a marker line above the new keys.
#[must_use]
pub fn set(contents: &str, values: &[(String, Option<String>)], note: &str) -> String {
    // An escaped key always compiles; a key that somehow did not would match nothing, which
    // keeps every line rather than deleting any.
    let matchers: Vec<Regex> = values.iter().filter_map(|(k, _)| assignment(k)).collect();

    // CRLF normalised, so a file written on Windows is not left with stray '\r'.
    let normalised = contents.replace("\r\n", "\n");
    let mut kept: Vec<&str> = normalised
        .split('\n')
        .filter(|line| !matchers.iter().any(|m| m.is_match(line)))
        .filter(|line| !MARKER_COMMENT.is_match(line))
        .collect();

    // One trailing blank at most, so repeated edits do not walk the file downwards.
    while kept.last().is_some_and(|l| l.trim().is_empty()) {
        kept.pop();
    }

    let written: Vec<(&str, &str)> = values
        .iter()
        .filter_map(|(k, v)| v.as_deref().map(|v| (k.as_str(), v)))
        .collect();
    if written.is_empty() {
        // Everything asked for was a removal: the file just loses those lines.
        return kept.join("\n") + "\n";
    }

    let mut out = kept.join("\n");
    out.push_str("\n\n");
    out.push_str(&format!("{MARKER} {note}\n"));
    for (key, value) in written {
        out.push_str(&format!("{key}={value}\n"));
    }
    out
}

/// A dated note for the header line, so the file says when it was last machine-edited.
#[must_use]
pub fn note(what: &str, date: NaiveDate) -> String {
    format!("{what} on {}", date.format("%Y-%m-%d"))
}
