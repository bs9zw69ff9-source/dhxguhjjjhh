//! Concurrency primitives shared by the stores (`Core/Concurrency`).

mod keyed_lock;

pub use keyed_lock::{KeyComparison, KeyedLock, KeyedLockGuard};
