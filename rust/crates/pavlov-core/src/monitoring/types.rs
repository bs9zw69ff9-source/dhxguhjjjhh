//! The monitor's vocabulary (C# `Core/Monitoring/MonitorTypes.cs`).

use std::time::Duration;

use chrono::{DateTime, Utc};

/// The state a monitored server is in, as the machine sees it.
///
/// UNKNOWN is the START state and the honest answer after a bot restart: nothing has been
/// probed, so the server is neither declared healthy nor down. Reporting ONLINE before the
/// first probe is exactly the false green a restart must not produce.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum HealthState {
    #[default]
    Unknown,
    Online,
    Degraded,
    Offline,
    Reconnecting,
}

/// Which latency band the last round trip fell in.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum LatencyBand {
    #[default]
    Unknown,
    Healthy,
    Degraded,
    Critical,
}

impl LatencyBand {
    /// The band as the alert text writes it.
    #[must_use]
    pub fn upper(self) -> &'static str {
        match self {
            Self::Unknown => "UNKNOWN",
            Self::Healthy => "HEALTHY",
            Self::Degraded => "DEGRADED",
            Self::Critical => "CRITICAL",
        }
    }
}

/// How a return to service is classified: the restart/crash distinction.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum RestartKind {
    #[default]
    None,
    Expected,
    Unexpected,
    Unknown,
}

/// An alert-worthy thing the machine decided happened. One per genuine change.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SignalKind {
    ServerOffline,
    ServerOnline,
    /// Escalation while an outage persists - NOT one per check.
    StillOffline,
    RconDisconnected,
    RconReconnected,
    HighLatency,
    LatencyRecovered,
    RepeatedRconFailures,
    LogInactive,
    UnexpectedRestart,
    PlayerCountZero,
    PlayerCapacityReached,
    PlayerAnomaly,
}

/// How loud a signal is: drives the colour and whether the alert role is pinged.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Severity {
    Info,
    Warning,
    Critical,
}

/// One probe result: everything the decision needs and nothing tying it to a transport, so the
/// machine is tested with plain values, no sockets.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HealthProbe {
    /// The RCON round trip succeeded.
    pub rcon_ok: bool,
    /// The round-trip time; `None` when the probe failed.
    pub latency: Option<Duration>,
    pub players: Option<u32>,
    pub max_players: Option<u32>,
    pub map: Option<String>,
    /// systemd's view of the process; `None` when it cannot be asked.
    pub process_running: Option<bool>,
    /// The server was stopped on purpose (a switch, an admin, the bot).
    pub intentionally_stopped: bool,
    /// When the server's log last advanced.
    pub last_log_activity: Option<DateTime<Utc>>,
    /// Why the probe failed, for the alert.
    pub failure_reason: Option<String>,
    /// The probe's wall-clock time.
    pub at: DateTime<Utc>,
}

/// Rolling latency statistics over the samples seen this run. Long-run figures come from the
/// persisted history, not this.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct LatencyStats {
    pub current: Option<Duration>,
    pub min: Option<Duration>,
    pub max: Option<Duration>,
    pub average: Option<Duration>,
    pub samples: u64,
    /// Whole milliseconds summed, as the C# keeps it.
    pub total_milliseconds: u64,
}

impl LatencyStats {
    /// These statistics with one more sample.
    #[must_use]
    pub fn with(self, latency: Duration) -> Self {
        let samples = self.samples + 1;
        let whole_ms = u64::try_from(latency.as_millis()).unwrap_or(u64::MAX);
        let total = self.total_milliseconds.saturating_add(whole_ms);
        // TimeSpan.FromMilliseconds(total / samples): truncated to whole 100 ns ticks.
        let average_ticks = (total as f64 / samples as f64 * 10_000.0).trunc();
        Self {
            current: Some(latency),
            min: Some(self.min.map_or(latency, |m| m.min(latency))),
            max: Some(self.max.map_or(latency, |m| m.max(latency))),
            average: Some(Duration::from_nanos(average_ticks as u64 * 100)),
            samples,
            total_milliseconds: total,
        }
    }
}

/// An alert the machine decided to raise. The host adds the server name and formats it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MonitorSignal {
    pub kind: SignalKind,
    pub severity: Severity,
    /// One line, already written for a human.
    pub detail: String,
}

impl MonitorSignal {
    pub(crate) fn new(kind: SignalKind, severity: Severity, detail: String) -> Self {
        Self {
            kind,
            severity,
            detail,
        }
    }
}
