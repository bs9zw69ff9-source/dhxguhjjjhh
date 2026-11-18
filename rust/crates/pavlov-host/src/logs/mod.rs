//! Reading the game servers' logs (C# `Host/Logs`): who joined from where, kills, and the RCON
//! commands the game recorded. The line parsing is pure and lives in `pavlov_core::logs`.

pub mod feed_bridge;
pub mod ip_tracking;
pub mod stats_log_service;
pub mod tailer;

pub use feed_bridge::{FeedBridge, FeedBridgeParts};
pub use ip_tracking::{FlaggedJoin, IpTrackingService, PlayerConfirmed, PlayerJoined, RconLogged};
pub use stats_log_service::{NameResolver, StatsLogService};
pub use tailer::LogTailer;

/// One line of one server's log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LogLine {
    /// Which log it came from: the key every per-server state is held under.
    pub file: String,
    pub text: String,
}

impl LogLine {
    #[must_use]
    pub fn new(file: impl Into<String>, text: impl Into<String>) -> Self {
        Self {
            file: file.into(),
            text: text.into(),
        }
    }
}
