//! The server monitor (C# `Monitoring/ServerMonitor.cs`): one probe per server per tick, in
//! parallel, folded into the pure health state machine; signals become alerts (unless the board
//! is the only surface) and events for the board; every probe goes into the history.
//!
//! - A fresh monitor knows nothing: every server starts UNKNOWN, so a restart cannot invent an
//!   outage, and one clean probe establishes ONLINE without an alert.
//! - An OFFLINE server is not probed again until its back-off has elapsed.
//! - A RECONNECTING server's RCON session is reset first: a wedged session never recovers.
//! - Probes for one server NEVER OVERLAP: a tick that finds the last still running skips it.

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex, PoisonError};

use chrono::{DateTime, Utc};
use futures_util::future::{BoxFuture, join_all};
use pavlov_core::monitoring::{
    HealthProbe, MonitorSettings, MonitorSignal, ServerHealth, Severity, SignalKind,
};

use super::history::{HealthStats, MonitorHistory, MonitorSample};
use crate::registry::RconRegistry;

const EVENT_BUFFER_SIZE: usize = 20;

/// One probe of one server.
pub trait ServerProbe: Send + Sync {
    fn probe<'a>(&'a self, server: &'a str) -> BoxFuture<'a, HealthProbe>;
}

/// Where alerts go (the Discord layer implements it).
pub trait MonitorAlertSink: Send + Sync {
    fn post<'a>(
        &'a self,
        server: &'a str,
        signal: &'a MonitorSignal,
        health: &'a ServerHealth,
    ) -> BoxFuture<'a, ()>;

    /// Post a synthetic alert to prove the channel works, and say what happened in one line.
    fn test(&self) -> BoxFuture<'_, String>;
}

/// The servers to watch, and how to reset a wedged session.
pub trait MonitorTargets: Send + Sync {
    fn servers(&self) -> Vec<String>;
    fn reset_session<'a>(&'a self, server: &'a str) -> BoxFuture<'a, ()>;
}

impl MonitorTargets for RconRegistry {
    fn servers(&self) -> Vec<String> {
        RconRegistry::servers(self)
    }

    fn reset_session<'a>(&'a self, server: &'a str) -> BoxFuture<'a, ()> {
        Box::pin(async move {
            if let Some(client) = self.client(server) {
                client.reset_session().await;
            }
        })
    }
}

/// A transition, for the board's event log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MonitorEvent {
    pub server: String,
    pub kind: SignalKind,
    pub severity: Severity,
    pub detail: String,
    pub at: DateTime<Utc>,
}

/// What the monitor is built from.
pub struct MonitorParts {
    pub probe: Arc<dyn ServerProbe>,
    pub alerts: Arc<dyn MonitorAlertSink>,
    pub history: Arc<MonitorHistory>,
    pub settings: MonitorSettings,
    pub targets: Arc<dyn MonitorTargets>,
    pub clock: Arc<dyn pavlov_core::time::Clock>,
    /// False in board mode: the one board is the only surface.
    pub post_individual_alerts: bool,
}

impl std::fmt::Debug for MonitorParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MonitorParts")
            .field("post_individual_alerts", &self.post_individual_alerts)
            .finish_non_exhaustive()
    }
}

/// The monitor.
pub struct ServerMonitor {
    parts: MonitorParts,
    state: Mutex<HashMap<String, ServerHealth>>,
    gates: Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
    events: Mutex<VecDeque<MonitorEvent>>,
}

impl std::fmt::Debug for ServerMonitor {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServerMonitor").finish_non_exhaustive()
    }
}

impl ServerMonitor {
    #[must_use]
    pub fn new(parts: MonitorParts) -> Self {
        Self {
            parts,
            state: Mutex::new(HashMap::new()),
            gates: Mutex::new(HashMap::new()),
            events: Mutex::new(VecDeque::with_capacity(EVENT_BUFFER_SIZE)),
        }
    }

    #[must_use]
    pub fn settings(&self) -> &MonitorSettings {
        &self.parts.settings
    }

    #[must_use]
    pub fn servers(&self) -> Vec<String> {
        self.parts.targets.servers()
    }

    /// Where a server is now; UNKNOWN before its first probe.
    #[must_use]
    pub fn snapshot(&self, server: &str) -> ServerHealth {
        self.state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .get(server)
            .cloned()
            .unwrap_or_else(ServerHealth::initial)
    }

    #[must_use]
    pub fn stats(&self, server: &str, window: std::time::Duration) -> HealthStats {
        self.parts.history.stats(server, window)
    }

    /// The last transitions, oldest first.
    #[must_use]
    pub fn recent_events(&self) -> Vec<MonitorEvent> {
        self.events
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .iter()
            .cloned()
            .collect()
    }

    /// Check every server, in parallel.
    pub async fn tick(&self) {
        let servers = self.servers();
        join_all(servers.iter().map(|s| self.check(s))).await;
    }

    async fn check(&self, server: &str) {
        let gate = Arc::clone(
            self.gates
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .entry(server.to_owned())
                .or_default(),
        );
        // Busy means the last check is still running: skip rather than queue behind it.
        let Ok(_held) = gate.try_lock() else {
            return;
        };

        let current = self.snapshot(server);
        let now = self.parts.clock.now();
        if current.declared_offline && !current.may_attempt_at(now) {
            return;
        }
        if current.is_reconnecting() {
            self.parts.targets.reset_session(server).await;
        }

        let probe = self.parts.probe.probe(server).await;
        let (next, signals) = current.observe(&probe, &self.parts.settings);
        self.state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(server.to_owned(), next.clone());

        for signal in &signals {
            self.record_event(MonitorEvent {
                server: server.to_owned(),
                kind: signal.kind,
                severity: signal.severity,
                detail: signal.detail.clone(),
                at: probe.at,
            });
            if self.parts.post_individual_alerts {
                self.parts.alerts.post(server, signal, &next).await;
            }
        }

        let sample = MonitorSample {
            at: probe.at,
            state: next.state,
            latency_ms: probe.latency.map(|l| l.as_secs_f64() * 1000.0),
            players: probe.players,
            rcon_ok: probe.rcon_ok,
            unexpected_restart: signals
                .iter()
                .any(|s| s.kind == SignalKind::UnexpectedRestart),
        };
        if let Err(error) = self.parts.history.record(server, sample).await {
            tracing::error!(server, %error, "could not record the monitoring sample - state kept, will retry");
        }
    }

    fn record_event(&self, event: MonitorEvent) {
        let mut events = self.events.lock().unwrap_or_else(PoisonError::into_inner);
        events.push_back(event);
        while events.len() > EVENT_BUFFER_SIZE {
            events.pop_front();
        }
    }
}
