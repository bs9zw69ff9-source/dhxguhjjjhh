//! Server monitoring (`Core/Monitoring`). For now only the settings record, which
//! configuration fills in; the probe state machine and its alerts arrive in M2.

mod settings;

pub use settings::MonitorSettings;
