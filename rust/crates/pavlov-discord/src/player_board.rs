//! The live player board: who is on, which server, and which faction (C# `PlayerBoard.cs`).
//!
//! - ONE INLINE FIELD PER SERVER puts the servers side by side: Discord lays inline fields
//!   three to a row, so a three-server install reads as three columns of names.
//! - NAMES ARE PRINTED PLAIN, escaped rather than in code spans (a wall of grey boxes at phone
//!   width), so an unclosed marker in one name cannot restyle the column.
//! - A FULL SERVER DOES NOT FIT IN A FIELD (1024 characters). The list continues into another
//!   column, and past [`MAX_COLUMNS_PER_SERVER`] the last column says how many names it could
//!   not show. A board quietly listing the alphabetical first forty would answer "is so-and-so
//!   on" with no for half the server.
//! - PURE: handed what is known, so a stale roster, a server that never answered and an
//!   oversized roster are testable without RCON or a gateway.

use std::sync::LazyLock;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::embed::{Embed, palette};
use pavlov_core::skin::{EmbedBudget, lore, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::time::EasternTime;
use regex::Regex;

/// Room kept back for the "and N more" line, so it always fits once needed.
const TAIL_ALLOWANCE: usize = 28;

/// Columns one server's list may spill across: around 170 names, past any Pavlov server, and
/// there so one absurd roster cannot take every column from the other servers.
pub const MAX_COLUMNS_PER_SERVER: usize = 4;

pub const TITLE: &str = "Live Player List";

/// One name on the board, with their faction when they are on a roster.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BoardPlayer {
    pub name: String,
    pub faction: Option<String>,
}

impl BoardPlayer {
    #[must_use]
    pub fn new(name: impl Into<String>, faction: Option<&str>) -> Self {
        Self {
            name: name.into(),
            faction: faction.map(str::to_owned),
        }
    }
}

/// One server's contribution.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BoardRoster {
    pub server: String,
    pub players: Vec<BoardPlayer>,
    /// How old the list is, or `None` when no sweep has landed. Not the same thing as empty.
    pub age: Option<TimeDelta>,
    /// The sweep is overdue. The names are still shown, marked.
    pub stale: bool,
    /// Why the last sweep failed, when it did.
    pub problem: Option<String>,
}

/// `42 minutes`: how old something is, for a person (C# `PlayersCommand.Describe`).
#[must_use]
#[allow(clippy::cast_precision_loss)]
pub fn describe_age(age: TimeDelta) -> String {
    let seconds = age.num_milliseconds() as f64 / 1000.0;
    let round = |v: f64| format!("{:.0}", v.round());
    if seconds < 90.0 {
        format!("{} seconds", round(seconds))
    } else if seconds / 60.0 < 90.0 {
        format!("{} minutes", round(seconds / 60.0))
    } else if seconds / 3600.0 < 36.0 {
        format!("{} hours", round(seconds / 3600.0))
    } else {
        format!("{} days", round(seconds / 86_400.0))
    }
}

static INTERNAL_NAME: LazyLock<Option<Regex>> =
    LazyLock::new(|| Regex::new(r"(?i)^server\s*(\d+)$").ok());

/// `Server 2`: what a player calls the server, not the config key. Any other name was chosen
/// deliberately and is printed as it stands.
#[must_use]
pub fn display_name(server: &str) -> String {
    match INTERNAL_NAME.as_ref().and_then(|re| re.captures(server)) {
        Some(found) => format!("Server {}", &found[1]),
        None => Sanitize::message(Some(server)),
    }
}

fn label(server: &BoardRoster) -> String {
    match server.age {
        None => format!("{} (no roster yet)", display_name(&server.server)),
        Some(_) => format!(
            "{} ({})",
            display_name(&server.server),
            server.players.len()
        ),
    }
}

fn has_problem(server: &BoardRoster) -> bool {
    server.problem.as_deref().is_some_and(|p| !p.is_empty())
}

