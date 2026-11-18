//! The monitor's probe over RCON (C# `Monitoring/ServerProbe.cs` `RconServerProbe`) and the
//! log-activity reading it carries (C# `ServerLogActivity.cs`).
//!
//! - UNCACHED on purpose: a monitor must time a real round trip, never read a cached success
//!   for a server that has just gone down.
//! - Bounded by `MonitorSettings::rcon_timeout`; a timeout is named as one.
//! - A server somebody STOPPED is asked of systemd, so it reads as stopped, not as a fault.
//!   When systemd cannot answer, the fault stands.
//! - A log that cannot be read is "unknown", never a crash.

use std::sync::Arc;
use std::time::Instant;

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::monitoring::{HealthProbe, MonitorSettings};
use pavlov_core::time::Clock;
use pavlov_rcon::ServerLifecycle;
use pavlov_rcon::reply;

use crate::monitoring::ServerProbe;
use crate::registry::RconRegistry;
use crate::server_labels::{self, ServerLabels};

/// When a server's log was last written to.
pub type LastLogActivity = Arc<dyn Fn(&str) -> Option<DateTime<Utc>> + Send + Sync>;

/// The probe.
pub struct RconServerProbe {
    rcon: Arc<RconRegistry>,
    settings: MonitorSettings,
    clock: Arc<dyn Clock>,
    lifecycle: Option<Arc<dyn ServerLifecycle>>,
    last_log: Option<LastLogActivity>,
}

impl std::fmt::Debug for RconServerProbe {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RconServerProbe").finish_non_exhaustive()
    }
}

impl RconServerProbe {
    #[must_use]
    pub fn new(
        rcon: Arc<RconRegistry>,
        settings: MonitorSettings,
        clock: Arc<dyn Clock>,
        lifecycle: Option<Arc<dyn ServerLifecycle>>,
        last_log: Option<LastLogActivity>,
    ) -> Self {
        Self {
            rcon,
            settings,
            clock,
            lifecycle,
            last_log,
        }
    }

    async fn run(&self, server: &str) -> HealthProbe {
        let at = self.clock.now();
        let intentionally_stopped = match &self.lifecycle {
            Some(lifecycle) => lifecycle.is_intentionally_stopped(server).await,
            None => false,
        };
        let last_log_activity = self.last_log.as_ref().and_then(|read| read(server));
        let started = Instant::now();
        let answer = tokio::time::timeout(
            self.settings.rcon_timeout,
            self.rcon.probe(server, "ServerInfo"),
        )
        .await;
        let failed = |reason: String| HealthProbe {
            rcon_ok: false,
            latency: None,
            players: None,
            max_players: None,
            map: None,
            process_running: None,
            intentionally_stopped,
            last_log_activity,
            failure_reason: Some(reason),
            at,
        };
        match answer {
            Err(_) => failed(format!(
                "no reply within {}s",
                self.settings.rcon_timeout.as_secs()
            )),
            Ok(Err(error)) => failed(error.to_string()),
            Ok(Ok(body)) => {
                let latency = started.elapsed();
                let info = reply::try_parse(Some(&body)).map(|v| reply::server_info(&v));
                let roster = self.rcon.roster(server);
                HealthProbe {
                    rcon_ok: true,
                    latency: Some(latency),
                    players: roster
                        .taken_at
                        .map(|_| u32::try_from(roster.players.len()).unwrap_or(u32::MAX)),
                    max_players: info
                        .as_ref()
                        .and_then(|i| i.max_players)
                        .and_then(|m| u32::try_from(m).ok()),
                    map: info.and_then(|i| i.map_label),
                    process_running: None,
                    intentionally_stopped,
                    last_log_activity,
                    failure_reason: None,
                    at,
                }
            }
        }
    }
}

impl ServerProbe for RconServerProbe {
    fn probe<'a>(&'a self, server: &'a str) -> BoxFuture<'a, HealthProbe> {
        Box::pin(self.run(server))
    }
}

/// When the log of the server with this RCON name was last written, by RCON NUMBER: the
/// C# once compared "Server 1" with "server1", which never matched, so the monitor never saw
/// a log time at all.
#[must_use]
pub fn log_activity(labels: Arc<ServerLabels>, paths: Vec<String>) -> LastLogActivity {
    Arc::new(move |server: &str| {
        let path = labels
            .log_for(server)
            .or_else(|| server_labels::log_for(&paths, server))?;
        let modified = std::fs::metadata(path).and_then(|m| m.modified()).ok()?;
        Some(DateTime::<Utc>::from(modified))
    })
}
