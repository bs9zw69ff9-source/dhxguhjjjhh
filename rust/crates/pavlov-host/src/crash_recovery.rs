//! Restarting a game server that systemd reports FAILED, within limits (C#
//! `Servers/CrashRecovery.cs`).
//!
//! At most [`MAX_ATTEMPTS`] restarts in [`ATTEMPT_WINDOW`], then it stops and says so ONCE -
//! something is wrong that restarting does not fix. A cooldown between attempts lets a server
//! that takes a minute to come up finish starting. Coming back healthy clears the history.

use std::collections::{BTreeMap, HashMap};
use std::sync::{Arc, Mutex, PoisonError};

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::servers::crash_recovery::{ATTEMPT_WINDOW as WINDOW, MAX_ATTEMPTS as MAX};
use pavlov_core::time::Clock;

use crate::service_control::{UnitAction, UnitControl, UnitState};

/// Restarts allowed in the window.
pub const MAX_ATTEMPTS: u32 = MAX;
/// The window attempts are counted over (the core constant, as a `TimeDelta`).
pub const ATTEMPT_WINDOW: TimeDelta = TimeDelta::seconds(WINDOW.as_secs() as i64);
/// The least time between two attempts.
pub const COOLDOWN: TimeDelta = TimeDelta::minutes(2);

/// What one sweep did.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RecoverySweep {
    pub restarted: Vec<String>,
    /// Unit -> why it was not (or not successfully) restarted. A cooldown is not in here: it
    /// is a normal part of waiting for a server to come up.
    pub suppressed: BTreeMap<String, String>,
}

impl RecoverySweep {
    #[must_use]
    pub fn did_anything(&self) -> bool {
        !self.restarted.is_empty() || !self.suppressed.is_empty()
    }
}

#[derive(Default)]
struct Attempts {
    at: Vec<DateTime<Utc>>,
    gave_up_announced: bool,
}

enum Gate {
    Allow,
    Quiet,
    Report(String),
}

/// The recovery loop's memory.
pub struct CrashRecovery {
    services: Arc<dyn UnitControl>,
    clock: Arc<dyn Clock>,
    attempts: Mutex<HashMap<String, Attempts>>,
}

impl std::fmt::Debug for CrashRecovery {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CrashRecovery").finish_non_exhaustive()
    }
}

impl CrashRecovery {
    #[must_use]
    pub fn new(services: Arc<dyn UnitControl>, clock: Arc<dyn Clock>) -> Self {
        Self {
            services,
            clock,
            attempts: Mutex::new(HashMap::new()),
        }
    }

    /// Restart every FAILED unit the limits allow.
    pub async fn sweep(&self) -> RecoverySweep {
        let mut sweep = RecoverySweep::default();
        let units: Vec<String> = self.services.units().to_vec();
        for unit in units {
            let state = self.services.state(&unit).await;
            if state != UnitState::Failed {
                // Back to healthy: forget its history, so next week's crash is judged alone.
                if state == UnitState::Active {
                    self.forget(&unit);
                }
                continue;
            }
            match self.check(&unit) {
                Gate::Allow => {}
                Gate::Quiet => continue,
                Gate::Report(reason) => {
                    sweep.suppressed.insert(unit, reason);
                    continue;
                }
            }
            tracing::error!(%unit, "unit is in the FAILED state - restarting it");
            self.record_attempt(&unit);
            let result = self.services.run(&unit, UnitAction::Restart).await;
            if result.ok {
                tracing::warn!(%unit, "had crashed and has been restarted automatically");
                sweep.restarted.push(unit);
            } else {
                tracing::error!(%unit, detail = %result.detail, "automatic restart FAILED");
                sweep
                    .suppressed
                    .insert(unit, format!("restart failed: {}", result.detail));
            }
        }
        sweep
    }

    fn check(&self, unit: &str) -> Gate {
        let now = self.clock.now();
        let mut all = self.attempts.lock().unwrap_or_else(PoisonError::into_inner);
        let Some(attempts) = all.get_mut(unit) else {
            return Gate::Allow;
        };
        attempts.at.retain(|at| now - *at <= ATTEMPT_WINDOW);
        let count = attempts.at.len();
        if count >= MAX_ATTEMPTS as usize {
            if attempts.gave_up_announced {
                return Gate::Quiet; // already said; stay quiet
            }
            attempts.gave_up_announced = true;
            let minutes = ATTEMPT_WINDOW.num_minutes();
            tracing::error!(%unit, count, minutes, "AUTOMATIC RESTARTS ARE NOW OFF for this unit - check `systemctl status {unit}` and `journalctl -u {unit}`");
            return Gate::Report(format!(
                "failed {count} times in {minutes} minutes - automatic restarts stopped, this needs a human"
            ));
        }
        // STILL COMING UP: restarting mid-start reports a failure for a unit that was fine.
        if attempts
            .at
            .last()
            .is_some_and(|last| now - *last < COOLDOWN)
        {
            return Gate::Quiet;
        }
        Gate::Allow
    }

    fn record_attempt(&self, unit: &str) {
        let now = self.clock.now();
        self.attempts
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .entry(unit.to_owned())
            .or_default()
            .at
            .push(now);
    }

    fn forget(&self, unit: &str) {
        let removed = self
            .attempts
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .remove(unit);
        if removed.is_some_and(|a| !a.at.is_empty()) {
            tracing::info!(%unit, "healthy again - its crash history has been cleared");
        }
    }

    /// Attempts inside the window, for `/diagnose`.
    #[must_use]
    pub fn attempts_for(&self, unit: &str) -> usize {
        let now = self.clock.now();
        let mut all = self.attempts.lock().unwrap_or_else(PoisonError::into_inner);
        all.get_mut(unit).map_or(0, |a| {
            a.at.retain(|at| now - *at <= ATTEMPT_WINDOW);
            a.at.len()
        })
    }
}
