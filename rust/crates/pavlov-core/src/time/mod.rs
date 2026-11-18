//! Every human-readable time the bot prints or parses (`Core/Time`).

mod ban_durations;
mod eastern_time;

pub use ban_durations::BanDurations;
pub use eastern_time::{Clock, EasternTime, ManualClock, SystemClock};
