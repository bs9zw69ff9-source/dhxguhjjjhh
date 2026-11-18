//! Server monitoring (`Core/Monitoring`): the settings, the vocabulary and the pure health
//! state machine. The loop that probes servers and posts alerts is `pavlov-services`'.

mod health;
mod settings;
mod types;

pub use health::{ServerHealth, humanize};
pub use settings::MonitorSettings;
pub use types::{
    HealthProbe, HealthState, LatencyBand, LatencyStats, MonitorSignal, RestartKind, Severity,
    SignalKind,
};
