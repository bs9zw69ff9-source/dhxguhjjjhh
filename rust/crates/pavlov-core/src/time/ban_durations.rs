//! The `/tempban` duration picker (C# `BanDurations` in `Core/Time/EasternTime.cs`).
//!
//! The labels are COMPACT SPANS ("1d", "1mo"), never dates: a moderator picks a LENGTH, which is
//! the thing they actually have an opinion about; the expiry date is derived.

use chrono::TimeDelta;

use super::EasternTime;

/// Namespace for the picker, mirroring the C# static class.
#[derive(Debug)]
pub struct BanDurations;

impl BanDurations {
    /// The sentinel the picker uses for a ban that never expires.
    pub const PERMANENT: &'static str = "permanent";

    /// Every picker choice and its length, in display order.
    #[must_use]
    pub fn all() -> [(&'static str, TimeDelta); 11] {
        [
            ("1h", TimeDelta::hours(1)),
            ("6h", TimeDelta::hours(6)),
            ("1d", TimeDelta::days(1)),
            ("3d", TimeDelta::days(3)),
            ("5d", TimeDelta::days(5)),
            ("1w", TimeDelta::days(7)),
            ("2w", TimeDelta::days(14)),
            ("1mo", TimeDelta::days(30)),
            ("3mo", TimeDelta::days(90)),
            ("6mo", TimeDelta::days(180)),
            ("1y", TimeDelta::days(365)),
        ]
    }

    /// Resolve a picker value to a length. `None` means PERMANENT *or* unrecognised; the caller
    /// must tell those apart, which is why this never returns a zero span. Falls back to the
    /// free-text parser so a typed "3d 4h" works wherever a pick does.
    #[must_use]
    pub fn length(value: Option<&str>) -> Option<TimeDelta> {
        let value = value?;
        Self::all()
            .into_iter()
            .find(|(key, _)| key.eq_ignore_ascii_case(value))
            .map(|(_, length)| length)
            .or_else(|| EasternTime::parse_ban_span(Some(value)))
    }
}
