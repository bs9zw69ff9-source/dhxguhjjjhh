//! A faction name as a slash-command option name (C# `Discord/CommandOptionName.cs`):
//! `^[a-z0-9_-]{1,32}$`, or `None`. Discord rejects a bulk command overwrite WHOLE for one bad
//! option name, which would empty the entire command picker, not just one command.

const MAX_LENGTH: usize = 32;

/// Lowercase ASCII letters and digits, punctuation and spaces collapsed to one underscore, the
/// FACTION part truncated (never the suffix, which says what the option takes). `None` for a
/// name with nothing usable in it: a made-up `faction_1` has no visible link to its faction.
#[must_use]
pub fn slug(name: Option<&str>, suffix: &str) -> Option<String> {
    let name = name.filter(|n| !n.trim().is_empty())?;
    let mut out = String::with_capacity(name.len() + suffix.len());
    let mut underscore = false;
    for c in name.chars() {
        if c.is_ascii_alphanumeric() {
            out.push(c.to_ascii_lowercase());
            underscore = false;
        } else if !underscore && !out.is_empty() {
            out.push('_');
            underscore = true;
        }
    }
    let room = MAX_LENGTH.checked_sub(suffix.len()).filter(|r| *r > 0)?;
    out.truncate(room);
    while out.ends_with('_') {
        out.pop();
    }
    if out.is_empty() {
        return None;
    }
    out.push_str(suffix);
    Some(out)
}
