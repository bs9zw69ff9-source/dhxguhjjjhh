//! A `FACTIONS_PATH` file: the faction ladders, replacing the built-in set entirely (C#
//! `Factions/FactionsFile.cs`).
//!
//! - Comments and trailing commas are ACCEPTED: this file is hand-edited at 2am, and strict
//!   JSON's refusal to explain a trailing comma is a bad experience for a config file. They are
//!   blanked to spaces, not removed, so a parse error's line and column still point at the file.
//! - Property names are matched ignoring case, as the C# reader did.
//! - A path that does not exist is REFUSED, never the built-ins: falling back would start a bot
//!   meant for other ladders running the police roster.
//! - Every problem is reported at once, and a set with any problem is not returned.

use std::path::Path;

use indexmap::IndexMap;
use pavlov_core::factions::{FactionDefinition, FactionSet};
use serde_json::{Map, Value};

/// What loading found.
#[derive(Debug, Clone)]
pub struct FactionsFileResult {
    /// The set, only when there were no problems.
    pub set: Option<FactionSet>,
    pub problems: Vec<String>,
    /// Ranks that still set `cap`, which no longer does anything: counted for the startup
    /// summary rather than refused or silently ignored.
    pub ignored_rank_caps: usize,
}

impl FactionsFileResult {
    fn failed(problem: String) -> Self {
        Self {
            set: None,
            problems: vec![problem],
            ignored_rank_caps: 0,
        }
    }
}

/// JSON with comments and trailing commas made strict, positions preserved: every removed
/// character becomes a space (a newline inside a block comment stays a newline).
#[must_use]
pub fn strip_relaxed_json(text: &str) -> String {
    let chars: Vec<char> = text.chars().collect();
    let mut out: Vec<char> = Vec::with_capacity(chars.len());
    let mut i = 0;
    let mut in_string = false;
    while i < chars.len() {
        let c = chars[i];
        if in_string {
            out.push(c);
            if c == '\\' && i + 1 < chars.len() {
                out.push(chars[i + 1]);
                i += 2;
                continue;
            }
            if c == '"' {
                in_string = false;
            }
            i += 1;
            continue;
        }
        match (c, chars.get(i + 1)) {
            ('"', _) => {
                in_string = true;
                out.push(c);
                i += 1;
            }
            ('/', Some('/')) => {
                while i < chars.len() && chars[i] != '\n' {
                    out.push(' ');
                    i += 1;
                }
            }
            ('/', Some('*')) => {
                out.extend([' ', ' ']);
                i += 2;
                while i < chars.len() && !(chars[i] == '*' && chars.get(i + 1) == Some(&'/')) {
                    out.push(if chars[i] == '\n' { '\n' } else { ' ' });
                    i += 1;
                }
                if i < chars.len() {
                    out.extend([' ', ' ']);
                    i += 2;
                }
            }
            (',', _) => {
                // A comma followed only by whitespace (and comments, already blanked by the
                // time the closer is read) before `]` or `}` is trailing.
                let mut j = i + 1;
                let mut closer = false;
                while j < chars.len() {
                    match (chars[j], chars.get(j + 1)) {
                        (w, _) if w.is_whitespace() => j += 1,
                        ('/', Some('/')) => {
                            while j < chars.len() && chars[j] != '\n' {
                                j += 1;
                            }
                        }
                        ('/', Some('*')) => {
                            j += 2;
                            while j < chars.len()
                                && !(chars[j] == '*' && chars.get(j + 1) == Some(&'/'))
                            {
                                j += 1;
                            }
                            j += 2;
                        }
                        (']' | '}', _) => {
                            closer = true;
                            break;
                        }
                        _ => break,
                    }
                }
                out.push(if closer { ' ' } else { ',' });
                i += 1;
            }
            _ => {
                out.push(c);
                i += 1;
            }
        }
    }
    out.into_iter().collect()
}

fn field<'a>(object: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    object
        .iter()
        .find(|(k, _)| k.eq_ignore_ascii_case(name))
        .map(|(_, v)| v)
}

fn text<'a>(object: &'a Map<String, Value>, name: &str) -> Option<&'a str> {
    field(object, name)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|s| !s.is_empty())
}

fn named_files(object: &Map<String, Value>, list: &str) -> (Vec<(String, String)>, usize) {
    let mut caps = 0;
    let entries = field(object, list)
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(Value::as_object)
                .filter_map(|rank| {
                    let name = text(rank, "name")?;
                    let file = text(rank, "file")?;
                    if field(rank, "cap")
                        .and_then(Value::as_i64)
                        .is_some_and(|c| c > 0)
                    {
                        caps += 1;
                    }
                    Some((name.to_owned(), file.to_owned()))
                })
                .collect()
        })
        .unwrap_or_default();
    (entries, caps)
}

