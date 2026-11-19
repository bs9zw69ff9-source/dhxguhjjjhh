//! The monitoring decision, as a pure state machine (C# `Core/Monitoring/ServerHealth.cs`).
//!
//! THE RELIABILITY OF THE WHOLE FEATURE LIVES HERE, so it is a value with no I/O.
//! [`ServerHealth::observe`] takes one probe and the settings and returns the next state plus
//! whatever alerts the transition warrants. The loop that owns sockets and Discord is thin glue
//! around it.
//!
//! DEDUPLICATION IS INTRINSIC: a signal is produced only on a genuine CHANGE, so a server down
//! for ten minutes yields one OFFLINE and then silence until it recovers or crosses the
//! escalation cadence. There is no path that emits per tick.
//!
//! UNKNOWN IS THE START STATE. After a bot restart the first probe establishes reality; a first
//! success is not a "recovery" and does not alert.

use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};

use super::{
    HealthProbe, HealthState, LatencyBand, LatencyStats, MonitorSettings, MonitorSignal,
    RestartKind, Severity, SignalKind,
};
use crate::text::number::group_thousands;

/// One server's monitored state. Each server is its own value.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ServerHealth {
    pub state: HealthState,
    pub consecutive_failures: u32,
    pub consecutive_successes: u32,
    pub latency: LatencyStats,
    /// The latency band last alerted about, so a steady band does not re-alert.
    pub reported_band: LatencyBand,
    pub last_success_at: Option<DateTime<Utc>>,
    pub last_probe_at: Option<DateTime<Utc>>,

    // ---- outage bookkeeping ----
    pub outage_start: Option<DateTime<Utc>>,
    pub intentional_outage: bool,
    pub declared_offline: bool,
    /// The confirmed outage is "process up, RCON dead" rather than the box being down.
    pub rcon_only_outage: bool,
    pub checks_while_offline: u32,

    // ---- reconnection pacing ----
    pub reconnect_attempts: u32,
    pub next_retry_at: Option<DateTime<Utc>>,

    // ---- last-known facts, for the dashboard ----
    pub players: Option<u32>,
    pub max_players: Option<u32>,
    pub map: Option<String>,
    pub process_running: Option<bool>,
    pub last_log_activity: Option<DateTime<Utc>>,

    // ---- anomaly, capacity and log dedup ----
    pub prev_players: Option<u32>,
    pub prev_players_at: Option<DateTime<Utc>>,
    pub at_capacity_reported: bool,
    pub log_inactive_reported: bool,

    pub ever_online: bool,
    pub last_restart: RestartKind,
}

/// A settings duration as a span between two instants.
fn span(duration: Duration) -> TimeDelta {
    TimeDelta::from_std(duration).unwrap_or(TimeDelta::MAX)
}

fn ms(t: Duration) -> String {
    format!(
        "{}ms",
        group_thousands((t.as_secs_f64() * 1000.0).round() as i64)
    )
}

/// A compact duration: "2m 41s", "3h 5m", "12s".
#[must_use]
pub fn humanize(span: TimeDelta) -> String {
    let span = span.max(TimeDelta::zero());
    let hours = span.num_hours();
    let minutes = span.num_minutes() % 60;
    let seconds = span.num_seconds() % 60;
    if hours >= 1 {
        format!("{hours}h {minutes}m")
    } else if span.num_minutes() >= 1 {
        format!("{minutes}m {seconds}s")
    } else {
        format!("{seconds}s")
    }
}

impl ServerHealth {
    /// The state before anything has been probed.
    #[must_use]
    pub fn initial() -> Self {
        Self::default()
    }

    /// A reconnect back-off is pending, so the loop can pace attempts.
    #[must_use]
    pub fn is_reconnecting(&self) -> bool {
        matches!(self.state, HealthState::Reconnecting | HealthState::Offline)
    }

    /// Whether an attempt may run now, honouring the back-off the last failure set.
    #[must_use]
    pub fn may_attempt_at(&self, now: DateTime<Utc>) -> bool {
        self.next_retry_at.is_none_or(|next| now >= next)
    }

