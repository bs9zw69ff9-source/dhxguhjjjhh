//! The `.env` grammar (C# `Configuration/DotEnvConfiguration.cs`).
//!
//! The point is that the bot reads ONE file exactly the way dotenv 16 does - the way the Node
//! bot read it and the C# bot reads it. A subtle difference in parsing does not look like a
//! parsing bug: it looks like a wrong password, a truncated webhook URL or a token with a stray
//! quote in it. So this matches dotenv's grammar rather than inventing a cleaner one:
//!
//! - `export ` prefixes are allowed and ignored
//! - `KEY=value` and `KEY: value` both bind
//! - single, double and backtick quotes are stripped; escapes are expanded only inside double
//!   quotes, and only `\n` and `\r`
//! - an UNQUOTED value ends at the first `#`: dotenv's inline-comment rule, which means a value
//!   containing a hash MUST be quoted. Diverging to be "safer" would make the same file mean
//!   different things to different builds, which is worse.
//! - a key repeated later in the file wins, and an empty value is `""`, not absent

use std::sync::LazyLock;

use regex::Regex;

use crate::Config;

/// dotenv 16's LINE expression, kept as one pattern so the correspondence with its source is
/// checkable. It needs only lazy quantifiers, which the `regex` crate has, and matches with the
/// same leftmost-first semantics as the .NET engine the C# runs it on.
static LINE: LazyLock<Regex> = LazyLock::new(|| {
    // A constant pattern: if it failed to compile, every test of this module would fail first.
    #[allow(clippy::expect_used)]
    Regex::new(concat!(
        r"(?m)^\s*(?:export\s+)?([\w.-]+)(?:\s*=\s*?|:\s+?)",
        r#"(\s*'(?:\\'|[^'])*'|\s*"(?:\\"|[^"])*"|\s*`(?:\\`|[^`])*`|[^#\r\n]+)?"#,
        r"\s*(?:#.*)?$",
    ))
    .expect("the dotenv line pattern compiles")
});

/// Parse the contents of a `.env` file.
#[must_use]
pub fn parse(contents: &str) -> Config {
    let contents = contents.replace("\r\n", "\n");
    let mut config = Config::default();

    for line in LINE.captures_iter(&contents) {
        let Some(key) = line.get(1).map(|k| k.as_str()).filter(|k| !k.is_empty()) else {
            continue;
        };
        let raw = line.get(2).map_or("", |v| v.as_str().trim());
        config.set(key, unquote(raw));
    }
    config
}

fn unquote(raw: &str) -> String {
    let bytes = raw.as_bytes();
    if bytes.len() < 2 {
        return raw.to_owned();
    }

    let first = bytes[0];
    if matches!(first, b'"' | b'\'' | b'`') && bytes[bytes.len() - 1] == first {
        // Both ends are ASCII quotes, so slicing inside them is on a character boundary.
        let inner = &raw[1..raw.len() - 1];
        /* ONLY \n AND \r, and only inside double quotes. dotenv does not unescape \" - a
           backslash-quote stays a literal backslash-quote - and the C# port once did, which
           would have silently changed the value of any token containing one. Single and
           backtick quotes are literal. */
        return if first == b'"' {
            inner.replace("\\n", "\n").replace("\\r", "\r")
        } else {
            inner.to_owned()
        };
    }
    raw.to_owned()
}
