//! A player's save as the game mod writes it: `<ModSave>/<playername>.json` (owner change,
//! 2026-11-18, replacing the `Money` / `DriversLiscense` text files).
//!
//! ```json
//! { "money": 153, "drivers_license": false, "firearm_license": true,
//!   "look": { ... }, "face": { ... }, "skin": 3, "name": "Rickythegamer1001",
//!   "pf_version": 1, "updated": "2026-11-18 15:44" }
//! ```
//!
//! - THE BOT OWNS FOUR FIELDS AND STAMPS A FIFTH: `money`, `drivers_license`,
//!   `firearm_license`, the `ban` block, and `updated` on every edit. Everything else (`look`, `face`, `skin`,
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

/// The ban block (owner, 2026-11-19: bans live in the save, not in RCON or blacklist.txt):
/// `"ban": { "banned": true, "reason": "...", "unban_date": "11/20/26" }`.
pub const BAN: &str = "ban";
const BANNED: &str = "banned";
const BAN_REASON: &str = "reason";
const UNBAN_DATE: &str = "unban_date";
/// How `unban_date` is written: the mod's format, a local date with no time. Blank is
/// permanent.
pub const UNBAN_DATE_FORMAT: &str = "%m/%d/%y";

/// The day an `unban_date` names, or `None` when it is blank (permanent) or not a date the mod
/// writes. The ban ends when that day begins, server local time: the date is when it is lifted,
/// and lifting is the bot's job (the mod does not lift on its own).
#[must_use]
pub fn parse_unban_date(text: &str) -> Option<chrono::NaiveDate> {
    chrono::NaiveDate::parse_from_str(text.trim(), UNBAN_DATE_FORMAT).ok()
}

/// A ban as the save holds it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SaveBan {
    pub reason: String,
    /// [`UNBAN_DATE_FORMAT`], or empty for a permanent ban.
    pub unban_date: String,
}

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

    /// The ban the save holds, or `None` when it holds none (no block, or `banned` not true).
    #[must_use]
    pub fn ban(&self) -> Option<SaveBan> {
        let block = self.fields.get(BAN)?.as_object()?;
        if block.get(BANNED).and_then(Value::as_bool) != Some(true) {
            return None;
        }
        let text = |key: &str| {
            block
                .get(key)
                .and_then(Value::as_str)
                .unwrap_or_default()
                .to_owned()
        };
        Some(SaveBan {
            reason: text(BAN_REASON),
            unban_date: text(UNBAN_DATE),
        })
    }

    /// Whether the save's ban block is already exactly the lifted shape the bot writes.
    fn ban_is_clear(&self) -> bool {
        self.fields
            .get(BAN)
            .and_then(Value::as_object)
            .is_some_and(|block| {
                block.get(BANNED) == Some(&Value::Bool(false))
                    && block.get(BAN_REASON) == Some(&Value::String(String::new()))
                    && block.get(UNBAN_DATE) == Some(&Value::String(String::new()))
            })
    }

    /// This save with `ban` set (`Some`) or lifted (`None`: `banned` false, the reason and date
    /// blanked). `None` when that is what the save already says, so an unchanged file is not
    /// rewritten. Any other key the mod keeps inside the block is left as it was.
    #[must_use]
    pub fn with_ban(&self, ban: Option<&SaveBan>) -> Option<Self> {
        match ban {
            Some(wanted) if self.ban().as_ref() == Some(wanted) => return None,
            None if self.ban_is_clear() => return None,
            _ => {}
        }
        let mut edited = self.clone();
        let mut block = match self.fields.get(BAN) {
            Some(Value::Object(existing)) => existing.clone(),
            _ => Map::new(),
        };
        let (banned, reason, date) = match ban {
            Some(b) => (true, b.reason.clone(), b.unban_date.clone()),
            None => (false, String::new(), String::new()),
        };
        block.insert(BANNED.to_owned(), Value::Bool(banned));
        block.insert(BAN_REASON.to_owned(), Value::String(reason));
        block.insert(UNBAN_DATE.to_owned(), Value::String(date));
        edited.fields.insert(BAN.to_owned(), Value::Object(block));
        Some(edited)
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