    /// Fold one probe in: the next state, and any alerts it warrants.
    #[must_use]
    pub fn observe(
        &self,
        probe: &HealthProbe,
        settings: &MonitorSettings,
    ) -> (Self, Vec<MonitorSignal>) {
        let mut signals = Vec::new();
        let next = if probe.rcon_ok {
            self.on_success(probe, settings, &mut signals)
        } else {
            self.on_failure(probe, settings, &mut signals)
        };
        (next, signals)
    }

    fn healthy_or_degraded(band: LatencyBand) -> HealthState {
        if band == LatencyBand::Healthy {
            HealthState::Online
        } else {
            HealthState::Degraded
        }
    }

    // ------------------------------------------------------------------ success

    fn on_success(
        &self,
        probe: &HealthProbe,
        settings: &MonitorSettings,
        signals: &mut Vec<MonitorSignal>,
    ) -> Self {
        let band = settings.band_of(probe.latency);
        let mut next = Self {
            last_probe_at: Some(probe.at),
            last_success_at: Some(probe.at),
            consecutive_successes: self.consecutive_successes + 1,
            consecutive_failures: 0,
            latency: probe.latency.map_or(self.latency, |l| self.latency.with(l)),
            players: probe.players.or(self.players),
            max_players: probe.max_players.or(self.max_players),
            map: probe.map.clone().or_else(|| self.map.clone()),
            process_running: probe.process_running.or(self.process_running),
            last_log_activity: probe.last_log_activity.or(self.last_log_activity),
            ever_online: true,
            ..self.clone()
        };

        // Coming back from an outage.
        if matches!(self.state, HealthState::Offline | HealthState::Reconnecting) {
            // A blip that never crossed the threshold was never announced, so its recovery is
            // announced to nobody either.
            if !self.declared_offline {
                return Self::restore_quietly(next, probe, band);
            }
            // A confirmed outage needs several clean probes, so one lucky reply does not flap.
            if next.consecutive_successes < settings.recovery_threshold.max(1) {
                next.state = HealthState::Reconnecting;
                return next;
            }
            return self.recover(next, probe, settings, band, signals);
        }

        // Steady state: latency, players and log activity, for changes worth a word.
        self.report_latency(band, &next, signals);
        next = self.check_players(next, probe, settings, signals);
        next = self.check_log_activity(next, probe, settings, signals);
        next.state = Self::healthy_or_degraded(band);
        next.reported_band = band;
        next
    }

    /// Return from an undeclared blip with no alert, resetting the outage bookkeeping.
    fn restore_quietly(mut next: Self, probe: &HealthProbe, band: LatencyBand) -> Self {
        next.state = Self::healthy_or_degraded(band);
        next.reported_band = band;
        next.outage_start = None;
        next.intentional_outage = false;
        next.reconnect_attempts = 0;
        next.next_retry_at = None;
        next.checks_while_offline = 0;
        // Reset the anomaly baseline, so a reading from before the blip is not compared to one
        // after it.
        next.prev_players = next.players;
        next.prev_players_at = Some(probe.at);
        next.log_inactive_reported = false;
        next
    }

    fn recover(
        &self,
        mut next: Self,
        probe: &HealthProbe,
        settings: &MonitorSettings,
        band: LatencyBand,
        signals: &mut Vec<MonitorSignal>,
    ) -> Self {
        let downtime = self
            .outage_start
            .map_or(TimeDelta::zero(), |start| probe.at - start);
        let latency_text = probe
            .latency
            .map_or_else(String::new, |l| format!(", RCON latency {}", ms(l)));
        let players = next.players.map_or_else(String::new, |p| {
            format!(
                ", players {p}{}",
                next.max_players
                    .map_or_else(String::new, |m| format!("/{m}"))
            )
        });

        let restart = if self.declared_offline && !self.intentional_outage && !self.rcon_only_outage
        {
            // The box was down and came back, and nobody asked for it.
            signals.push(MonitorSignal::new(
                SignalKind::UnexpectedRestart,
                Severity::Warning,
                format!(
                    "Came back after an unexplained outage of {}. Looks like a crash/restart, not a planned one.",
                    humanize(downtime)
                ),
            ));
            RestartKind::Unexpected
        } else if self.intentional_outage {
            RestartKind::Expected
        } else if self.declared_offline {
            RestartKind::Unknown
        } else {
            RestartKind::None
        };

        if settings.recovery_messages_enabled {
            let kind = if self.rcon_only_outage {
                SignalKind::RconReconnected
            } else {
                SignalKind::ServerOnline
            };
            signals.push(MonitorSignal::new(
                kind,
                Severity::Info,
                format!(
                    "Back online after {}{latency_text}{players}.",
                    humanize(downtime)
                ),
            ));
        }

        next.state = Self::healthy_or_degraded(band);
        next.reported_band = band;
        next.outage_start = None;
        next.intentional_outage = false;
        next.declared_offline = false;
        next.rcon_only_outage = false;
        next.checks_while_offline = 0;
        next.reconnect_attempts = 0;
        next.next_retry_at = None;
        next.last_restart = restart;
        // A restart resets the anomaly baseline: 0 -> 40 right after a restart is a recovery.
        next.prev_players = next.players;
        next.prev_players_at = Some(probe.at);
        next.at_capacity_reported =
            matches!((next.max_players, next.players), (Some(m), Some(p)) if p >= m);
        next.log_inactive_reported = false;
        next
    }

