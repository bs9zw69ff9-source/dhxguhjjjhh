//! Suggestions for the player-name and rank fields (C# `Discord/Autocomplete.cs`).
//!
//! - THE ORDER IS THE FEATURE: online players, then recently seen, then everyone recorded.
//!   The name a moderator wants is nearly always somebody on the server now.
//! - Prefix matches before substring ones: "al" offers "Alice" before "Marshal".
//! - Nothing matched offers what was typed, tagged "manual entry": the bot cannot know every
//!   name, and refusing one would make the command unusable exactly when it is needed.
//! - Reads only memory and one dataset, never RCON: Discord gives about three seconds.

use std::sync::Arc;

use chrono::{DateTime, Utc};
use pavlov_core::data::SerializedStore;
use pavlov_core::json::timestamp_from_value;
use pavlov_core::text::NameLabels;
use pavlov_core::text::case::{IgnoreCaseMap, IgnoreCaseSet};
use pavlov_host::factions::RosterService;
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::registry::RconRegistry;
use pavlov_storage::datasets::LAST_SEEN;
use serde_json::Value;

use crate::command::Choice;

/// Discord's cap on choices.
pub const MAX_CHOICES: usize = 25;
/// Discord's cap on a choice label.
const MAX_LABEL: usize = 100;

/// Where a suggested name came from, best first.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
enum Source {
    Online,
    Recent,
    Known,
}

impl Source {
    fn tag(self) -> Option<&'static str> {
        match self {
            Self::Online => Some("online"),
            Self::Recent => Some("recent"),
            Self::Known => None,
        }
    }
}

/// The suggester.
pub struct PlayerAutocomplete {
    rcon: Arc<RconRegistry>,
    tracking: Arc<IpTrackingService>,
    store: Arc<SerializedStore>,
    rosters: Arc<RosterService>,
}

impl std::fmt::Debug for PlayerAutocomplete {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PlayerAutocomplete").finish_non_exhaustive()
    }
}

fn label(name: &str, source: Source) -> String {
    NameLabels::decorate(name, source.tag())
        .chars()
        .take(MAX_LABEL)
        .collect()
}

/// Order and filter the candidates: the pure half of [`PlayerAutocomplete::suggest`].
#[must_use]
pub fn rank_names(
    online: &[String],
    recent: &[String],
    known: &[String],
    typed: &str,
) -> Vec<Choice> {
    let query = typed.trim();
    let mut seen = IgnoreCaseSet::new();
    let mut ordered: Vec<(&str, Source)> = online
        .iter()
        .map(|n| (n.as_str(), Source::Online))
        .chain(recent.iter().map(|n| (n.as_str(), Source::Recent)))
        .chain(known.iter().map(|n| (n.as_str(), Source::Known)))
        .filter(|(n, _)| seen.insert(*n))
        .collect();
    if !query.is_empty() {
        let folded = query.to_lowercase();
        ordered.retain(|(n, _)| n.to_lowercase().contains(&folded));
        // Stable: within a band the online/recent/known order holds.
        ordered.sort_by_key(|(n, source)| (!n.to_lowercase().starts_with(&folded), *source));
    }
    let mut choices: Vec<Choice> = ordered
        .into_iter()
        .take(MAX_CHOICES)
        .map(|(n, source)| Choice::string(label(n, source), n))
        .collect();
    if choices.is_empty() && !query.is_empty() {
        choices.push(Choice::string(
            NameLabels::decorate(query, Some("manual entry")),
            query,
        ));
    }
    choices
}

impl PlayerAutocomplete {
    #[must_use]
    pub fn new(
        rcon: Arc<RconRegistry>,
        tracking: Arc<IpTrackingService>,
        store: Arc<SerializedStore>,
        rosters: Arc<RosterService>,
    ) -> Self {
        Self {
            rcon,
            tracking,
            store,
            rosters,
        }
    }

    /// Player names for what has been typed so far (empty is normal).
    #[must_use]
    pub fn suggest(&self, typed: &str) -> Vec<Choice> {
        let online = self.rcon.all_online_players();
        let stored: IgnoreCaseMap<Value> = self
            .store
            .read(LAST_SEEN, IgnoreCaseMap::new())
            .unwrap_or_default();
        let mut recent: Vec<(String, DateTime<Utc>)> = stored
            .iter()
            .map(|(name, at)| {
                (
                    name.to_owned(),
                    timestamp_from_value(at).unwrap_or(DateTime::UNIX_EPOCH),
                )
            })
            .collect();
        recent.sort_by_key(|(_, at)| std::cmp::Reverse(*at));
        let recent: Vec<String> = recent.into_iter().map(|(n, _)| n).collect();
        rank_names(&online, &recent, &self.tracking.known_names(), typed)
    }

    /// The ranks of the faction picked BESIDE the field, highest first. An unknown faction
    /// offers nothing: the command would refuse any rank it was handed anyway.
    #[must_use]
    pub fn suggest_ranks(&self, faction: Option<&str>, typed: &str) -> Vec<Choice> {
        let Some(definition) = self.rosters.factions().get(faction.map(str::trim)) else {
            return Vec::new();
        };
        let query = typed.trim().to_lowercase();
        definition
            .order
            .iter()
            .rev()
            .filter(|r| query.is_empty() || r.to_lowercase().contains(&query))
            .take(MAX_CHOICES)
            .map(|r| Choice::string(r.clone(), r.clone()))
            .collect()
    }

    /// Dispatched on the FOCUSED option: `rank` gets ranks, every other field player names.
    #[must_use]
    pub fn for_focused(&self, focused: &str, typed: &str, faction: Option<&str>) -> Vec<Choice> {
        if focused == "rank" {
            self.suggest_ranks(faction, typed)
        } else {
            self.suggest(typed)
        }
    }
}
