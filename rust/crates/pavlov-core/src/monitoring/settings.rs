//! Every monitor tunable in one place (C# `Core/Monitoring/MonitorTypes.cs`, `MonitorSettings`).

use std::time::Duration;

/// Nothing in the monitor hard-codes a threshold; every one is here and overridable from
/// configuration.
#[derive(Debug, Clone, PartialEq)]
pub struct MonitorSettings {
    /// How often the loop probes each server.
    pub check_interval: Duration,
    /// Consecutive failed probes before a server is declared OFFLINE. Never one.
    pub failure_threshold: u32,
    /// Consecutive good probes before an OFFLINE server is declared back ONLINE.
    pub recovery_threshold: u32,
    /// How long a single probe waits before it counts as a failure.
    pub rcon_timeout: Duration,
    /// First reconnect back-off.
    pub retry_base_delay: Duration,
    /// Ceiling the exponential back-off is clamped to.
    pub retry_max_delay: Duration,
    /// At or above this, latency is DEGRADED.
    pub latency_warn: Duration,
    /// At or above this, latency is CRITICAL.
    pub latency_critical: Duration,
    /// A drop of at least this fraction of players inside the window is an anomaly.
    pub player_anomaly_drop_fraction: f64,
    /// The window a sudden drop is measured over.
    pub player_anomaly_window: Duration,
    /// No log advance for this long, while the server answers, is an alert.
    pub log_inactivity_threshold: Duration,
    /// While OFFLINE, re-alert once every this many further failed probes. 0 disables.
    pub escalation_every: u32,
    /// Whether recovery ("back ONLINE") messages are posted.
    pub recovery_messages_enabled: bool,
}

impl Default for MonitorSettings {
    /// The values the C# ships with.
    fn default() -> Self {
        Self {
            check_interval: Duration::from_secs(30),
            failure_threshold: 3,
            recovery_threshold: 2,
            rcon_timeout: Duration::from_secs(5),
            retry_base_delay: Duration::from_secs(2),
            retry_max_delay: Duration::from_secs(120),
            latency_warn: Duration::from_millis(250),
            latency_critical: Duration::from_millis(750),
            player_anomaly_drop_fraction: 0.8,
            // Longer than the check interval on purpose: a drop is measured between
            // consecutive probes, so the previous reading must still count as "recent" when
            // the next arrives.
            player_anomaly_window: Duration::from_secs(90),
            log_inactivity_threshold: Duration::from_secs(5 * 60),
            escalation_every: 20,
            recovery_messages_enabled: true,
        }
    }
}
