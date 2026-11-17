//! One faction per player, one rank, at most one sub-class (C# `MembershipRules`).
//!
//! These exist because the STORAGE CANNOT EXPRESS THEM: rosters are plain text the game reads
//! live, and nothing stops a name appearing in six files at once. Current membership is passed
//! in, so every rule tests without a filesystem.

use super::FactionDefinition;
use crate::text::case::eq_ignore_case;

/// What a membership change would do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MembershipOutcome {
    Allowed,
    AlreadyLowest,
    AlreadyHighest,
    NotWhitelisted,
    AlreadyInAnotherFaction,
    NoSuchSubclass,
    NoSuchRank,
    AlreadyHasSubclass,
    NoChange,
    UnknownFaction,
    RosterUnavailable,
    WriteFailed,
}

/// The decision, with the rank it lands on or the conflicting faction or sub-class.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MembershipDecision {
    pub outcome: MembershipOutcome,
    pub rank: Option<String>,
    pub conflict: Option<String>,
}

impl MembershipDecision {
    fn of(outcome: MembershipOutcome) -> Self {
        Self {
            outcome,
            rank: None,
            conflict: None,
        }
    }

    fn with_rank(outcome: MembershipOutcome, rank: &str) -> Self {
        Self {
            outcome,
            rank: Some(rank.to_owned()),
            conflict: None,
        }
    }

    fn with_conflict(outcome: MembershipOutcome, conflict: &str) -> Self {
        Self {
            outcome,
            rank: None,
            conflict: Some(conflict.to_owned()),
        }
    }

    /// Allowed, landing on `rank` (or sub-class).
    #[must_use]
    pub fn allow(rank: Option<&str>) -> Self {
        Self {
            outcome: MembershipOutcome::Allowed,
            rank: rank.map(str::to_owned),
            conflict: None,
        }
    }

    /// Whether the change may go ahead.
    #[must_use]
    pub fn is_allowed(&self) -> bool {
        self.outcome == MembershipOutcome::Allowed
    }
}

/// Namespace for the rules, mirroring the C# static class.
#[derive(Debug)]
pub struct MembershipRules;

impl MembershipRules {
    /// One step up (`direction = 1`) or down (`-1`). An unrecognised current rank counts as the
    /// lowest, so a promotion can fix a member stranded by a renamed rank. Ranks are uncapped.
    #[must_use]
    pub fn change_rank(
        faction: Option<&FactionDefinition>,
        current_rank: Option<&str>,
        direction: i32,
    ) -> MembershipDecision {
        let Some(faction) = faction else {
            return MembershipDecision::of(MembershipOutcome::UnknownFaction);
        };
        let index = i64::try_from(faction.index_of(current_rank).unwrap_or(0)).unwrap_or(0);
        let next = index + i64::from(direction);
        if next < 0 {
            return MembershipDecision::with_rank(
                MembershipOutcome::AlreadyLowest,
                faction.lowest(),
            );
        }
        match usize::try_from(next)
            .ok()
            .and_then(|n| faction.order.get(n))
        {
            Some(rank) => MembershipDecision::allow(Some(rank)),
            None => {
                MembershipDecision::with_rank(MembershipOutcome::AlreadyHighest, faction.highest())
            }
        }
    }

    /// Straight to `target_rank`, RESOLVED TO THE FACTION'S OWN SPELLING (the rank files are
    /// keyed on it). Already there is `NoChange`, not a failure, and nothing is written.
    #[must_use]
    pub fn set_rank(
        faction: Option<&FactionDefinition>,
        current_rank: Option<&str>,
        target_rank: Option<&str>,
    ) -> MembershipDecision {
        let Some(faction) = faction else {
            return MembershipDecision::of(MembershipOutcome::UnknownFaction);
        };
        let Some(target) = target_rank.map(str::trim).filter(|t| !t.is_empty()) else {
            return MembershipDecision::of(MembershipOutcome::NoSuchRank);
        };
        let Some(resolved) = faction.order.iter().find(|r| eq_ignore_case(r, target)) else {
            return MembershipDecision::of(MembershipOutcome::NoSuchRank);
        };
        if current_rank.is_some_and(|c| eq_ignore_case(c, resolved)) {
            return MembershipDecision::with_rank(MembershipOutcome::NoChange, resolved);
        }
        MembershipDecision::allow(Some(resolved))
    }

    /// Join at the default rank. One faction per player: factions are mutually exclusive.
    #[must_use]
    pub fn join(
        faction: Option<&FactionDefinition>,
        current_factions: &[String],
    ) -> MembershipDecision {
        let Some(faction) = faction else {
            return MembershipDecision::of(MembershipOutcome::UnknownFaction);
        };
        if let Some(existing) = current_factions
            .iter()
            .find(|f| !eq_ignore_case(f, &faction.name))
        {
            return MembershipDecision::with_conflict(
                MembershipOutcome::AlreadyInAnotherFaction,
                existing,
            );
        }
        if current_factions
            .iter()
            .any(|f| eq_ignore_case(f, &faction.name))
        {
            return MembershipDecision::with_conflict(MembershipOutcome::NoChange, &faction.name);
        }
        MembershipDecision::allow(Some(&faction.default))
    }

    /// Add or remove a sub-class. A member may hold at most ONE alongside their rank.
    #[must_use]
    pub fn change_subclass(
        faction: Option<&FactionDefinition>,
        subclass: &str,
        held: &[String],
        removing: bool,
    ) -> MembershipDecision {
        let Some(faction) = faction else {
            return MembershipDecision::of(MembershipOutcome::UnknownFaction);
        };
        if !faction.has_subclass(subclass) {
            return MembershipDecision::with_conflict(MembershipOutcome::NoSuchSubclass, subclass);
        }
        let has = held.iter().any(|s| eq_ignore_case(s, subclass));
        if removing {
            return if has {
                MembershipDecision::allow(Some(subclass))
            } else {
                MembershipDecision::with_conflict(MembershipOutcome::NoChange, subclass)
            };
        }
        if has {
            return MembershipDecision::with_conflict(MembershipOutcome::NoChange, subclass);
        }
        match held.iter().find(|s| !eq_ignore_case(s, subclass)) {
            Some(other) => {
                MembershipDecision::with_conflict(MembershipOutcome::AlreadyHasSubclass, other)
            }
            None => MembershipDecision::allow(Some(subclass)),
        }
    }
}
