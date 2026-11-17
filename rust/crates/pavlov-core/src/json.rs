//! The stored JSON shape, which is a live contract (C# `Host/Storage/EpochTimeConverter.cs`).
//!
//! The Node bot wrote the data files the bot still reads: camelCase keys and every timestamp
//! as `Date.now()` milliseconds. The C# reads three timestamp forms and writes one, and so does
//! this:
//!
//! * READ a number (Node's, and everything since), an ISO-8601 string (an early C# build), or a
//!   number in quotes. `null` and an empty string are absent. Anything else (an object, an
//!   array) is ABSENT too, never an error: an error fails the whole dataset, and a ban list that
//!   fails to read is treated as empty and then overwritten on the next ban.
//! * WRITE milliseconds, always.
//!
//! Absent stays absent, never epoch zero: a ban with no expiry that is not permanent is
//! INVALID and skipped, while one that expired in 1970 would be lifted on the next sweep.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Deserializer, Serializer};
use serde_json::Value;

/// A stored timestamp, read leniently (see the module docs). `None` when absent or unreadable.
#[must_use]
pub fn timestamp_from_value(value: &Value) -> Option<DateTime<Utc>> {
    match value {
        Value::Number(n) => {
            // JSON has no integer type; a fractional millisecond is truncated, not rejected.
            let ms = n.as_i64().or_else(|| n.as_f64().map(|f| f as i64))?;
            DateTime::from_timestamp_millis(ms)
        }
        Value::String(text) => {
            let text = text.trim();
            if text.is_empty() {
                return None;
            }
            parse_iso(text).or_else(|| {
                text.parse::<i64>()
                    .ok()
                    .and_then(DateTime::from_timestamp_millis)
            })
        }
        _ => None,
    }
}

/// ISO 8601 as .NET's `DateTimeOffset.TryParse` with `AssumeUniversal` reads it: with or
/// without an offset, with or without fractional seconds, `T` or a space between date and time.
fn parse_iso(text: &str) -> Option<DateTime<Utc>> {
    if let Ok(at) = DateTime::parse_from_rfc3339(text) {
        return Some(at.with_timezone(&Utc));
    }
    for format in [
        "%Y-%m-%dT%H:%M:%S%.f",
        "%Y-%m-%d %H:%M:%S%.f",
        "%Y-%m-%dT%H:%M:%S",
        "%Y-%m-%d %H:%M:%S",
        "%Y-%m-%d %H:%M:%S%.f%:z",
    ] {
        if let Ok(naive) = chrono::NaiveDateTime::parse_from_str(text, format) {
            return Some(naive.and_utc());
        }
    }
    chrono::NaiveDate::parse_from_str(text, "%Y-%m-%d")
        .ok()
        .and_then(|d| d.and_hms_opt(0, 0, 0))
        .map(|n| n.and_utc())
}

/// `#[serde(with = "pavlov_core::json::epoch_ms_opt", default)]` for an optional timestamp.
pub mod epoch_ms_opt {
    use super::{
        DateTime, Deserialize, Deserializer, Serializer, Utc, Value, timestamp_from_value,
    };

    /// Milliseconds, or `null`.
    ///
    /// # Errors
    /// Only what the underlying serializer reports.
    #[allow(clippy::ref_option)] // serde's `with` contract passes `&Option<T>`
    pub fn serialize<S: Serializer>(
        value: &Option<DateTime<Utc>>,
        serializer: S,
    ) -> Result<S::Ok, S::Error> {
        match value {
            Some(at) => serializer.serialize_i64(at.timestamp_millis()),
            None => serializer.serialize_none(),
        }
    }

    /// Lenient read: see [`timestamp_from_value`].
    ///
    /// # Errors
    /// Only when the input is not JSON at all.
    pub fn deserialize<'de, D: Deserializer<'de>>(
        deserializer: D,
    ) -> Result<Option<DateTime<Utc>>, D::Error> {
        Ok(timestamp_from_value(&Value::deserialize(deserializer)?))
    }
}

/// `#[serde(with = "pavlov_core::json::epoch_ms")]` for a timestamp the type cannot do without.
/// An unreadable value reads as the Unix epoch, as the C# non-nullable converter's `default`.
pub mod epoch_ms {
    use super::{
        DateTime, Deserialize, Deserializer, Serializer, Utc, Value, timestamp_from_value,
    };

    /// Milliseconds.
    ///
    /// # Errors
    /// Only what the underlying serializer reports.
    pub fn serialize<S: Serializer>(
        value: &DateTime<Utc>,
        serializer: S,
    ) -> Result<S::Ok, S::Error> {
        serializer.serialize_i64(value.timestamp_millis())
    }

    /// Lenient read: see [`timestamp_from_value`].
    ///
    /// # Errors
    /// Only when the input is not JSON at all.
    pub fn deserialize<'de, D: Deserializer<'de>>(
        deserializer: D,
    ) -> Result<DateTime<Utc>, D::Error> {
        Ok(
            timestamp_from_value(&Value::deserialize(deserializer)?)
                .unwrap_or(DateTime::UNIX_EPOCH),
        )
    }
}

/// `#[serde(deserialize_with = "pavlov_core::json::null_as_default")]`: a `null` where a list
/// or map belongs reads as empty. The C# let `null` through and failed later on the first
/// enumeration; an empty collection is what every caller means by it.
///
/// # Errors
/// When the value is neither `null` nor a valid `T`.
pub fn null_as_default<'de, D, T>(deserializer: D) -> Result<T, D::Error>
where
    D: Deserializer<'de>,
    T: Default + Deserialize<'de>,
{
    Ok(Option::<T>::deserialize(deserializer)?.unwrap_or_default())
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;
    use serde_json::json;

    #[test]
    fn every_stored_timestamp_form_reads() {
        let at = Utc.with_ymd_and_hms(2026, 7, 15, 12, 0, 0).unwrap();
        let ms = at.timestamp_millis();
        assert_eq!(timestamp_from_value(&json!(ms)), Some(at));
        assert_eq!(timestamp_from_value(&json!(ms as f64 + 0.4)), Some(at));
        assert_eq!(
            timestamp_from_value(&json!("2026-07-15T12:00:00Z")),
            Some(at)
        );
        assert_eq!(
            timestamp_from_value(&json!("2026-07-15T08:00:00-04:00")),
            Some(at)
        );
        assert_eq!(
            timestamp_from_value(&json!("2026-07-15T12:00:00")),
            Some(at)
        );
        assert_eq!(timestamp_from_value(&json!(ms.to_string())), Some(at));
    }

    #[test]
    fn anything_else_is_absent_never_an_error() {
        for value in [
            json!(null),
            json!(""),
            json!("  "),
            json!("soon"),
            json!({"a": 1}),
            json!([1, 2]),
            json!(true),
        ] {
            assert_eq!(timestamp_from_value(&value), None, "value {value}");
        }
    }
}
