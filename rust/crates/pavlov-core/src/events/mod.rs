//! The timeline: one normalised shape for everything that happens (C# `Core/Events`).
//!
//! APPEND-ONLY AND NEVER EDITED. A timeline whose past can be rewritten is not evidence, and
//! this is meant to be read during an argument about a ban. There is no update path, by design;
//! the only thing that removes a row is retention.

pub mod mapping;

pub use mapping::{category_of, is_automated, kind_of};

use chrono::{DateTime, Utc};

/// Which lens an event belongs to, so a timeline can be filtered without knowing every kind.
///
/// THE FILTER IS THE CATEGORY, NOT THE KIND. Kinds are added constantly as features land;
/// categories should almost never change. The discriminants are STORED (the `events.category`
/// column is this number), so they are fixed and a new category only ever goes on the end.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(i32)]
pub enum EventCategory {
    /// Server lifecycle: restarts, crashes, map changes, RCON state.
    Server = 0,
    /// Players coming and going.
    Player = 1,
    /// Anything a staff member did.
    Staff = 2,
    /// Detections: evasion, VPN, flags, auto-bans.
    Security = 3,
    /// Money moving.
    Economy = 4,
    /// Rosters, ranks, whitelists.
    Faction = 5,
}

impl EventCategory {
    pub const ALL: [Self; 6] = [
        Self::Server,
        Self::Player,
        Self::Staff,
        Self::Security,
        Self::Economy,
        Self::Faction,
    ];

    /// The stored number.
    #[must_use]
    pub const fn code(self) -> i32 {
        self as i32
    }

    /// The category a stored number names, `None` for one this build does not know.
    #[must_use]
    pub fn from_code(code: i32) -> Option<Self> {
        Self::ALL.into_iter().find(|c| c.code() == code)
    }

    /// The C# enum member name, which is also what `/timeline` shows.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::Server => "Server",
            Self::Player => "Player",
            Self::Staff => "Staff",
            Self::Security => "Security",
            Self::Economy => "Economy",
            Self::Faction => "Faction",
        }
    }

    /// The lower-case name, the prefix of every kind slug.
    #[must_use]
    pub const fn slug(self) -> &'static str {
        match self {
            Self::Server => "server",
            Self::Player => "player",
            Self::Staff => "staff",
            Self::Security => "security",
            Self::Economy => "economy",
            Self::Faction => "faction",
        }
    }
}

/// One thing that happened, normalised across every system that produces events.
///
/// DELIBERATELY FLAT AND DENORMALISED. Player and server are plain strings rather than
/// references, so an event still reads correctly after the player is renamed or the server is
/// removed from the config.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerEvent {
    /// When it happened. Indexed - every query is time-bounded.
    pub at: DateTime<Utc>,
    pub category: EventCategory,
    /// A short stable slug: `player.join`, `staff.ban`.
    pub kind: String,
    pub player: Option<String>,
    pub server: Option<String>,
    /// Who caused it: a staff name, "auto", or `None` when nobody did.
    pub actor: Option<String>,
    /// One human sentence. Shown verbatim in the timeline.
    pub detail: Option<String>,
}

impl ServerEvent {
    /// Longest a kind slug may be. Keeps the index small and the column predictable.
    pub const MAX_KIND: usize = 48;
    /// Longest a detail line may be, matching what a Discord field can show.
    pub const MAX_DETAIL: usize = 512;
    const MAX_PLAYER: usize = 128;
    const MAX_SERVER: usize = 64;
    const MAX_ACTOR: usize = 128;

    /// An event with only the required fields.
    #[must_use]
    pub fn new(at: DateTime<Utc>, category: EventCategory, kind: impl Into<String>) -> Self {
        Self {
            at,
            category,
            kind: kind.into(),
            player: None,
            server: None,
            actor: None,
            detail: None,
        }
    }

    /// The event as it will be stored: trimmed, bounded, and with empties normalised to `None`.
    ///
    /// NORMALISED ON THE WAY IN, so no query has to test for both empty and absent. TRUNCATION
    /// IS SILENT: losing a timeline entry because its detail was long is worse than losing the
    /// tail of a sentence.
    #[must_use]
    pub fn normalised(&self) -> Self {
        Self {
            at: self.at,
            category: self.category,
            kind: clamp(Some(&self.kind), Self::MAX_KIND).unwrap_or_else(|| "unknown".into()),
            player: clamp(self.player.as_deref(), Self::MAX_PLAYER),
            server: clamp(self.server.as_deref(), Self::MAX_SERVER),
            actor: clamp(self.actor.as_deref(), Self::MAX_ACTOR),
            detail: clamp(self.detail.as_deref(), Self::MAX_DETAIL),
        }
    }
}

/// Trimmed and cut to `max` characters; `None` when blank. Cut on a character boundary, so a
/// multi-byte character is never split (the C# counts UTF-16 units and can split a surrogate).
fn clamp(value: Option<&str>, max: usize) -> Option<String> {
    let trimmed = value?.trim();
    if trimmed.is_empty() {
        return None;
    }
    Some(trimmed.chars().take(max).collect())
}

/// What to select from the timeline.
///
/// EVERY FIELD IS OPTIONAL EXCEPT THE LIMIT, and the limit is not optional on purpose: an
/// unbounded query against a table that grows forever is the problem this store exists to avoid.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct EventQuery {
    /// Hard cap on rows.
    pub limit: i32,
    /// Only events at or after this instant.
    pub since: Option<DateTime<Utc>>,
    /// Only events strictly before this instant.
    pub until: Option<DateTime<Utc>>,
    pub category: Option<EventCategory>,
    /// Matched case-insensitively.
    pub player: Option<String>,
    pub server: Option<String>,
    /// Who caused it - the staff-activity view.
    pub actor: Option<String>,
}

impl EventQuery {
    /// Most rows any single query may return, whatever it asks for.
    pub const MAX_LIMIT: i32 = 500;

    /// A query for at most `limit` rows.
    #[must_use]
    pub fn new(limit: i32) -> Self {
        Self {
            limit,
            ..Self::default()
        }
    }

    /// The limit clamped into `1..=MAX_LIMIT`.
    #[must_use]
    pub fn effective_limit(&self) -> i32 {
        self.limit.clamp(1, Self::MAX_LIMIT)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_stored_codes_are_the_csharp_enum_order() {
        let codes: Vec<i32> = EventCategory::ALL.iter().map(|c| c.code()).collect();
        assert_eq!(codes, [0, 1, 2, 3, 4, 5]);
        assert_eq!(EventCategory::from_code(3), Some(EventCategory::Security));
        assert_eq!(EventCategory::from_code(6), None);
    }

    #[test]
    fn normalising_trims_bounds_and_nulls_empties() {
        let mut e = ServerEvent::new(DateTime::UNIX_EPOCH, EventCategory::Staff, "   ");
        e.player = Some("  Bob  ".into());
        e.server = Some(String::new());
        e.detail = Some("é".repeat(600));
        let n = e.normalised();
        assert_eq!(n.kind, "unknown");
        assert_eq!(n.player.as_deref(), Some("Bob"));
        assert_eq!(n.server, None);
        assert_eq!(n.detail.map(|d| d.chars().count()), Some(512));
    }

    #[test]
    fn the_limit_cannot_be_raised_or_zeroed() {
        assert_eq!(EventQuery::new(0).effective_limit(), 1);
        assert_eq!(EventQuery::new(10_000).effective_limit(), 500);
        assert_eq!(EventQuery::new(25).effective_limit(), 25);
    }
}
