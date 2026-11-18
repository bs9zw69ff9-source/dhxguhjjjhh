//! Reading Pavlov's replies (C# `PavlovBot.Rcon/Protocol/RconReply.cs`).
//!
//! An untyped JSON tree rather than typed records, for two reasons from the running bot:
//!
//! - PAVLOV IS INCONSISTENT ABOUT PROPERTY NAMES. The same field arrives as `Username`,
//!   `username`, `PlayerName`, `name` or `Name` depending on the build and the command.
//!   Coalescing once, here, is one place to fix.
//! - A REPLY IS NOT ALWAYS JSON. Failures come back as bare text, so parsing returns `None` and
//!   the caller decides.

use serde_json::Value;

use pavlov_core::text::case::eq_ignore_case;

/// Parse a reply body. `None` when it is not JSON at all.
#[must_use]
pub fn try_parse(raw: Option<&str>) -> Option<Value> {
    let raw = raw?;
    if raw.trim().is_empty() {
        return None;
    }
    serde_json::from_str(raw).ok()
}

/// A property, trying each spelling exactly, then each case-insensitively.
#[must_use]
pub fn property<'a>(element: &'a Value, names: &[&str]) -> Option<&'a Value> {
    let object = element.as_object()?;
    names.iter().find_map(|n| object.get(*n)).or_else(|| {
        // Only reached on an unknown shape, so the sweep is not paid on the happy path.
        object
            .iter()
            .find(|(key, _)| names.iter().any(|n| eq_ignore_case(key, n)))
            .map(|(_, v)| v)
    })
}

/// A property as trimmed text, or `None` when absent or empty.
#[must_use]
pub fn text(element: &Value, names: &[&str]) -> Option<String> {
    let text = match property(element, names)? {
        Value::String(s) => s.clone(),
        Value::Number(n) => n.to_string(),
        Value::Bool(b) => b.to_string(),
        _ => return None,
    };
    let text = text.trim();
    (!text.is_empty()).then(|| text.to_owned())
}

/// A property as an integer, tolerating one delivered as a string.
#[must_use]
pub fn int(element: &Value, names: &[&str]) -> Option<i32> {
    match property(element, names)? {
        Value::Number(n) => n.as_i64().and_then(|n| i32::try_from(n).ok()),
        Value::String(s) => s.trim().parse().ok(),
        _ => None,
    }
}

/// Whether the server reported the command as successful. `None` means it did not say - which
/// is NOT the same as saying no, and callers must not collapse the two.
#[must_use]
pub fn successful(root: &Value) -> Option<bool> {
    property(root, &["Successful", "successful"])?.as_bool()
}

/// One player on a server.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct PavlovPlayer {
    /// Display name. On Pavlov Shack this is what Ban, Kick and Unban target on the wire; may be
    /// empty when the server only reported an id.
    pub name: String,
    /// The EOS account id. Recorded for evasion flags and evidence; NOT the RCON target on Shack.
    pub unique_id: String,
}

/// Every player in a RefreshList reply. Empty when the reply is unusable.
#[must_use]
pub fn players(root: &Value) -> Vec<PavlovPlayer> {
    let Some(list) = property(root, &["PlayerList", "playerList"]).and_then(Value::as_array) else {
        return Vec::new();
    };
    list.iter()
        .filter_map(|entry| {
            let name = text(
                entry,
                &["Username", "username", "PlayerName", "name", "Name"],
            )
            .unwrap_or_default();
            let unique_id =
                text(entry, &["UniqueId", "uniqueId", "UniqueID", "Id", "id"]).unwrap_or_default();
            // An entry with neither is noise, not a player.
            (!name.is_empty() || !unique_id.is_empty()).then_some(PavlovPlayer { name, unique_id })
        })
        .collect()
}

/// Just the non-empty display names, as the roster surfaces want them.
#[must_use]
pub fn names(root: &Value) -> Vec<String> {
    players(root)
        .into_iter()
        .map(|p| p.name)
        .filter(|n| !n.is_empty())
        .collect()
}

/// A ServerInfo reply. Every field is `None` when unreported - never defaulted here: a made-up
/// capacity of 24 in the data layer once rendered a 40-slot server as "30/24 players".
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PavlovServerInfo {
    pub server_name: Option<String>,
    pub map_label: Option<String>,
    pub game_mode: Option<String>,
    pub max_players: Option<i32>,
}

/// Read a ServerInfo body. Pavlov nests the fields under `ServerInfo`; reading them top-level
/// showed *Unknown* for months. Both shapes are accepted.
#[must_use]
pub fn server_info(root: &Value) -> PavlovServerInfo {
    let scope = property(root, &["ServerInfo", "serverInfo"]).unwrap_or(root);
    PavlovServerInfo {
        server_name: text(scope, &["ServerName", "serverName"]),
        map_label: text(scope, &["MapLabel", "MapName", "mapLabel"]),
        game_mode: text(scope, &["GameMode", "gameMode"]),
        /* Capacity is NOT a field of its own in Pavlov's reply - it is the denominator of
        PlayerCount, a STRING like "2/24". MaxPlayers is still tried first, in case a build
        ever reports one. */
        max_players: int(scope, &["MaxPlayers", "maxPlayers"])
            .or_else(|| capacity(text(scope, &["PlayerCount", "playerCount"]).as_deref())),
    }
}

/// The capacity out of a "2/24" player count; `None` for a bare "2" or nothing, never a guess.
#[must_use]
pub fn capacity(player_count: Option<&str>) -> Option<i32> {
    let (_, max) = player_count?.split_once('/')?;
    max.trim().parse::<i32>().ok().filter(|m| *m > 0)
}
