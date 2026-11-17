//! Whether somebody's earnings look like an exploit (C# `Core/Economy/FraudSignals.cs`).
//!
//! IT FLAGS AND NEVER PUNISHES: there is no code path here that could. A big number is not
//! evidence: a good night, a payout owed for a week and a duplication bug all look the same in
//! a total. Against the player's OWN baseline first, because players earn at wildly different
//! rates; the server-wide check is the weaker second one, for a fresh alt with no history.

use chrono::TimeDelta;

use crate::intelligence::{RiskConfidence, RiskScorer};
use crate::text::number::{fixed, group_thousands};

/// What about the earnings looks wrong. The weight IS the variant.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FraudSignalKind {
    /// Far above what this player normally makes.
    FarAboveOwnBaseline = 60,
    /// Far above what anybody on the server normally makes.
    FarAboveServerNormal = 45,
    /// Credits arriving faster than play could produce them.
    ImpossibleFrequency = 40,
    /// The same amount over and over.
    RepeatedIdenticalAmounts = 30,
    /// One credit larger than everything else in the window combined.
    OversizedSingleCredit = 25,
}

impl FraudSignalKind {
    #[must_use]
    pub fn weight(self) -> i32 {
        self as i32
    }
}

/// One signal and the numbers behind it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FraudSignal {
    pub kind: FraudSignalKind,
    pub summary: String,
}

/// The verdict.
#[derive(Debug, Clone, PartialEq)]
pub struct FraudVerdict {
    pub player: String,
    pub earned: i64,
    pub baseline: i64,
    /// Strongest first.
    pub signals: Vec<FraudSignal>,
    pub assessment: String,
    pub confidence: RiskConfidence,
}

impl FraudVerdict {
    #[must_use]
    pub fn suspicious(&self) -> bool {
        !self.signals.is_empty()
    }

    /// How many times their own baseline this is, or `None` with no baseline.
    #[must_use]
    pub fn multiple(&self) -> Option<f64> {
        // Balances are far inside f64's exact-integer range.
        #[allow(clippy::cast_precision_loss)]
        (self.baseline > 0).then(|| self.earned as f64 / self.baseline as f64)
    }
}

/// Namespace for the assessment, mirroring the C# static class.
#[derive(Debug)]
pub struct FraudSignals;

impl FraudSignals {
    pub const OWN_BASELINE_MULTIPLE: f64 = 8.0;
    pub const SERVER_NORMAL_MULTIPLE: f64 = 6.0;
    /// Pavlov pays for kills and objectives; a credit every few seconds sustained over a
    /// window is a script or a bug.
    pub const IMPOSSIBLE_CREDITS_PER_MINUTE: f64 = 4.0;
    pub const IDENTICAL_RUN_LENGTH: usize = 5;
    /// A FLOOR: below this nothing is flagged, whatever the ratios say, or the quietest players
    /// would be flagged constantly and staff would learn to ignore the alerts.
    pub const MINIMUM_TO_FLAG: i64 = 2500;

