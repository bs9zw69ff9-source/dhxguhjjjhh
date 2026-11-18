//! Reading the game's logs (`Core/Logs`): one `Pavlov.log` line at a time, the join
//! correlation over them, and `Stats.log`'s structured kill records.

mod join_correlator;
pub mod pavlov_log;
mod stats_log;

pub use join_correlator::{JoinCorrelator, JoinGuess};
pub use pavlov_log::{ConfirmedPairing, KillEvent, LoginRequest, RconAction};
pub use stats_log::{StatsKill, StatsLogReader, StatsRecord, StatsRoundState};