/// Load the file.
#[must_use]
pub fn load(path: &Path) -> FactionsFileResult {
    let shown = path.display();
    if path.as_os_str().is_empty() {
        return FactionsFileResult::failed("No faction file path was given.".into());
    }
    if !path.exists() {
        return FactionsFileResult::failed(format!("{shown} does not exist."));
    }
    let raw = match std::fs::read_to_string(path) {
        Ok(raw) => raw,
        Err(error) => {
            return FactionsFileResult::failed(format!("{shown} could not be read: {error}"));
        }
    };
    let parsed: Value = match serde_json::from_str(&strip_relaxed_json(&raw)) {
        Ok(value) => value,
        // The message carries the line and column, which is the whole value of it.
        Err(error) => {
            return FactionsFileResult::failed(format!("{shown} is not valid JSON: {error}"));
        }
    };
    let factions = parsed
        .as_object()
        .and_then(|root| field(root, "factions"))
        .and_then(Value::as_array)
        .filter(|f| !f.is_empty());
    let Some(factions) = factions else {
        return FactionsFileResult::failed(format!("{shown} defines no factions."));
    };

    let mut problems = Vec::new();
    let mut definitions = Vec::new();
    let mut ignored_rank_caps = 0;
    for faction in factions {
        let Some(faction) = faction.as_object() else {
            problems.push("A faction is not an object.".to_owned());
            continue;
        };
        let Some(name) = text(faction, "name") else {
            problems.push("A faction has no name.".to_owned());
            continue;
        };
        let (ranks, caps) = named_files(faction, "ranks");
        ignored_rank_caps += caps;
        if ranks.is_empty() {
            problems.push(format!(
                "{name} has no usable ranks - each needs a name and a file."
            ));
            continue;
        }
        let order: Vec<String> = ranks.iter().map(|(n, _)| n.clone()).collect();
        let (subclasses, _) = named_files(faction, "subclasses");
        definitions.push(FactionDefinition {
            name: name.to_owned(),
            // The lowest rank: where a new member starts, right with no configuration.
            default: text(faction, "default").map_or_else(|| order[0].clone(), str::to_owned),
            // A ladderless faction's one file is its spawn file.
            spawn_file: text(faction, "spawnFile")
                .map_or_else(|| ranks[0].1.clone(), str::to_owned),
            rank_files: ranks.into_iter().collect::<IndexMap<_, _>>(),
            subclasses: subclasses.into_iter().collect(),
            order,
            former_names: Vec::new(),
        });
    }
    if definitions.is_empty() {
        return FactionsFileResult {
            set: None,
            problems,
            ignored_rank_caps,
        };
    }
    let set = FactionSet::of(definitions);
    problems.extend(set.problems());
    FactionsFileResult {
        set: problems.is_empty().then_some(set),
        problems,
        ignored_rank_caps,
    }
}

/// Whether a missing `FACTIONS_PATH` may fall back to a named built-in set (C#
/// `Program.MayFallBackToSet`). Only a file that is NOT there, with a set name that IS a set:
/// a typo is not a set, a removed one is not either, and with no set named a missing file is
/// fatal, since starting would run the police ladders on a bot meant for others.
#[must_use]
pub fn may_fall_back_to_set(path: Option<&Path>, set_name: Option<&str>) -> bool {
    path.is_some_and(|p| !p.as_os_str().is_empty() && !p.exists())
        && set_name.is_some_and(|s| !s.is_empty())
        && pavlov_core::factions::FactionRegistry::preset(set_name).is_some()
}

/// Where the factions come from, for the startup summary (C# `Program.FactionSource`). The
/// file wins when both are set, so it is named first; a relative path is resolved against
/// `cwd`, the directory the process is actually standing in; a file that exists carries its
/// last edit, so an undeployed change is visible; and a file that is NOT there is never named
/// as the source.
#[must_use]
pub fn describe_source(path: Option<&Path>, set_name: Option<&str>, cwd: &Path) -> String {
    let set_name = set_name.filter(|s| !s.is_empty());
    let Some(path) = path else {
        return match set_name {
            Some(set) => format!("the built-in \"{set}\" set (no FACTIONS_PATH)"),
            None => "built in (neither FACTION_SET nor FACTIONS_PATH set)".to_owned(),
        };
    };
    let full = if path.is_absolute() {
        path.to_path_buf()
    } else {
        cwd.join(path)
    };
    let shown = full.display();
    if full.is_file() {
        let edited = std::fs::metadata(&full)
            .and_then(|m| m.modified())
            .map(|t| {
                chrono::DateTime::<chrono::Utc>::from(t)
                    .format("%Y-%m-%d %H:%M")
                    .to_string()
            });
        return match edited {
            Ok(at) => format!("{shown} (last edited {at}Z)"),
            Err(_) => shown.to_string(),
        };
    }
    match set_name {
        Some(set) => format!("the built-in \"{set}\" set ({shown} does not exist)"),
        None => shown.to_string(),
    }
}