/// One server's whole column: how old the list is, what went wrong, then the names. The
/// notes come first so they are never what gets squeezed out.
fn lines(server: &BoardRoster) -> Vec<String> {
    let mut lines = Vec::new();
    match server.age {
        // Only when nothing went wrong: a sweep that FAILED has run.
        None if !has_problem(server) => lines.push("The sweep has not run yet.".to_owned()),
        Some(age) if server.stale => {
            lines.push(format!(
                "{} **as of {} ago**",
                theme::WARN,
                describe_age(age)
            ));
        }
        _ => {}
    }
    if let Some(problem) = server.problem.as_deref().filter(|p| !p.is_empty()) {
        lines.push(format!(
            "{} {}",
            theme::BAD,
            Sanitize::message(Some(problem))
        ));
    }
    if server.age.is_none() {
        return lines;
    }
    let names: Vec<String> = server
        .players
        .iter()
        .filter(|p| !p.name.trim().is_empty())
        .map(|p| {
            let tag = p
                .faction
                .as_deref()
                .filter(|f| !f.trim().is_empty())
                .map(|f| format!(" — {}", Sanitize::markdown(Some(f))))
                .unwrap_or_default();
            format!("{} {}{tag}", theme::DOT, Sanitize::markdown(Some(&p.name)))
        })
        .collect();
    if names.is_empty() {
        lines.push("*nobody online*".to_owned());
    }
    lines.extend(names);
    lines
}

/// Split a column too long for one field across several; past the cap, the last column says
/// how many lines went rather than ending mid-list.
fn columns(lines: &[String]) -> Vec<String> {
    let mut columns = Vec::new();
    let mut current = String::new();
    let mut current_len = 0;
    let mut index = 0;
    while index < lines.len() && columns.len() < MAX_COLUMNS_PER_SERVER {
        let last = columns.len() == MAX_COLUMNS_PER_SERVER - 1;
        let line = &lines[index];
        let line_len = line.chars().count();
        let needed = line_len + usize::from(current_len != 0);
        let reserve = if last && index + 1 < lines.len() {
            TAIL_ALLOWANCE
        } else {
            0
        };
        if current_len + needed + reserve <= EmbedBudget::FIELD_LIMIT {
            if current_len > 0 {
                current.push('\n');
            }
            current.push_str(line);
            current_len += needed;
            index += 1;
            continue;
        }
        // Only a line longer than a whole field fits nowhere, and Sanitize caps every one at
        // 200 characters; an empty column is never pushed for it.
        if current_len == 0 {
            index += 1;
            continue;
        }
        columns.push(std::mem::take(&mut current));
        current_len = 0;
    }
    if index < lines.len() {
        if current_len > 0 {
            current.push('\n');
        }
        current.push_str(&format!("*… and {} more*", lines.len() - index));
    }
    if !current.is_empty() {
        columns.push(current);
    }
    columns
}

/// The board, or `None` in the RESTART WINDOW only: before the first sweep lands every server
/// reads "no roster yet", and posting that overwrites a good list on every restart. A sweep
/// that is FAILING never recovers on its own, so the board posts and says why.
#[must_use]
pub fn build(servers: &[BoardRoster], at: DateTime<Utc>) -> Option<Embed> {
    let answered: Vec<&BoardRoster> = servers.iter().filter(|s| s.age.is_some()).collect();
    let broken = servers.iter().any(has_problem);
    if !servers.is_empty() && answered.is_empty() && !broken {
        return None;
    }
    let total: usize = answered.iter().map(|s| s.players.len()).sum();
    let stale = answered.iter().any(|s| s.stale);
    let description = if servers.is_empty() {
        "No servers are configured.".to_owned()
    } else if answered.is_empty() {
        "No server has answered yet.".to_owned()
    } else {
        let count = i64::try_from(total).unwrap_or(i64::MAX);
        format!("> **{total}** *{}*", lore::roaming(count))
    };
    let color = if answered.is_empty() {
        palette::GREY
    } else if stale || broken {
        palette::AMBER
    } else {
        palette::GREEN
    };
    let mut budget = EmbedBudget::new(
        TITLE.chars().count() + description.chars().count() + EmbedBudget::FOOTER_ALLOWANCE,
    );
    let mut embed = Embed::new()
        .color(color)
        .title(TITLE)
        .description(description);
    let mut dropped = 0;
    for server in servers {
        for (column, value) in columns(&lines(server)).iter().enumerate() {
            let name = if column == 0 {
                label(server)
            } else {
                format!("{} (cont.)", display_name(&server.server))
            };
            if !budget.add(&mut embed, &name, Some(value), true) {
                dropped += 1;
                break;
            }
        }
    }
    let mut footer = format!("Updated {} Eastern", EasternTime::stamp(Some(at)));
    if dropped > 0 {
        footer.push_str(&format!(" · {dropped} column(s) would not fit"));
    }
    Some(embed.brand(Some(&footer)))
}
