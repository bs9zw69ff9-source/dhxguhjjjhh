//! The police records the bot stores (C# `Discord/Commands/PoliceCommands.cs` records and
//! `Storage/ArrestConverter.cs`).
//!
//! AN ARREST IS STORED IN THE NODE BOT'S SHAPE: `playerId`, `minutes`, `by`, `at` in epoch
//! milliseconds, and `charges` as an array of OBJECTS. The two bots disagreed about every field
//! name, and Node's charges are objects where the C# expected strings - so reading threw and the
//! most-wanted board saw an empty registry. Both spellings are READ; only Node's is WRITTEN, so a
//! rollback still ranks every player correctly.

use chrono::{DateTime, Utc};
use serde::ser::SerializeMap;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::{Map, Value};

use crate::json::{epoch_ms, parse_iso};

/// A standing warrant. Stored camelCase with the time in epoch milliseconds.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Warrant {
    #[serde(default)]
    pub player: String,
    /// Why it was issued. Shown to whoever runs the check.
    #[serde(default)]
    pub reason: String,
    #[serde(default)]
    pub issued_by: String,
    #[serde(with = "epoch_ms", default = "epoch")]
    pub at: DateTime<Utc>,
}

fn epoch() -> DateTime<Utc> {
    DateTime::UNIX_EPOCH
}

/// The bail multiplier, scaled by `/bail`, and the per-booking jail cap.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PoliceConfig {
    pub bail_rate: f64,
    /// The most jail one booking can carry however many charges are stacked. Zero means
    /// uncapped - the behaviour before the cap existed.
    pub max_jail_minutes: i32,
}

impl Default for PoliceConfig {
    fn default() -> Self {
        Self {
            bail_rate: 1.0,
            max_jail_minutes: 0,
        }
    }
}

/// One booking.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Arrest {
    pub player: String,
    /// The charges booked, so a sentence can be re-derived or appealed.
    pub codes: Vec<String>,
    pub jail_minutes: i32,
    pub bail: i32,
    pub arrested_by: String,
    pub at: DateTime<Utc>,
}

fn text<'a>(root: &'a Map<String, Value>, name: &str) -> Option<&'a str> {
    root.get(name).and_then(Value::as_str)
}

fn int(root: &Map<String, Value>, name: &str) -> Option<i32> {
    root.get(name)
        .and_then(Value::as_i64)
        .and_then(|n| i32::try_from(n).ok())
}

impl Arrest {
    /// Read either shape. A record with nothing recognisable is an EMPTY arrest rather than an
    /// error: one malformed entry must not empty the whole most-wanted board.
    #[must_use]
    pub fn from_value(value: &Value) -> Self {
        let Some(root) = value.as_object() else {
            return Self::empty();
        };
        let codes = ["codes", "charges"]
            .iter()
            .find_map(|name| root.get(*name).and_then(Value::as_array))
            .map(|entries| {
                entries
                    .iter()
                    .filter_map(|e| match e {
                        Value::String(s) => Some(s.as_str()),
                        // Node stores the whole charge; the code is the part that identifies it.
                        Value::Object(o) => text(o, "code"),
                        _ => None,
                    })
                    .filter(|c| !c.is_empty())
                    .map(str::to_owned)
                    .collect()
            })
            .unwrap_or_default();
        let at = match root.get("at") {
            // Node writes Date.now(); an earlier C# build wrote ISO.
            Some(Value::Number(n)) => n
                .as_i64()
                .or_else(|| n.as_f64().map(|f| f as i64))
                .and_then(DateTime::from_timestamp_millis),
            Some(Value::String(s)) => parse_iso(s.trim()),
            _ => None,
        }
        .unwrap_or(DateTime::UNIX_EPOCH);
        Self {
            player: text(root, "player")
                .or_else(|| text(root, "playerId"))
                .unwrap_or_default()
                .to_owned(),
            codes,
            jail_minutes: int(root, "jailMinutes")
                .or_else(|| int(root, "minutes"))
                .unwrap_or(0),
            bail: int(root, "bail").unwrap_or(0),
            arrested_by: text(root, "arrestedBy")
                .or_else(|| text(root, "by"))
                .unwrap_or_default()
                .to_owned(),
            at,
        }
    }

    fn empty() -> Self {
        Self {
            player: String::new(),
            codes: Vec::new(),
            jail_minutes: 0,
            bail: 0,
            arrested_by: String::new(),
            at: DateTime::UNIX_EPOCH,
        }
    }
}

impl<'de> Deserialize<'de> for Arrest {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        Ok(Self::from_value(&Value::deserialize(deserializer)?))
    }
}

impl Serialize for Arrest {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        // NODE'S FIELD NAMES, and charge OBJECTS carrying only the code: the rest is derivable
        // from the penal code, and a stale copy of a charge's bail is a second source of truth.
        let charges: Vec<Value> = self
            .codes
            .iter()
            .map(|c| serde_json::json!({ "code": c }))
            .collect();
        let mut map = serializer.serialize_map(Some(6))?;
        map.serialize_entry("playerId", &self.player)?;
        map.serialize_entry("minutes", &self.jail_minutes)?;
        map.serialize_entry("bail", &self.bail)?;
        map.serialize_entry("by", &self.arrested_by)?;
        map.serialize_entry("at", &self.at.timestamp_millis())?;
        map.serialize_entry("charges", &charges)?;
        map.end()
    }
}
