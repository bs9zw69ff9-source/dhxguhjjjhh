//! Guards on writes the game reads (`Core/Data`).

mod roster_write_guard;

pub use roster_write_guard::{RosterWriteDecision, RosterWriteGuard, RosterWriteVerdict};
