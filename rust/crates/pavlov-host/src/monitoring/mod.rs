//! Watching each game server: probing, the health state machine's transitions, alerts, the
//! 25-hour history and the live board (C# `Host/Monitoring`). The state machine itself is pure
//! and lives in `pavlov_core::monitoring`.

pub mod board;
pub mod history;
pub mod monitor;

pub use board::MonitorBoard;
pub use history::{HealthStats, MonitorHistory, MonitorSample};
pub use monitor::{
    MonitorAlertSink, MonitorEvent, MonitorParts, MonitorTargets, ServerMonitor, ServerProbe,
};
