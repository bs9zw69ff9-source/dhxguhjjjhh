//! Exponential backoff with jitter (C# `Backoff`).
//!
//! Fixed backoff makes every caller retry in lockstep after a blip - a synchronised burst
//! exactly when the server can least serve one.

use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::time::Duration;

/// The default first ceiling.
pub const BASE_MS: u64 = 500;
/// The largest ceiling.
pub const MAX_MS: u64 = 30_000;

/// A uniform value in `[0, 1)`. Jitter needs spread, not unpredictability, and `RandomState`
/// is seeded per instance from the OS, which is plenty for that without another dependency.
fn unit() -> f64 {
    let bits = RandomState::new().build_hasher().finish() >> 11;
    // 53 bits of mantissa: exact in an f64.
    bits as f64 / (1u64 << 53) as f64
}

/// 50-100% of `min(max_ms, base_ms * 2^attempt)`.
#[must_use]
pub fn delay(attempt: u32, base_ms: u64, max_ms: u64) -> Duration {
    let ceiling = max_ms.min(base_ms.saturating_mul(1u64 << attempt.min(16)));
    let ms = ceiling as f64 * (0.5 + unit() * 0.5);
    Duration::from_secs_f64(ms / 1000.0)
}

/// [`delay`] with the default base and cap.
#[must_use]
pub fn default_delay(attempt: u32) -> Duration {
    delay(attempt, BASE_MS, MAX_MS)
}
