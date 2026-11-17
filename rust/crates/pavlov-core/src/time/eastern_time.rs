//! Server-local time, DST-aware (C# `Core/Time/EasternTime.cs`).
//!
//! America/New_York, so it reads EST in winter and EDT in summer. Printing UTC is what made the
//! connection feed look four to five hours off. Discord's own `<t:..:R>` markers are left to
//! localise per viewer elsewhere.
//!
//! The zone comes from the tz database compiled into `chrono-tz`, not the host's tzdata. The C#
//! threw at first use when the host lacked tzdata; here there is nothing to be missing.

use chrono::{DateTime, TimeDelta, Utc};
use chrono_tz::America::New_York;

use crate::patterns::static_regex;

/// The current time, injected so every rule that depends on it is testable.
pub trait Clock: Send + Sync {
    /// Now, in UTC.
    fn now(&self) -> DateTime<Utc>;
}

/// The real clock.
#[derive(Debug, Clone, Copy, Default)]
pub struct SystemClock;

impl Clock for SystemClock {
    fn now(&self) -> DateTime<Utc> {
        Utc::now()
    }
}

/// .NET's 100-nanosecond tick, the unit the C# overflow arithmetic was written in. Keeping it
/// keeps the ceilings and the rounding identical.
const TICKS_PER_SECOND: i64 = 10_000_000;
const TICKS_PER_MINUTE: i64 = TICKS_PER_SECOND * 60;
const TICKS_PER_HOUR: i64 = TICKS_PER_MINUTE * 60;
const TICKS_PER_DAY: i64 = TICKS_PER_HOUR * 24;
const NANOS_PER_TICK: i64 = 100;

/// The longest span either parser produces: a hundred years. Longer input is UNPARSEABLE, not
/// clamped, because the callers already handle "cannot read this" (the ban importer records it
/// as permanent and keeps the raw text) and a silent hundred-year ban would look deliberate.
pub const MAX_SPAN_TICKS: i64 = TICKS_PER_DAY * 365 * 100;

static_regex!(SIMPLE_DURATION = r"^(\d+)\s*(s|m|h|d)?$");
// "mo" before the single letters, or "1mo" reads as one MINUTE.
static_regex!(SPAN_PART = r"\d+\s*(?:mo|[smhdwy])");
static_regex!(SPAN_UNIT = r"^(\d+)\s*(mo|[smhdwy])$");
static_regex!(CLOCK_TIME = r"^(\d{1,2})(?::(\d{2}))?(am|pm)?$");

/// Namespace for the time helpers, mirroring the C# static class.
#[derive(Debug)]
pub struct EasternTime;

impl EasternTime {
    /// The longest span either parser produces ([`MAX_SPAN_TICKS`]: a hundred years).
    #[must_use]
    pub fn max_span() -> TimeDelta {
        ticks_to_delta(MAX_SPAN_TICKS)
    }

    /// `YYYY-MM-DD HH:MM:SS` Eastern, or `unknown` for an absent or non-positive timestamp.
    /// Zero means NEVER SEEN; printing 1970-01-01 is a lie that reads as data.
    #[must_use]
    pub fn stamp(instant: Option<DateTime<Utc>>) -> String {
        match instant {
            Some(at) if at.timestamp_millis() > 0 => at
                .with_timezone(&New_York)
                .format("%Y-%m-%d %H:%M:%S")
                .to_string(),
            _ => "unknown".to_owned(),
        }
    }

    /// The Eastern calendar day, not the UTC one, or `unknown`.
    #[must_use]
    pub fn date(instant: Option<DateTime<Utc>>) -> String {
        let stamp = Self::stamp(instant);
        if stamp == "unknown" {
            stamp
        } else {
            stamp[..10].to_owned()
        }
    }

    /// The wall clock as the day key and `HH:MM`, for the scheduled map rotation.
    #[must_use]
    pub fn clock(instant: DateTime<Utc>) -> (String, String) {
        let local = instant.with_timezone(&New_York);
        (
            local.format("%Y-%m-%d").to_string(),
            local.format("%H:%M").to_string(),
        )
    }

    /// How long is left, coarsely: `3d 4h`, `5h 20m`, `12m`, or `expired`. Two units, never
    /// three, and never `0m` for a live ban (see the body).
    #[must_use]
    pub fn time_left(expires: DateTime<Utc>, now: DateTime<Utc>) -> String {
        let remaining = expires - now;
        if remaining <= TimeDelta::zero() {
            return "expired".to_owned();
        }
        // .NET TimeSpan components: whole days, then hours 0-23 and minutes 0-59, truncated.
        let ms = remaining.num_milliseconds();
        let days = ms / 86_400_000;
        let hours = (ms / 3_600_000) % 24;
        let minutes = (ms / 60_000) % 60;
        if days > 0 {
            return format!("{days}d {hours}h");
        }
        if hours > 0 {
            return format!("{hours}h {minutes}m");
        }
        /* ROUNDED UP, NEVER "0m". The ban file written from this string is read back by the
        importer, which rejects a zero quantity - so a temp ban in its last minute came back
        as PERMANENT in the C# until this was fixed. */
        format!("{}m", minutes.max(1))
    }