    fn report_latency(&self, band: LatencyBand, next: &Self, signals: &mut Vec<MonitorSignal>) {
        let current = next.latency.current.unwrap_or_default();
        let raised = matches!(
            self.reported_band,
            LatencyBand::Degraded | LatencyBand::Critical
        );
        if matches!(band, LatencyBand::Degraded | LatencyBand::Critical)
            && band != self.reported_band
        {
            let severity = if band == LatencyBand::Critical {
                Severity::Critical
            } else {
                Severity::Warning
            };
            signals.push(MonitorSignal::new(
                SignalKind::HighLatency,
                severity,
                format!(
                    "RCON latency is {} at {} (avg {}).",
                    band.upper(),
                    ms(current),
                    ms(next.latency.average.unwrap_or_default())
                ),
            ));
        } else if band == LatencyBand::Healthy && raised {
            signals.push(MonitorSignal::new(
                SignalKind::LatencyRecovered,
                Severity::Info,
                format!("RCON latency back to healthy at {}.", ms(current)),
            ));
        }
    }

    fn check_players(
        &self,
        mut next: Self,
        probe: &HealthProbe,
        settings: &MonitorSettings,
        signals: &mut Vec<MonitorSignal>,
    ) -> Self {
        let Some(now) = probe.players else {
            return next;
        };
        let max = probe.max_players.or(next.max_players);

        // Capacity: alert once on the way up, re-arm when it drops below.
        let at_capacity = max.is_some_and(|cap| cap > 0 && now >= cap);
        if at_capacity && !self.at_capacity_reported {
            signals.push(MonitorSignal::new(
                SignalKind::PlayerCapacityReached,
                Severity::Info,
                format!(
                    "Full: {now}/{} players.",
                    max.map_or_else(String::new, |m| m.to_string())
                ),
            ));
        }

        // A sudden drop while RCON still answers (this is the success path), so it is a real
        // drop, not the count going dark in an outage.
        if let (Some(prev), Some(prev_at)) = (self.prev_players, self.prev_players_at)
            && prev > 0
            && probe.at - prev_at <= span(settings.player_anomaly_window)
            && prev > now
        {
            let dropped = prev - now;
            if f64::from(dropped) / f64::from(prev) >= settings.player_anomaly_drop_fraction {
                signals.push(if now == 0 {
                        MonitorSignal::new(
                            SignalKind::PlayerCountZero,
                            Severity::Warning,
                            format!(
                                "Players fell from {prev} to 0 while the server was still answering RCON - worth a look."
                            ),
                        )
                    } else {
                        MonitorSignal::new(
                            SignalKind::PlayerAnomaly,
                            Severity::Warning,
                            format!("Players dropped sharply, {prev} to {now}, while RCON stayed up."),
                        )
                    });
            }
        }

        next.prev_players = Some(now);
        next.prev_players_at = Some(probe.at);
        next.at_capacity_reported = at_capacity;
        next
    }

