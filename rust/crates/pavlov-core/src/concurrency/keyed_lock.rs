//! Per-key mutual exclusion over a FIXED set of stripes (C# `Core/Concurrency/KeyedLock.cs`).
//!
//! STRIPED, NOT A MAP OF LOCKS. The C# once kept one lock per key and reclaimed idle ones; the
//! reclamation could remove a lock out from under a caller that had looked it up but not yet
//! acquired it, letting two writers into one balance. A fixed array has nothing to reclaim.
//! Distinct keys may share a stripe, which costs only parallelism, never correctness.

use std::hash::{Hash, Hasher};

use tokio::sync::{Mutex, MutexGuard};

use crate::text::case::fold_case;

/// How keys are compared: two keys that compare equal always land on the same stripe.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeyComparison {
    /// Exact.
    Ordinal,
    /// .NET `OrdinalIgnoreCase`, as the ledger keys players.
    IgnoreCase,
}

/// The lock. Cheap to share behind an `Arc`.
#[derive(Debug)]
pub struct KeyedLock {
    stripes: Vec<Mutex<()>>,
    comparison: KeyComparison,
}

/// Held while the guarded work runs; dropping it releases the stripe, including on panic.
#[derive(Debug)]
pub struct KeyedLockGuard<'a> {
    _guard: MutexGuard<'a, ()>,
}

/// The C# default.
const DEFAULT_STRIPES: usize = 256;

impl KeyedLock {
    /// A lock with the default 256 stripes.
    #[must_use]
    pub fn new(comparison: KeyComparison) -> Self {
        Self::with_stripes(DEFAULT_STRIPES, comparison)
    }

    /// A lock with `stripes` stripes (at least one).
    #[must_use]
    pub fn with_stripes(stripes: usize, comparison: KeyComparison) -> Self {
        Self {
            stripes: (0..stripes.max(1)).map(|_| Mutex::new(())).collect(),
            comparison,
        }
    }

    /// Wait for the stripe `key` falls on. Cancel-safe: dropping the future gives up the wait.
    pub async fn acquire(&self, key: &str) -> KeyedLockGuard<'_> {
        KeyedLockGuard {
            _guard: self.stripe_for(key).lock().await,
        }
    }

    fn stripe_for(&self, key: &str) -> &Mutex<()> {
        let mut hasher = Fnv1a::default();
        match self.comparison {
            KeyComparison::Ordinal => key.hash(&mut hasher),
            KeyComparison::IgnoreCase => fold_case(key).hash(&mut hasher),
        }
        // usize is at most 64 bits, so the remainder always fits back into usize.
        let index = usize::try_from(hasher.finish() % self.stripes.len() as u64).unwrap_or(0);
        &self.stripes[index]
    }
}

/// FNV-1a: deterministic across runs, so which keys share a stripe is reproducible in tests.
#[derive(Debug)]
struct Fnv1a(u64);

impl Default for Fnv1a {
    fn default() -> Self {
        Self(0xcbf2_9ce4_8422_2325)
    }
}

impl Hasher for Fnv1a {
    fn finish(&self) -> u64 {
        self.0
    }

    fn write(&mut self, bytes: &[u8]) {
        for &b in bytes {
            self.0 ^= u64::from(b);
            self.0 = self.0.wrapping_mul(0x0100_0000_01b3);
        }
    }
}
