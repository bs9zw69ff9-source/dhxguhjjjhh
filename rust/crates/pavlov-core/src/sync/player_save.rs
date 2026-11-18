//! A player's save as the game mod writes it: `<ModSave>/<playername>.json` (owner change,
//! 2026-11-18, replacing the `Money` / `DriversLiscense` text files).
//!
//! ```json
//! { "money": 153, "drivers_license": false, "firearm_license": true,
//!   "look": { ... }, "face": { ... }, "skin": 3, "name": "Rickythegamer1001",
//!   "pf_version": 1, "updated": "2026-11-18 15:44" }
//! ```
//!
//! - THE BOT OWNS THREE FIELDS AND STAMPS A FOURTH: `money`, `drivers_license`,
//!   `firearm_license`, and `updated` on every edit. Everything else (`look`, `face`, `skin`,
//!   `name`, `pf_version`, and whatever a later version of the mod adds) is kept exactly as
//!   read, in the order read.
//! - A FILE WITHOUT A WHOLE-NUMBER `money` IS NOT A SAVE. It is refused, never rewritten: the
//!   folder can hold other mods' JSON, and "rewrite what we half understand" is how a balance
//!   gets wiped.
//! - Written with TAB indentation, the layout the mod itself writes, so a diff of a bot edit
//!   shows only the values that changed.

use serde::Serialize;
use serde_json::{Map, Value};

/// The field holding the balance.
pub const MONEY: &str = "money";
/// The field the bot stamps on every edit.
pub const UPDATED: &str = "updated";
/// How `updated` is written (the mod's own format, local time, minutes).
pub const UPDATED_FORMAT: &str = "%Y-%m-%d %H:%M";

/// One player's save.
#[derive(Debug, Clone, PartialEq)]
pub struct PlayerSave {
    fields: Map<String, Value>,
}

impl PlayerSave {
    /// Parse a save. `None` for anything that is not a JSON object with a whole-number `money`.
    #[must_use]
    pub fn parse(content: Option<&str>) -> Option<Self> {
        let Value::Object(fields) = serde_json::from_str::<Value>(content?).ok()? else {
            return None;
        };
        fields.get(MONEY)?.as_i64()?;
        Some(Self { fields })
    }

    /// The balance.
    #[must_use]
    pub fn money(&self) -> Option<i64> {
        self.fields.get(MONEY).and_then(Value::as_i64)
    }

    /// A licence: `Some(held)` when the file has the field as a boolean, `None` when it does
    /// not have it at all.
    #[must_use]
    pub fn license(&self, key: &str) -> Option<bool> {
        self.fields.get(key).and_then(Value::as_bool)
    }

    /// The in-game name the mod recorded, when it did.
    #[must_use]
    pub fn name(&self) -> Option<&str> {
        self.fields.get("name").and_then(Value::as_str)
    }

    /// This save with another balance.
    #[must_use]
    pub fn with_money(&self, balance: i64) -> Self {
        let mut edited = self.clone();
        edited.fields.insert(MONEY.to_owned(), Value::from(balance));
        edited
    }

    /// This save with a licence set. Writes the field even when the file did not have it: the
    /// mod defaults a missing licence to not held, so adding it is a give, never a corruption.
    #[must_use]
    pub fn with_license(&self, key: &str, held: bool) -> Self {
        let mut edited = self.clone();
        edited.fields.insert(key.to_owned(), Value::Bool(held));
        edited
    }

    /// This save with `updated` set to `stamp` (already formatted with [`UPDATED_FORMAT`]).
    #[must_use]
    pub fn touched(&self, stamp: &str) -> Self {
        let mut edited = self.clone();
        edited
            .fields
            .insert(UPDATED.to_owned(), Value::String(stamp.to_owned()));
        edited
    }

    /// The file's text: tab-indented JSON with a trailing newline.
    #[must_use]
    pub fn render(&self) -> String {
        let mut out = Vec::new();
        let formatter = serde_json::ser::PrettyFormatter::with_indent(b"\t");
        let mut serializer = serde_json::Serializer::with_formatter(&mut out, formatter);
        // A Map of plain JSON values always serialises.
        if self.fields.serialize(&mut serializer).is_err() {
            return String::new();
        }
        let mut text = String::from_utf8(out).unwrap_or_default();
        text.push('\n');
        text
    }
}
