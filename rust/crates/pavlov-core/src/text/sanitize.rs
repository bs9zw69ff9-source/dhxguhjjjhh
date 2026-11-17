//! Attacker-controlled text made safe for a command or a message (C# `Core/Text/Sanitize.cs`).
//!
//! Every string here originates with a player. Two destinations need two treatments, and
//! using the wrong one is the bug:
//!
//! * RCON COMMANDS are space-delimited with no quoting, so a name containing a space or a
//!   newline is COMMAND INJECTION. [`Sanitize::id`] reduces to an alphabet that cannot
//!   express one.
//! * PUBLIC MESSAGES leak whatever the bot prints. [`Sanitize::redact_private`] is for text
//!   that leaves staff channels.

use std::sync::LazyLock;

use regex::Regex;

use super::NameLabels;
use crate::patterns::{static_fancy_regex, static_regex};

/// The bot's own display tags, stripped back off. BUILT FROM [`NameLabels::ALL`], which the C#
/// could not do (a source-generated pattern cannot read a runtime list) and so kept in step by a
/// test. No end anchor: a name that came back wearing two tags loses both.
static AUTOCOMPLETE_LABEL: LazyLock<Regex> = LazyLock::new(|| {
    let alternatives: Vec<String> = NameLabels::ALL.iter().map(|l| regex::escape(l)).collect();
    let pattern = format!(r"(?i)\s*\((?:{})\)", alternatives.join("|"));
    #[allow(clippy::expect_used)]
    Regex::new(&pattern).expect("escaped label alternation compiles")
});

static_regex!(SERVER_TAG = r"(?i)\s*\[(?:s1|s2|s1\+s2)\]");
static_regex!(NOT_ID_SAFE = r"[^a-zA-Z0-9_\-.]");
static_regex!(WHITESPACE = r"[\r\n\t]");
static_regex!(NON_PRINTABLE = r"[^\x20-\x7E]");
// Only what formats mid-line: every name here is printed after a bullet, so line-start markers
// (#, -, >) cannot fire and escaping them would litter names with backslashes.
static_regex!(MARKDOWN_SPECIAL = r"[\\*_~`|\[\]]");
static_regex!(IPV4 = r"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b");
static_fancy_regex!(MAYBE_IPV6 = r"(?i)(?<![\w:.])[0-9a-f:]{3,}(?![\w:])");
static_regex!(LONG_HEX = r"(?i)\b[0-9a-f]{24,}\b");
static_regex!(SNOWFLAKE = r"\b\d{17,20}\b");
static_fancy_regex!(HOME_PATH = r"/(?:home/[^\s/]+|root)(?=/|\b)");

/// Longest id that may become one RCON argument.
const MAX_ID_CHARS: usize = 64;
/// Longest broadcast message.
const MAX_MESSAGE_CHARS: usize = 200;

/// Namespace for the sanitisers, mirroring the C# static class.
#[derive(Debug)]
pub struct Sanitize;

impl Sanitize {
    /// A player name or id, reduced to what can safely be a single RCON argument.
    ///
    /// An ALLOW-list: only letters, digits, underscore, hyphen and dot can come out, capped at
    /// 64 characters. The output is ASCII, so the cap counts characters and bytes alike.
    #[must_use]
    pub fn id(raw: Option<&str>) -> String {
        let text = raw.unwrap_or("");
        let text = AUTOCOMPLETE_LABEL.replace_all(text, "");
        let text = SERVER_TAG.replace_all(&text, "");
        let text = NOT_ID_SAFE.replace_all(text.trim(), "");
        truncate_ascii(&text, MAX_ID_CHARS)
    }

    /// A free-text message bound for an RCON broadcast.
    ///
    /// Newlines become spaces rather than vanishing: the protocol is line oriented, so an
    /// embedded newline would split one command into two. Non-printables go entirely.
    #[must_use]
    pub fn message(raw: Option<&str>) -> String {
        let text = WHITESPACE.replace_all(raw.unwrap_or(""), " ");
        let text = NON_PRINTABLE.replace_all(&text, "");
        truncate_ascii(&text, MAX_MESSAGE_CHARS)
    }

    /// A name safe to print OUTSIDE a code span: Discord's mid-line markers are escaped with a
    /// backslash rather than stripped, so the name still reads as the player is called.
    #[must_use]
    pub fn markdown(raw: Option<&str>) -> String {
        MARKDOWN_SPECIAL
            .replace_all(&Self::message(raw), r"\$0")
            .into_owned()
    }

    /// Scrub anything private out of text bound for a PUBLIC surface: IPv4, IPv6 (two or more
    /// colons, which over-matches clock ranges on purpose), long hex runs, Discord snowflakes,
    /// and `/home/<user>` or `/root` paths.
    #[must_use]
    pub fn redact_private(text: Option<&str>) -> String {
        let output = IPV4.replace_all(text.unwrap_or(""), "[ip redacted]");
        let output = replace_fancy(&MAYBE_IPV6, &output, |m| {
            if m.matches(':').count() >= 2 {
                "[ip redacted]".to_owned()
            } else {
                m.to_owned()
            }
        });
        let output = LONG_HEX.replace_all(&output, "[id redacted]");
        let output = SNOWFLAKE.replace_all(&output, "[id redacted]");
        replace_fancy(&HOME_PATH, &output, |_| "[path redacted]".to_owned())
    }

    /// A name made safe INSIDE a code span: backticks become apostrophes, because escaping
    /// does not work there - the span just ends.
    #[must_use]
    pub fn code(name: Option<&str>) -> String {
        name.unwrap_or("").replace('`', "'")
    }
}

/// The first `max` characters of text already reduced to ASCII.
fn truncate_ascii(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

/// `fancy_regex` replace-all with a closure. A match that hits the backtracking limit is left
/// as it was rather than failing the whole redaction: under-redacting one token is visible in
/// the output, a panic in a log path is not.
fn replace_fancy(
    pattern: &fancy_regex::Regex,
    text: &str,
    replace: impl Fn(&str) -> String,
) -> String {
    let mut out = String::with_capacity(text.len());
    let mut last = 0;
    for found in pattern.find_iter(text) {
        let Ok(m) = found else { break };
        out.push_str(&text[last..m.start()]);
        out.push_str(&replace(m.as_str()));
        last = m.end();
    }
    out.push_str(&text[last..]);
    out
}
