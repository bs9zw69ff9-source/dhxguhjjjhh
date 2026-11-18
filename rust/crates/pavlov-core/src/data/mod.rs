//! Datasets and guards on writes the game reads (`Core/Data`).

mod memory_backend;
mod roster_write_guard;
mod serialized_store;

pub use memory_backend::MemoryBackend;
pub use roster_write_guard::{RosterWriteDecision, RosterWriteGuard, RosterWriteVerdict};
pub use serialized_store::{KeyValueBackend, SerializedStore, StoreError, UpdateResult};
