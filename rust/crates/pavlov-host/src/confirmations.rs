//! Confirming a state-changing RCON command from Pavlov.log (C# `Logs/RconConfirmations.cs`).
//!
//! THE LOG IS WHAT THE SERVER ACTUALLY RAN. Pavlov services RCON on the game thread, so on a
//! busy or map-loading server the reply can come late or not at all while the command has
//! already happened. The log tail notes every `Rcon:` line here; the registry asks whether the
//! command it just sent has appeared since it sent it.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::text::case::fold_case;
use pavlov_core::time::{Clock, SystemClock};

const RETENTION: TimeDelta = TimeDelta::minutes(2);
const POLL_INTERVAL: Duration = Duration::from_millis(250);
const SWEEP_ABOVE: usize = 512;

/// The commands the logs have shown running, and when.
pub struct RconConfirmations {
    executed: Mutex<HashMap<String, DateTime<Utc>>>,
    clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for RconConfirmations {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RconConfirmations").finish_non_exhaustive()
    }
}

impl Default for RconConfirmations {
    fn default() -> Self {
        Self::new(Arc::new(SystemClock))
    }
}

/// Runs of whitespace collapsed, and trimmed: the log may render a command's spacing
/// differently than the bot sent it, and a bit code carries an internal space of its own.
fn normalize(command: &str) -> String {
    command.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn key(log_file: Option<&str>, command: &str) -> String {
    format!(
        "{}\n{}",
        log_file.unwrap_or("*"),
        fold_case(&normalize(command))
    )
}

impl RconConfirmations {
    #[must_use]
    pub fn new(clock: Arc<dyn Clock>) -> Self {
        Self {
            executed: Mutex::new(HashMap::new()),
            clock,
        }
    }

    /// The log showed `command` run (on `log_file`'s server, when known).
    pub fn note(&self, command: &str, log_file: Option<&str>) {
        if command.trim().is_empty() {
            return;
        }
        let now = self.clock.now();
        let mut executed = self.executed.lock().unwrap_or_else(PoisonError::into_inner);
        executed.insert(key(None, command), now);
        // Also by log, so a command sent to every server is confirmed per server.
        if let Some(file) = log_file.filter(|f| !f.is_empty()) {
            executed.insert(key(Some(file), command), now);
        }
        // Swept on the write path, which is the only place the map grows.
        if executed.len() > SWEEP_ABOVE {
            let cutoff = now - RETENTION;
            executed.retain(|_, at| *at >= cutoff);
        }
    }

    /// Whether `command` has appeared at or after `since`.
    #[must_use]
    pub fn confirmed_since(
        &self,
        log_file: Option<&str>,
        command: &str,
        since: DateTime<Utc>,
    ) -> bool {
        !command.trim().is_empty()
            && self
                .executed
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .get(&key(log_file, command))
                .is_some_and(|at| *at >= since)
    }

    /// Wait up to `within` for `command` to appear at or after `since`.
    pub async fn confirmed(
        &self,
        log_file: Option<&str>,
        command: &str,
        since: DateTime<Utc>,
        within: Duration,
    ) -> bool {
        let deadline = self.clock.now() + TimeDelta::from_std(within).unwrap_or(TimeDelta::MAX);
        loop {
            if self.confirmed_since(log_file, command, since) {
                return true;
            }
            if self.clock.now() >= deadline {
                return false;
            }
            tokio::time::sleep(POLL_INTERVAL).await;
        }
    }
}
