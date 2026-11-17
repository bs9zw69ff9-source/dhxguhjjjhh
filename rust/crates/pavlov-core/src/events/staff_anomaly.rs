//! A burst of staff actions against that staff member's own rate (C# `StaffAnomaly`).
//!
//! A steady worker is never flagged however much they do: a high total is somebody doing the
//! job. Only a SHARP BURST against their own baseline is unusual.

use chrono::TimeDelta;

use crate::text::number::fixed;

/// The verdict and its one-sentence explanation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StaffAnomalyVerdict {
    pub unusual: bool,
    pub explanation: String,
}

impl StaffAnomalyVerdict {
    /// Nothing unusual.
    #[must_use]
    pub fn normal() -> Self {
        Self {
            unusual: false,
            explanation: "Nothing unusual.".to_owned(),
        }
    }
}

/// Namespace for the check, mirroring the C# static class.
#[derive(Debug)]
pub struct StaffAnomaly;

impl StaffAnomaly {
    /// A busiest hour below this is never a burst.
    pub const MINIMUM_BURST: i64 = 10;
    /// The busiest hour must be at least this many times the average.
    pub const BURST_MULTIPLE: f64 = 5.0;

    /// Check the busiest hour against the average over `window`.
    ///
    /// Over a one-hour window the peak IS the average, so nothing is flagged: a burst means
    /// something only against a baseline, and a window that short has none.
    #[must_use]
    pub fn check(peak_hour: i64, total: i64, window: TimeDelta) -> StaffAnomalyVerdict {
        if peak_hour < Self::MINIMUM_BURST || window <= TimeDelta::zero() {
            return StaffAnomalyVerdict::normal();
        }
        // Counts and hours are far below f64's exact-integer range.
        #[allow(clippy::cast_precision_loss)]
        let (hours, total, peak) = (
            (window.num_milliseconds() as f64 / 3_600_000.0).max(1.0),
            total as f64,
            peak_hour as f64,
        );
        let average = total / hours;
        let ratio = peak / average;
        if ratio < Self::BURST_MULTIPLE {
            return StaffAnomalyVerdict::normal();
        }
        StaffAnomalyVerdict {
            unusual: true,
            explanation: format!(
                "{peak_hour} actions in one hour against an average of {} per hour over the window - about {}x their own usual rate.",
                fixed(average, 1),
                fixed(ratio, 0)
            ),
        }
    }
}
