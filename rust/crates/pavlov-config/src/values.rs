//! Reading one setting as a number, a switch, an id or a list - the C# options' private
//! helpers, in one place, parsing the way .NET's `TryParse` does with the invariant culture.
//!
//! Every helper answers "absent or nonsense" with the caller's fallback rather than an error:
//! these settings turn features on and off, and a feature left at its default says so in the
//! startup summary. The settings that are REQUIRED are checked by `BotOptions::validate`.

use std::str::FromStr;
use std::time::Duration;

use crate::Config;

/// .NET ticks: 100 ns.
const TICKS_PER_MILLISECOND: f64 = 10_000.0;
const TICKS_PER_MINUTE: f64 = 600_000_000.0;

/// The whitespace `NumberStyles.AllowLeadingWhite` and `AllowTrailingWhite` skip.
fn number_whitespace(c: char) -> bool {
    matches!(c, '\t'..='\r' | ' ')
}

/// `value?.Trim()`, non-empty, or `None`.
#[must_use]
pub fn text(config: &Config, key: &str) -> Option<String> {
    config
        .get(key)
        .map(str::trim)
        .filter(|v| !v.is_empty())
        .map(str::to_owned)
}

/// A switch where "unset" differs from "off": `1`, `true`, `yes` and `on` are on, anything
/// else that is set is off. A `.env` is written by hand, and a switch that silently ignored
/// `=1` would be one somebody spends an hour on.
#[must_use]
pub fn optional_flag(config: &Config, key: &str) -> Option<bool> {
    let value = text(config, key)?;
    Some(
        ["1", "true", "yes", "on"]
            .iter()
            .any(|on| value.eq_ignore_ascii_case(on)),
    )
}

/// An opt-in switch: anything that is not affirmative is off.
#[must_use]
pub fn flag(config: &Config, key: &str) -> bool {
    optional_flag(config, key) == Some(true)
}

/// `T.TryParse(value, InvariantCulture)` under `NumberStyles.Integer`: surrounding whitespace
/// and a sign are allowed, nothing else.
pub fn integer<T: FromStr>(raw: Option<&str>) -> Option<T> {
    raw?.trim_matches(number_whitespace).parse().ok()
}

/// A positive whole number, or `fallback` (C# `Int`).
#[must_use]
pub fn positive(config: &Config, key: &str, fallback: u32) -> u32 {
    integer::<i32>(config.get(key))
        .filter(|&v| v > 0)
        .and_then(|v| u32::try_from(v).ok())
        .unwrap_or(fallback)
}

/// A whole number where zero is meaningful (it switches something off), or `fallback` when
/// unset, unparseable or negative.
#[must_use]
pub fn non_negative(config: &Config, key: &str, fallback: u32) -> u32 {
    integer::<i32>(config.get(key))
        .and_then(|v| u32::try_from(v).ok())
        .unwrap_or(fallback)
}

/// A money amount; zero is how a feature is left off, so anything not positive is zero.
#[must_use]
pub fn money(config: &Config, key: &str) -> i64 {
    integer::<i64>(config.get(key))
        .filter(|&v| v > 0)
        .unwrap_or(0)
}

/// A Discord id: positive, or `None`.
#[must_use]
pub fn snowflake(config: &Config, key: &str) -> Option<u64> {
    integer::<u64>(config.get(key)).filter(|&id| id > 0)
}

/// Discord ids split on any of `separators`; entries that are not positive ids are dropped.
#[must_use]
pub fn snowflakes(config: &Config, key: &str, separators: &[char]) -> Vec<u64> {
    config
        .get(key)
        .unwrap_or_default()
        .split(separators)
        .map(str::trim)
        .filter(|v| !v.is_empty())
        .map(|v| v.parse::<u64>().unwrap_or(0))
        .filter(|&id| id > 0)
        .collect()
}

/// A comma-separated list, each entry trimmed, empty entries dropped.
#[must_use]
pub fn list(config: &Config, key: &str) -> Vec<String> {
    config
        .get(key)
        .unwrap_or_default()
        .split(',')
        .map(str::trim)
        .filter(|v| !v.is_empty())
        .map(str::to_owned)
        .collect()
}