    /// `30s`, `10m`, `2h`, `1d`; a bare number means MINUTES. `None` when unparseable or zero.
    #[must_use]
    pub fn parse_duration(raw: Option<&str>) -> Option<TimeDelta> {
        let text = raw.unwrap_or("").trim().to_lowercase();
        let caps = SIMPLE_DURATION.captures(&text)?;
        let n: i64 = caps.get(1)?.as_str().parse().ok()?;
        if n == 0 {
            return None;
        }
        let unit = caps.get(2).map_or("m", |u| u.as_str());
        try_ticks(n, unit).map(ticks_to_delta)
    }

    /// A ban length as a compound span: `1d`, `3d 4h`, `1w`, `1mo`, `1y`.
    ///
    /// SPANS ONLY: "until friday" and "2026-08-01" are refused, never guessed at. Anything left
    /// over once the span parts are removed means the input was not purely a span. A month is
    /// 30 days and a year 365, deliberately, so "1mo" means the same whenever it is issued.
    #[must_use]
    pub fn parse_ban_span(raw: Option<&str>) -> Option<TimeDelta> {
        let text = raw.unwrap_or("").trim().to_lowercase();
        if text.is_empty() {
            return None;
        }
        let parts: Vec<&str> = SPAN_PART.find_iter(&text).map(|m| m.as_str()).collect();
        if parts.is_empty() || !SPAN_PART.replace_all(&text, "").trim().is_empty() {
            return None;
        }

        let mut total: i64 = 0;
        for part in parts {
            let caps = SPAN_UNIT.captures(part)?;
            // Parse can fail: `\d+` has no ceiling, so twenty digits arrive here intact.
            let n: i64 = caps.get(1)?.as_str().parse().ok()?;
            let ticks = try_ticks(n, caps.get(2)?.as_str())?;
            // "3d 4h 9999999999y" must not wrap back into a plausible-looking span.
            if total > MAX_SPAN_TICKS - ticks {
                return None;
            }
            total += ticks;
        }
        (total != 0).then(|| ticks_to_delta(total))
    }

    /// `18:30`, `6:30pm`, `3pm`, `0:00`, normalised to 24-hour `HH:MM`, or `None`.
    #[must_use]
    pub fn parse_clock_time(raw: Option<&str>) -> Option<String> {
        let text: String = raw
            .unwrap_or("")
            .trim()
            .to_lowercase()
            .chars()
            .filter(|c| !c.is_whitespace())
            .collect();
        let caps = CLOCK_TIME.captures(&text)?;
        // A non-ASCII digit matches `\d` but does not parse: the C# threw there; this refuses.
        let mut hour: u32 = caps.get(1)?.as_str().parse().ok()?;
        let minute: u32 = match caps.get(2) {
            Some(m) => m.as_str().parse().ok()?,
            None => 0,
        };
        if minute > 59 {
            return None;
        }
        match caps.get(3).map(|m| m.as_str()) {
            Some(meridiem) => {
                if !(1..=12).contains(&hour) {
                    return None;
                }
                if meridiem == "pm" && hour != 12 {
                    hour += 12;
                }
                if meridiem == "am" && hour == 12 {
                    hour = 0;
                }
            }
            None if hour > 23 => return None,
            None => {}
        }
        Some(format!("{hour:02}:{minute:02}"))
    }
}

/// One quantity and unit as ticks, or `None` if it does not fit under [`MAX_SPAN_TICKS`].
///
/// THE DIVISION IS THE POINT: comparing against the quotient first means nothing is ever
/// multiplied out of range. The C# once threw `OverflowException` on "99999999d" from a typo in
/// /tempban and from the game's own banlist.txt, taking out a whole import batch.
fn try_ticks(n: i64, unit: &str) -> Option<i64> {
    if n < 0 {
        return None;
    }
    let per_unit = match unit {
        "s" => TICKS_PER_SECOND,
        "m" => TICKS_PER_MINUTE,
        "h" => TICKS_PER_HOUR,
        "d" => TICKS_PER_DAY,
        "w" => TICKS_PER_DAY * 7,
        "mo" => TICKS_PER_DAY * 30,
        "y" => TICKS_PER_DAY * 365,
        _ => return None,
    };
    if n > MAX_SPAN_TICKS / per_unit {
        return None;
    }
    Some(n * per_unit)
}

/// Ticks to a `TimeDelta`. Never overflows: the largest input is [`MAX_SPAN_TICKS`], about
/// 3.2e18 nanoseconds, under `i64::MAX`.
pub(crate) fn ticks_to_delta(ticks: i64) -> TimeDelta {
    TimeDelta::nanoseconds(ticks.saturating_mul(NANOS_PER_TICK))
}