    fn check_log_activity(
        &self,
        mut next: Self,
        probe: &HealthProbe,
        settings: &MonitorSettings,
        signals: &mut Vec<MonitorSignal>,
    ) -> Self {
        let Some(last) = probe.last_log_activity else {
            return next;
        };
        let quiet_for = probe.at - last;
        if quiet_for >= span(settings.log_inactivity_threshold) {
            if !self.log_inactive_reported {
                signals.push(MonitorSignal::new(
                    SignalKind::LogInactive,
                    Severity::Warning,
                    format!(
                        "The server answers RCON but its log has not advanced for {} - it may be hung.",
                        humanize(quiet_for)
                    ),
                ));
                next.log_inactive_reported = true;
            }
            return next;
        }
        // Activity resumed: re-arm quietly, no "recovered" spam for a log going quiet.
        next.log_inactive_reported = false;
        next
    }

    // ------------------------------------------------------------------ failure

    fn on_failure(
        &self,
        probe: &HealthProbe,
        settings: &MonitorSettings,
        signals: &mut Vec<MonitorSignal>,
    ) -> Self {
        let failures = self.consecutive_failures + 1;
        let first_of_outage = matches!(
            self.state,
            HealthState::Online | HealthState::Degraded | HealthState::Unknown
        );
        let outage_start = if first_of_outage {
            probe.at
        } else {
            self.outage_start.unwrap_or(probe.at)
        };
        let intentional = self.intentional_outage || probe.intentionally_stopped;

        let mut next = Self {
            last_probe_at: Some(probe.at),
            consecutive_failures: failures,
            consecutive_successes: 0,
            outage_start: Some(outage_start),
            intentional_outage: intentional,
            process_running: probe.process_running.or(self.process_running),
            reconnect_attempts: self.reconnect_attempts + 1,
            next_retry_at: Some(probe.at + span(settings.backoff_for(self.reconnect_attempts + 1))),
            ..self.clone()
        };

        // Below the threshold: RECONNECTING, and silent. One failed request never pages anyone.
        if failures < settings.failure_threshold.max(1) {
            next.state = HealthState::Reconnecting;
            return next;
        }

        // At the threshold, and not already declared: confirm the outage, once.
        if !self.declared_offline {
            let rcon_only = probe.process_running == Some(true);
            if !intentional {
                signals.push(if rcon_only {
                    MonitorSignal::new(
                        SignalKind::RepeatedRconFailures,
                        Severity::Critical,
                        format!(
                            "{failures} RCON checks failed in a row. The process appears to be up, so RCON itself is the fault. Last reply {}.",
                            self.last_seen(probe.at)
                        ),
                    )
                } else {
                    MonitorSignal::new(
                        SignalKind::ServerOffline,
                        Severity::Critical,
                        format!(
                            "{failures} checks failed in a row. Last successful RCON {}. Last known players: {}.",
                            self.last_seen(probe.at),
                            self.last_players()
                        ),
                    )
                });
            }
            next.state = HealthState::Offline;
            next.declared_offline = true;
            next.rcon_only_outage = rcon_only;
            next.checks_while_offline = 0;
            return next;
        }

        // Already OFFLINE: quiet, except one escalation every N further failures.
        let checks_while_offline = self.checks_while_offline + 1;
        if !intentional
            && settings.escalation_every > 0
            && checks_while_offline.is_multiple_of(settings.escalation_every)
        {
            let down_for = self
                .outage_start
                .map_or(TimeDelta::zero(), |s| probe.at - s);
            signals.push(MonitorSignal::new(
                SignalKind::StillOffline,
                Severity::Warning,
                format!(
                    "Still down after {} ({failures} failed checks).",
                    humanize(down_for)
                ),
            ));
        }
        next.state = HealthState::Offline;
        next.checks_while_offline = checks_while_offline;
        next
    }

    // ------------------------------------------------------------------ text

    fn last_seen(&self, now: DateTime<Utc>) -> String {
        self.last_success_at.map_or_else(
            || "never this session".to_owned(),
            |at| format!("{} ago", humanize(now - at)),
        )
    }

    fn last_players(&self) -> String {
        self.players.map_or_else(
            || "unknown".to_owned(),
            |p| {
                format!(
                    "{p}{}",
                    self.max_players
                        .map_or_else(String::new, |m| format!("/{m}"))
                )
            },
        )
    }
}
