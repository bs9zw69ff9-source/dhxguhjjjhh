//! Refuse a roster write that would delete more than a handful of entries
//! (C# `Core/Data/RosterWriteGuard.cs`).
//!
//! THE CHECK IS ON THE SIZE OF THE CHANGE, not the shape of the data. An empty roster is
//! perfectly VALID to Pavlov, so a parsing bug producing an empty list would strip a faction
//! mid-round with no error anywhere; a write removing twenty entries is not a big operation, it
//! is a bug that already happened. "Cannot read the current file" is its own refusal, because
//! treating it as empty turns a transient permission error into a wipe.

use std::collections::HashSet;

use crate::text::case::fold_case;

/// The decision.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RosterWriteDecision {
    Allow,
    RefusedBulkDrop,
    RefusedUnreadable,
    RefusedInvalidPayload,
}

/// The verdict, with what it counted.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RosterWriteVerdict {
    pub decision: RosterWriteDecision,
    pub dropped: usize,
    pub existing: usize,
    pub explanation: Option<String>,
}

impl RosterWriteVerdict {
    #[must_use]
    pub fn is_allowed(&self) -> bool {
        self.decision == RosterWriteDecision::Allow
    }

    fn allow(dropped: usize, existing: usize) -> Self {
        Self {
            decision: RosterWriteDecision::Allow,
            dropped,
            existing,
            explanation: None,
        }
    }
}

/// Namespace for the guard, mirroring the C# static class.
#[derive(Debug)]
pub struct RosterWriteGuard;

impl RosterWriteGuard {
    /// More removals than this in one write are refused.
    pub const BULK_DROP_LIMIT: usize = 5;

    /// Judge replacing `current` (`None`: could not be read) with `proposed` (`None`: the
    /// payload was not a list of lines). `allow_bulk` is the deliberate wipe.
    #[must_use]
    pub fn evaluate(
        current: Option<&[String]>,
        proposed: Option<&[String]>,
        file_exists: bool,
        allow_bulk: bool,
    ) -> RosterWriteVerdict {
        let Some(proposed) = proposed else {
            return RosterWriteVerdict {
                decision: RosterWriteDecision::RefusedInvalidPayload,
                dropped: 0,
                existing: 0,
                explanation: Some("payload is not a list of lines".to_owned()),
            };
        };
        if allow_bulk {
            return RosterWriteVerdict::allow(0, current.map_or(0, <[String]>::len));
        }
        // A file that does not exist yet has nothing to lose.
        if !file_exists {
            return RosterWriteVerdict::allow(0, 0);
        }
        let Some(current) = current else {
            return RosterWriteVerdict {
                decision: RosterWriteDecision::RefusedUnreadable,
                dropped: 0,
                existing: 0,
                explanation: Some(
                    "cannot read current contents - aborting to protect the roster".to_owned(),
                ),
            };
        };
        if current.is_empty() {
            return RosterWriteVerdict::allow(0, 0);
        }
        let keep: HashSet<String> = proposed.iter().map(|l| fold_case(l)).collect();
        let dropped = current
            .iter()
            .filter(|line| !keep.contains(&fold_case(line)))
            .count();
        if dropped > Self::BULK_DROP_LIMIT {
            return RosterWriteVerdict {
                decision: RosterWriteDecision::RefusedBulkDrop,
                dropped,
                existing: current.len(),
                explanation: Some(format!(
                    "would remove {dropped} of {} entries (limit {}) - suspected corruption, roster left untouched",
                    current.len(),
                    Self::BULK_DROP_LIMIT
                )),
            };
        }
        RosterWriteVerdict::allow(dropped, current.len())
    }

    /// A roster file's lines: split on `\n`, `\r`/space/tab trimmed, blanks dropped.
    #[must_use]
    pub fn split_lines(raw: Option<&str>) -> Vec<String> {
        raw.map(|r| {
            r.split('\n')
                .map(|l| l.trim_matches(['\r', ' ', '\t']))
                .filter(|l| !l.is_empty())
                .map(str::to_owned)
                .collect()
        })
        .unwrap_or_default()
    }
}
