//! Reading the Pavlov master server's list, for `/server lookup` (C# `Core/Servers/ServerListing.cs`).
//!
//! PURE, and separate from anything that makes a request, so the shape of the response is
//! testable against a captured body. The list is third-party and can change under us; a parser
//! that failed on one odd row would take the whole browser down, so a row that cannot be read
//! is SKIPPED and the rest are returned.

use chrono::{DateTime, Utc};
use serde_json::{Map, Value};

use crate::json::parse_iso;
use crate::text::case::{IgnoreCaseSet, cmp_ignore_case, contains_ignore_case};

/// One server as the master server describes it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerListing {
    pub name: String,
    pub ip: String,
    pub port: i32,
    /// How many are on right now. The API calls this "slots".
    pub players: i32,
    /// Capacity. The API calls this "maxSlots".
    pub max_players: i32,
    /// The workshop id, e.g. "UGC3480960", or a stock map name.
    pub map_id: String,
    pub map_label: String,
    pub game_mode: String,
    pub game_mode_label: String,
    pub version: String,
    pub password_protected: bool,
    /// Anti-cheat enforced.
    pub secured: bool,
    /// When the server last checked in. `None` when unparseable.
    pub updated: Option<DateTime<Utc>>,
}

impl ServerListing {
    /// Stable identity, and short enough to ride in a Discord component value.
    #[must_use]
    pub fn key(&self) -> String {
        format!("{}:{}", self.ip, self.port)
    }

    /// Full only with a known capacity: 0 of 0 is unknown, not full.
    #[must_use]
    pub fn full(&self) -> bool {
        self.max_players > 0 && self.players >= self.max_players
    }

    /// The map is a workshop upload rather than a stock one.
    #[must_use]
    pub fn is_workshop_map(&self) -> bool {
        self.map_id
            .get(..3)
            .is_some_and(|p| p.eq_ignore_ascii_case("UGC"))
    }
}

/// Parse a master server response. Empty rather than an error for anything malformed.
#[must_use]
pub fn parse(json: &str) -> Vec<ServerListing> {
    if json.trim().is_empty() {
        return Vec::new();
    }
    serde_json::from_str::<Value>(json).map_or_else(|_| Vec::new(), |root| read(&root))
}

/// The listings in an already-parsed response.
#[must_use]
pub fn read(root: &Value) -> Vec<ServerListing> {
    root.get("servers")
        .and_then(Value::as_array)
        .map(|servers| servers.iter().filter_map(read_one).collect())
        .unwrap_or_default()
}

fn read_one(element: &Value) -> Option<ServerListing> {
    let element = element.as_object()?;
    let name = text(element, "name");
    let ip = text(element, "ip");
    // A row with no name or no address identifies nothing and cannot be connected to.
    if name.is_empty() || ip.is_empty() {
        return None;
    }

    let mode = text(element, "gameMode");
    let mode_label = text(element, "gameModeLabel");
    let map_id = text(element, "mapId");
    let map_label = text(element, "mapLabel");

    Some(ServerListing {
        name,
        ip,
        port: number(element, "port"),
        players: number(element, "slots"),
        max_players: number(element, "maxSlots"),
        // The label is what a person recognises; the id is the fallback because a workshop map
        // with no label still needs SOMETHING in the map field.
        map_label: if map_label.is_empty() {
            map_id.clone()
        } else {
            map_label
        },
        map_id,
        game_mode_label: if mode_label.is_empty() {
            mode.clone()
        } else {
            mode_label
        },
        game_mode: mode,
        version: text(element, "version"),
        password_protected: flag(element, "bPasswordProtected"),
        secured: flag(element, "bSecured"),
        updated: parse_iso(text(element, "updated").trim()),
    })
}

fn text(element: &Map<String, Value>, name: &str) -> String {
    element
        .get(name)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_owned()
}

fn number(element: &Map<String, Value>, name: &str) -> i32 {
    element
        .get(name)
        .and_then(Value::as_i64)
        .and_then(|n| i32::try_from(n).ok())
        .unwrap_or(0)
}

fn flag(element: &Map<String, Value>, name: &str) -> bool {
    element.get(name) == Some(&Value::Bool(true))
}

/// The browse order: busiest first, then by name, DEDUPLICATED BY ADDRESS.
///
/// Two rows sharing an address would become two select options with the same value, and
/// Discord rejects the entire message for that - so a duplicate upstream would present as a
/// browser that silently shows nothing. The listing kept is the busier one, since ordering has
/// already run. Full servers stay in place: "full" is information, not a reason to hide.
#[must_use]
pub fn ordered(servers: impl IntoIterator<Item = ServerListing>) -> Vec<ServerListing> {
    let mut all: Vec<ServerListing> = servers.into_iter().collect();
    // Stable, as LINQ's OrderBy is.
    all.sort_by(|a, b| {
        b.players
            .cmp(&a.players)
            .then_with(|| cmp_ignore_case(&a.name, &b.name))
    });
    let mut seen = IgnoreCaseSet::new();
    all.into_iter().filter(|s| seen.insert(s.key())).collect()
}

/// Servers whose name, map, mode or address matches `query`. An empty query matches everything:
/// a cleared search box should show the list again, not an empty one.
#[must_use]
pub fn matching<'a>(
    servers: impl IntoIterator<Item = &'a ServerListing>,
    query: Option<&str>,
) -> Vec<&'a ServerListing> {
    let needle = query.map(str::trim).unwrap_or_default();
    servers
        .into_iter()
        .filter(|s| {
            needle.is_empty()
                || contains_ignore_case(&s.name, needle)
                || contains_ignore_case(&s.map_label, needle)
                || contains_ignore_case(&s.game_mode_label, needle)
                || contains_ignore_case(&s.key(), needle)
        })
        .collect()
}