    /// Assess one player's credits over `window` against their own and the server's baseline.
    #[must_use]
    // Money and counts stay far inside f64's exact-integer range.
    #[allow(clippy::cast_precision_loss)]
    pub fn assess(
        player: &str,
        credits: &[i64],
        own_baseline: i64,
        server_normal: i64,
        window: TimeDelta,
    ) -> FraudVerdict {
        let earned: i64 = credits.iter().fold(0_i64, |sum, c| sum.saturating_add(*c));
        if earned < Self::MINIMUM_TO_FLAG {
            return FraudVerdict {
                player: player.to_owned(),
                earned,
                baseline: own_baseline,
                signals: Vec::new(),
                assessment: "Nothing unusual.".to_owned(),
                confidence: RiskConfidence::Low,
            };
        }

        let mut signals = Vec::new();
        if own_baseline > 0 && earned as f64 >= own_baseline as f64 * Self::OWN_BASELINE_MULTIPLE {
            signals.push(FraudSignal {
                kind: FraudSignalKind::FarAboveOwnBaseline,
                summary: format!(
                    "Earned {} against their usual {} for this length of time - about {}x.",
                    group_thousands(earned),
                    group_thousands(own_baseline),
                    fixed(earned as f64 / own_baseline as f64, 0)
                ),
            });
        }
        if server_normal > 0 && earned as f64 >= server_normal as f64 * Self::SERVER_NORMAL_MULTIPLE
        {
            signals.push(FraudSignal {
                kind: FraudSignalKind::FarAboveServerNormal,
                summary: format!(
                    "Earned {} against a server-typical {} over the same span.",
                    group_thousands(earned),
                    group_thousands(server_normal)
                ),
            });
        }
        let minutes = window.num_milliseconds() as f64 / 60_000.0;
        if window > TimeDelta::zero()
            && credits.len() as f64 / minutes >= Self::IMPOSSIBLE_CREDITS_PER_MINUTE
        {
            signals.push(FraudSignal {
                kind: FraudSignalKind::ImpossibleFrequency,
                summary: format!(
                    "{} separate credits in {} minutes - {} per minute.",
                    credits.len(),
                    fixed(minutes, 0),
                    fixed(credits.len() as f64 / minutes, 1)
                ),
            });
        }
        let run = Self::longest_identical_run(credits);
        if run >= Self::IDENTICAL_RUN_LENGTH {
            signals.push(FraudSignal {
                kind: FraudSignalKind::RepeatedIdenticalAmounts,
                summary: format!(
                    "{run} identical credits in a row, which ordinary play does not produce."
                ),
            });
        }
        // AGAINST THE REST OF THEIR OWN WINDOW, not a constant tuned on one server's economy.
        if credits.len() > 1 {
            let largest = credits.iter().copied().max().unwrap_or(0);
            let rest = earned - largest;
            if rest > 0 && largest > rest {
                signals.push(FraudSignal {
                    kind: FraudSignalKind::OversizedSingleCredit,
                    summary: format!(
                        "One credit of {} outweighs everything else in the window combined ({}).",
                        group_thousands(largest),
                        group_thousands(rest)
                    ),
                });
            }
        }

        signals.sort_by_key(|s| std::cmp::Reverse(s.kind.weight()));
        let assessment = Self::describe(&signals);
        // The same confidence the risk engine uses, so staff learn to read it once.
        let confidence = RiskScorer::confidence_of(signals.iter().map(|s| s.kind.weight()));
        FraudVerdict {
            player: player.to_owned(),
            earned,
            baseline: own_baseline,
            signals,
            assessment,
            confidence,
        }
    }

    /// The longest run of one amount repeated consecutively.
    #[must_use]
    pub fn longest_identical_run(credits: &[i64]) -> usize {
        let mut longest = usize::from(!credits.is_empty());
        let mut current = longest;
        for pair in credits.windows(2) {
            current = if pair[1] == pair[0] { current + 1 } else { 1 };
            longest = longest.max(current);
        }
        longest
    }

    fn describe(signals: &[FraudSignal]) -> String {
        let Some(top) = signals.first() else {
            return "Nothing unusual.".to_owned();
        };
        match top.kind {
            FraudSignalKind::ImpossibleFrequency | FraudSignalKind::RepeatedIdenticalAmounts => {
                "The SHAPE of these credits does not look like play. Worth reading the transactions before concluding anything - a payout that had been owed for a while arrives in one burst too."
            }
            FraudSignalKind::FarAboveOwnBaseline | FraudSignalKind::FarAboveServerNormal => {
                "Well outside what this player normally earns. A good night looks like this as often as an exploit does, so this is a reason to look rather than a finding."
            }
            FraudSignalKind::OversizedSingleCredit => "One credit dominates the window. Check where it came from.",
        }
        .to_owned()
    }
}