/// `double.TryParse(value, InvariantCulture)`: `NumberStyles.Float | AllowThousands`, so
/// surrounding whitespace, a leading sign, a decimal point, an exponent and `,` group
/// separators in the integer part. Non-finite values are `None`: every caller turns the number
/// into a duration, and .NET throws `OverflowException` at startup for one that big.
#[must_use]
pub fn real(raw: Option<&str>) -> Option<f64> {
    let raw = raw?.trim_matches(number_whitespace);
    let (sign, unsigned) = match raw.strip_prefix(['+', '-']) {
        Some(rest) => (&raw[..1], rest),
        None => ("", raw),
    };
    let (integer_part, rest) =
        unsigned.split_at(unsigned.find(['.', 'e', 'E']).unwrap_or(unsigned.len()));
    let well_formed = !unsigned.starts_with(',')
        && integer_part.chars().all(|c| c.is_ascii_digit() || c == ',')
        && !rest.contains(',')
        && unsigned.chars().any(|c| c.is_ascii_digit());
    if !well_formed {
        return None;
    }
    format!("{sign}{}{rest}", integer_part.replace(',', ""))
        .parse::<f64>()
        .ok()
        .filter(|v| v.is_finite())
}

/// `value` units of `ticks_per_unit` ticks as a duration, truncated to whole ticks the way
/// `TimeSpan.FromMilliseconds(double)` does; `None` where `TimeSpan` would throw.
fn duration(value: f64, ticks_per_unit: f64) -> Option<Duration> {
    let ticks = value * ticks_per_unit;
    // The bound compared as a double, as .NET compares it.
    let max_ticks = i64::MAX as f64;
    if !ticks.is_finite() || ticks < 0.0 || ticks > max_ticks {
        return None;
    }
    // In range and non-negative, checked just above.
    let ticks = ticks.trunc() as u64;
    let nanos = u32::try_from((ticks % 10_000_000) * 100).ok()?;
    Some(Duration::new(ticks / 10_000_000, nanos))
}

/// Milliseconds that must be positive - a zero-length timer cannot run - or `fallback`.
#[must_use]
pub fn positive_milliseconds(config: &Config, key: &str, fallback: Duration) -> Duration {
    real(config.get(key))
        .filter(|&ms| ms > 0.0)
        .and_then(|ms| duration(ms, TICKS_PER_MILLISECOND))
        .unwrap_or(fallback)
}

/// Milliseconds where zero is meaningful (no read cache), or `fallback`.
#[must_use]
pub fn milliseconds(config: &Config, key: &str, fallback: Duration) -> Duration {
    real(config.get(key))
        .filter(|&ms| ms >= 0.0)
        .and_then(|ms| duration(ms, TICKS_PER_MILLISECOND))
        .unwrap_or(fallback)
}

/// Positive minutes, which is how anybody writing a wage interval thinks, or `fallback`.
#[must_use]
pub fn minutes(config: &Config, key: &str, fallback: Duration) -> Duration {
    real(config.get(key))
        .filter(|&m| m > 0.0)
        .and_then(|m| duration(m, TICKS_PER_MINUTE))
        .unwrap_or(fallback)
}

/// Whole seconds as a duration.
#[must_use]
pub fn seconds(count: u32) -> Duration {
    Duration::from_secs(u64::from(count))
}

/// Whole days as a duration.
#[must_use]
pub fn days(count: u32) -> Duration {
    Duration::from_secs(u64::from(count) * 86_400)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn real_numbers_parse_the_way_dotnet_parses_them() {
        assert_eq!(real(Some(" 2500 ")), Some(2500.0));
        assert_eq!(real(Some("1,000")), Some(1000.0));
        assert_eq!(real(Some("1.5e3")), Some(1500.0));
        assert_eq!(real(Some("+.5")), Some(0.5));
        assert_eq!(real(Some("-3")), Some(-3.0));
        for nonsense in [
            "soon", "", ",5", "1.0,5", "1e", "Infinity", "NaN", "1e400", "0x10",
        ] {
            assert_eq!(real(Some(nonsense)), None, "{nonsense:?}");
        }
    }

    #[test]
    fn durations_truncate_to_ticks_and_refuse_what_timespan_cannot_hold() {
        assert_eq!(
            duration(1.000_09, TICKS_PER_MILLISECOND),
            Some(Duration::from_nanos(1_000_000))
        );
        assert_eq!(duration(1e300, TICKS_PER_MILLISECOND), None);
        assert_eq!(
            duration(500.0, TICKS_PER_MILLISECOND),
            Some(Duration::from_millis(500))
        );
    }
}
