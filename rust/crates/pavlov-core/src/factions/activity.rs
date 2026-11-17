//! Who in a faction is active (C# `FactionActivity`), for `/factionstats`.

use chrono::{DateTime, TimeDelta, Utc};

use super::FactionDefinition;
use crate::text::case::eq_ignore_case;

/// One member's recorded activity.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MemberActivity {
    pub player: String,
    pub rank: String,
    pub minutes: i64,
    pub last_seen: Option<DateTime<Utc>>,
}

impl MemberActivity {
    /// Time since last seen, or `None` when the bot never saw them.
    #[must_use]
    pub fn idle(&self, now: DateTime<Utc>) -> Option<TimeDelta> {
        self.last_seen.map(|seen| now - seen)
    }
}

/// A member flagged as inactive, and why. `days` is `None` for "never seen".
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InactiveMember {
    pub member: MemberActivity,
    pub reason: String,
    pub days: Option<i64>,
}

/// The report.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FactionReport {
    pub faction: String,
    pub total: usize,
    pub active: usize,
    /// Highest rank first: "who runs this faction" is the question being asked.
    pub rank_counts: Vec<(String, usize)>,
    pub inactive: Vec<InactiveMember>,
    pub total_minutes: i64,
}

impl FactionReport {
    #[must_use]
    pub fn inactive30(&self) -> usize {
        self.inactive.len()
    }

    /// Percentage active, rounded HALF TO EVEN as .NET's `Math.Round` does by default.
    #[must_use]
    pub fn active_percent(&self) -> i64 {
        if self.total == 0 {
            return 0;
        }
        // Member counts are small; the conversions cannot lose anything that matters.
        #[allow(clippy::cast_precision_loss, clippy::cast_possible_truncation)]
        let percent = (100.0 * self.active as f64 / self.total as f64).round_ties_even() as i64;
        percent
    }
}

/// Namespace for the report, mirroring the C# static class.
#[derive(Debug)]
pub struct FactionActivity;

impl FactionActivity {
    /// Seen within this window counts as active.
    #[must_use]
    pub fn active_window() -> TimeDelta {
        TimeDelta::days(14)
    }

    /// Not seen for longer than this is inactive.
    #[must_use]
    pub fn inactive_after() -> TimeDelta {
        TimeDelta::days(30)
    }

    /// Fewer recorded minutes than this, while seen recently, is also inactive.
    pub const MINIMUM_MONTHLY_MINUTES: i64 = 60;

    /// Build the report.
    #[must_use]
    pub fn report(
        faction: &FactionDefinition,
        members: &[MemberActivity],
        now: DateTime<Utc>,
    ) -> FactionReport {
        let active = members
            .iter()
            .filter(|m| {
                m.idle(now)
                    .is_some_and(|idle| idle <= Self::active_window())
            })
            .count();
        let rank_counts = faction
            .order
            .iter()
            .rev()
            .map(|rank| {
                (
                    rank.clone(),
                    members
                        .iter()
                        .filter(|m| eq_ignore_case(&m.rank, rank))
                        .count(),
                )
            })
            .collect();
        let mut inactive: Vec<InactiveMember> = members
            .iter()
            .filter_map(|m| Self::assess(m, now))
            .collect();
        // Longest idle first, "never seen" at the top; stable, as LINQ's OrderByDescending.
        inactive.sort_by_key(|i| std::cmp::Reverse(i.days.unwrap_or(i64::MAX)));
        FactionReport {
            faction: faction.name.clone(),
            total: members.len(),
            active,
            rank_counts,
            inactive,
            total_minutes: members.iter().map(|m| m.minutes).sum(),
        }
    }

    /// Whether one member is inactive, and why.
    ///
    /// NEVER SEEN is reported, not counted as long idle: a roster predates the bot, and ranking
    /// those members first would put the founders at the top of a removal list.
    #[must_use]
    pub fn assess(member: &MemberActivity, now: DateTime<Utc>) -> Option<InactiveMember> {
        let Some(idle) = member.idle(now) else {
            return Some(InactiveMember {
                member: member.clone(),
                reason: "never seen by the bot - may predate tracking, or play under another name"
                    .to_owned(),
                days: None,
            });
        };
        let days = idle.num_days();
        if idle > Self::inactive_after() {
            return Some(InactiveMember {
                member: member.clone(),
                reason: format!("not seen for {days} days"),
                days: Some(days),
            });
        }
        if member.minutes < Self::MINIMUM_MONTHLY_MINUTES {
            return Some(InactiveMember {
                member: member.clone(),
                reason: format!("only {} minutes of recorded playtime", member.minutes),
                days: Some(days),
            });
        }
        None
    }
}
