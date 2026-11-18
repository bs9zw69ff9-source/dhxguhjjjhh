//! Port of `ServerHealthTests.cs`: the monitoring decision as pure data - no server, no
//! gateway. This is the piece whose being wrong is silent.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::Duration;

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use pavlov_core::monitoring::{
    HealthProbe, HealthState, LatencyBand, MonitorSettings, MonitorSignal, RestartKind,
    ServerHealth, SignalKind,
};

fn s() -> MonitorSettings {
    MonitorSettings::default()
}

fn at(seconds: i64) -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 1, 12, 0, 0).unwrap() + TimeDelta::seconds(seconds)
}

struct Ok_ {
    players: Option<u32>,
    max: Option<u32>,
    latency_ms: u64,
    process: Option<bool>,
    log: Option<DateTime<Utc>>,
}

impl Default for Ok_ {
    fn default() -> Self {
        Self {
            players: Some(20),
            max: Some(50),
            latency_ms: 50,
            process: Some(true),
            log: None,
        }
    }
}

fn ok_with(seconds: i64, o: Ok_) -> HealthProbe {
    HealthProbe {
        rcon_ok: true,
        latency: Some(Duration::from_millis(o.latency_ms)),
        players: o.players,
        max_players: o.max,
        map: Some("Datacenter".into()),
        process_running: o.process,
        intentionally_stopped: false,
        last_log_activity: Some(o.log.unwrap_or_else(|| at(seconds))),
        failure_reason: None,
        at: at(seconds),
    }
}

fn ok(seconds: i64) -> HealthProbe {
    ok_with(seconds, Ok_::default())
}

fn fail_with(seconds: i64, intentional: bool, process: Option<bool>) -> HealthProbe {
    HealthProbe {
        rcon_ok: false,
        latency: None,
        players: None,
        max_players: None,
        map: None,
        process_running: process,
        intentionally_stopped: intentional,
        last_log_activity: None,
        failure_reason: Some("rcon timeout".into()),
        at: at(seconds),
    }
}

fn fail(seconds: i64) -> HealthProbe {
    fail_with(seconds, false, None)
}

fn run(start: &ServerHealth, probes: &[HealthProbe]) -> (ServerHealth, Vec<MonitorSignal>) {
    let mut health = start.clone();
    let mut all = Vec::new();
    for probe in probes {
        let (next, signals) = health.observe(probe, &s());
        health = next;
        all.extend(signals);
    }
    (health, all)
}

fn kinds(signals: &[MonitorSignal]) -> Vec<SignalKind> {
    signals.iter().map(|s| s.kind).collect()
}

// ---- baseline / bot restart ----

#[test]
fn starts_unknown_and_a_first_healthy_probe_does_not_alert() {
    // Bot restart: UNKNOWN, and no invented recovery or fault from the first look.
    assert_eq!(ServerHealth::initial().state, HealthState::Unknown);

    let (h, signals) = run(&ServerHealth::initial(), &[ok(0)]);

    assert_eq!(h.state, HealthState::Online);
    assert!(signals.is_empty());
}

// ---- going offline ----

#[test]
fn three_failures_go_offline_with_exactly_one_alert() {
    let (h, signals) = run(
        &ServerHealth::initial(),
        &[ok(0), fail(30), fail(60), fail(90)],
    );

    assert_eq!(h.state, HealthState::Offline);
    assert_eq!(kinds(&signals), [SignalKind::ServerOffline]);
}

#[test]
fn one_failure_is_reconnecting_not_offline() {
    // "Do not immediately declare a server offline because of one failed request."
    let (h, signals) = run(&ServerHealth::initial(), &[ok(0), fail(30)]);

    assert_eq!(h.state, HealthState::Reconnecting);
    assert!(signals.is_empty());
}

#[test]
fn a_transient_failure_that_recovers_never_alerts() {
    let (h, signals) = run(&ServerHealth::initial(), &[ok(0), fail(30), ok(60), ok(90)]);

    assert_eq!(h.state, HealthState::Online);
    assert!(signals.is_empty()); // never crossed the threshold, so nobody was paged
}

// ---- recovery ----

#[test]
fn recovery_needs_the_recovery_threshold_then_reports_downtime() {
    let (mid, s1) = run(
        &ServerHealth::initial(),
        &[ok(0), fail(30), fail(60), fail(90)],
    );
    assert_eq!(kinds(&s1), [SignalKind::ServerOffline]);

    // One good probe is not enough (recovery threshold 2).
    let (h1, s2) = mid.observe(&ok(120), &s());
    assert_eq!(h1.state, HealthState::Reconnecting);
    assert!(s2.is_empty());

    let (h2, s3) = h1.observe(&ok(150), &s());
    assert_eq!(h2.state, HealthState::Online);
    assert!(kinds(&s3).contains(&SignalKind::ServerOnline));
    assert!(kinds(&s3).contains(&SignalKind::UnexpectedRestart)); // nobody asked for it
}

// ---- repeated RCON failure (process up) ----

#[test]
fn process_up_but_rcon_failing_is_an_rcon_fault_not_an_offline() {
    let (h, signals) = run(
        &ServerHealth::initial(),
        &[
            ok(0),
            fail_with(30, false, Some(true)),
            fail_with(60, false, Some(true)),
            fail_with(90, false, Some(true)),
        ],
    );

    assert_eq!(h.state, HealthState::Offline);
    assert_eq!(kinds(&signals), [SignalKind::RepeatedRconFailures]);

    // ...and recovery of an RCON-only fault is not reported as a restart.
    let (h2, s2) = run(&h, &[ok(120), ok(150)]);
    assert_eq!(h2.state, HealthState::Online);
    assert!(kinds(&s2).contains(&SignalKind::RconReconnected));
    assert!(!kinds(&s2).contains(&SignalKind::UnexpectedRestart));
}

// ---- expected vs unexpected restart ----

#[test]
fn an_intentional_outage_is_not_alerted_and_not_a_crash() {
    let (h, down) = run(
        &ServerHealth::initial(),
        &[
            ok(0),
            fail_with(30, true, None),
            fail_with(60, true, None),
            fail_with(90, true, None),
        ],
    );

    assert_eq!(h.state, HealthState::Offline);
    assert!(down.is_empty()); // intended shutdowns do not page

    let (h2, up) = run(&h, &[ok(120), ok(150)]);
    assert_eq!(h2.last_restart, RestartKind::Expected);
    assert!(!kinds(&up).contains(&SignalKind::UnexpectedRestart));
}

#[test]
fn intentional_is_remembered_even_if_only_one_failing_probe_saw_the_flag() {
    let (h, down) = run(
        &ServerHealth::initial(),
        &[ok(0), fail_with(30, true, None), fail(60), fail(90)],
    );

    assert!(down.is_empty());
    assert!(h.intentional_outage);
}

// ---- alert deduplication / escalation ----

#[test]
fn a_prolonged_outage_alerts_once_then_only_on_escalation() {
    let (mut h, _) = run(&ServerHealth::initial(), &[ok(0)]);

    let mut signals = Vec::new();
    for i in 1..=i64::from(s().escalation_every + 5) {
        let (next, sig) = h.observe(&fail(30 * i), &s());
        h = next;
        signals.extend(sig);
    }

    let offlines = signals
        .iter()
        .filter(|x| x.kind == SignalKind::ServerOffline)
        .count();
    let escalations = signals
        .iter()
        .filter(|x| x.kind == SignalKind::StillOffline)
        .count();

    assert_eq!(offlines, 1); // never repeated
    assert!(escalations >= 1); // but did escalate once it dragged on
    assert!(escalations < 3); // and did not turn into a flood
}

// ---- latency ----

#[test]
fn latency_bands_are_classified_at_the_configured_thresholds() {
    let ms = |n| Some(Duration::from_millis(n));
    assert_eq!(s().band_of(ms(249)), LatencyBand::Healthy);
    assert_eq!(s().band_of(ms(250)), LatencyBand::Degraded);
    assert_eq!(s().band_of(ms(749)), LatencyBand::Degraded);
    assert_eq!(s().band_of(ms(750)), LatencyBand::Critical);
    assert_eq!(s().band_of(None), LatencyBand::Unknown);
}

#[test]
fn high_latency_alerts_once_per_band_change_and_recovers() {
    let probe = |seconds, latency_ms| {
        ok_with(
            seconds,
            Ok_ {
                latency_ms,
                ..Ok_::default()
            },
        )
    };
    let (h, signals) = run(
        &ServerHealth::initial(),
        &[
            probe(0, 50),   // healthy
            probe(30, 300), // -> degraded: one alert
            probe(60, 320), // still degraded: silent
            probe(90, 900), // -> critical: one alert
            probe(120, 40), // -> healthy: recovered
        ],
    );

    assert_eq!(h.state, HealthState::Online);
    assert_eq!(
        kinds(&signals),
        [
            SignalKind::HighLatency,
            SignalKind::HighLatency,
            SignalKind::LatencyRecovered
        ]
    );
}

#[test]
fn latency_stats_track_min_max_average() {
    let probe = |seconds, latency_ms| {
        ok_with(
            seconds,
            Ok_ {
                latency_ms,
                ..Ok_::default()
            },
        )
    };
    let (h, _) = run(
        &ServerHealth::initial(),
        &[probe(0, 40), probe(30, 60), probe(60, 50)],
    );

    let ms = Duration::from_millis;
    assert_eq!(h.latency.min, Some(ms(40)));
    assert_eq!(h.latency.max, Some(ms(60)));
    assert_eq!(h.latency.average, Some(ms(50)));
    assert_eq!(h.latency.current, Some(ms(50)));
    assert_eq!(h.latency.samples, 3);
}

// ---- exponential backoff / reconnection ----

#[test]
fn backoff_grows_exponentially_and_is_clamped() {
    let settings = s();
    assert_eq!(settings.backoff_for(1), settings.retry_base_delay);
    assert_eq!(settings.backoff_for(2), settings.retry_base_delay * 2);
    assert_eq!(settings.backoff_for(3), settings.retry_base_delay * 4);
    assert_eq!(settings.backoff_for(100), settings.retry_max_delay); // clamped, no overflow
}

#[test]
fn a_failure_sets_a_reconnect_deadline_that_gates_the_next_attempt() {
    let (h, _) = run(&ServerHealth::initial(), &[ok(0), fail(30)]);

    assert!(h.is_reconnecting());
    assert!(h.next_retry_at.is_some());
    assert!(!h.may_attempt_at(at(30))); // must wait out the back-off
    assert!(h.may_attempt_at(at(30) + TimeDelta::from_std(s().retry_max_delay).unwrap()));
}

// ---- log inactivity ----

#[test]
fn log_inactivity_alerts_once_while_the_server_still_answers() {
    let stale = Some(at(0)); // a log timestamp that never advances
    let probe = |seconds| {
        ok_with(
            seconds,
            Ok_ {
                log: stale,
                ..Ok_::default()
            },
        )
    };
    let (h, signals) = run(
        &ServerHealth::initial(),
        &[probe(0), probe(600), probe(630)],
    );

    assert_eq!(kinds(&signals), [SignalKind::LogInactive]);
    assert!(h.log_inactive_reported);
}

#[test]
fn log_activity_resuming_re_arms_without_spam() {
    let stale = Some(at(0));
    let probe = |seconds, log| {
        ok_with(
            seconds,
            Ok_ {
                log,
                ..Ok_::default()
            },
        )
    };
    let (mid, s1) = run(
        &ServerHealth::initial(),
        &[probe(0, stale), probe(600, stale)],
    );
    assert_eq!(kinds(&s1), [SignalKind::LogInactive]);

    let (after, s2) = run(&mid, &[probe(630, Some(at(625)))]); // the log moved again
    assert!(!after.log_inactive_reported);
    assert!(s2.is_empty()); // no "log recovered" noise
}

// ---- player anomaly ----

fn players(seconds: i64, players: u32) -> HealthProbe {
    ok_with(
        seconds,
        Ok_ {
            players: Some(players),
            ..Ok_::default()
        },
    )
}

#[test]
fn a_sudden_drop_to_zero_while_answering_is_flagged() {
    let (_, signals) = run(&ServerHealth::initial(), &[players(0, 27), players(30, 0)]);
    assert_eq!(kinds(&signals), [SignalKind::PlayerCountZero]);
}

#[test]
fn a_big_drop_short_of_zero_is_an_anomaly_not_a_zero() {
    let (_, signals) = run(&ServerHealth::initial(), &[players(0, 45), players(30, 2)]);
    assert_eq!(kinds(&signals), [SignalKind::PlayerAnomaly]);
}

#[test]
fn a_rise_after_a_restart_is_not_a_drop() {
    // 0 -> 40 immediately after a restart is a recovery, not an anomaly.
    let (down, _) = run(
        &ServerHealth::initial(),
        &[players(0, 30), fail(30), fail(60), fail(90)],
    );
    let (_, up) = run(&down, &[players(120, 0), players(150, 40)]);

    assert!(!kinds(&up).contains(&SignalKind::PlayerAnomaly));
    assert!(!kinds(&up).contains(&SignalKind::PlayerCountZero));
}

#[test]
fn players_going_dark_during_an_outage_is_not_a_player_anomaly() {
    let (_, signals) = run(
        &ServerHealth::initial(),
        &[players(0, 27), fail(30), fail(60), fail(90)],
    );

    assert!(!kinds(&signals).contains(&SignalKind::PlayerAnomaly));
    assert!(!kinds(&signals).contains(&SignalKind::PlayerCountZero));
}

#[test]
fn reaching_capacity_alerts_once_then_re_arms_when_it_drops() {
    let probe = |seconds, players| {
        ok_with(
            seconds,
            Ok_ {
                players: Some(players),
                max: Some(50),
                ..Ok_::default()
            },
        )
    };
    let (_, signals) = run(
        &ServerHealth::initial(),
        &[
            probe(0, 49),
            probe(30, 50),
            probe(60, 50),
            probe(90, 48),
            probe(120, 50),
        ],
    );

    assert_eq!(
        kinds(&signals),
        [
            SignalKind::PlayerCapacityReached,
            SignalKind::PlayerCapacityReached
        ]
    );
}

// ---- multiple servers ----

#[test]
fn servers_are_independent() {
    let (a, _) = run(
        &ServerHealth::initial(),
        &[ok(0), fail(30), fail(60), fail(90)],
    );
    let (b, b_signals) = run(&ServerHealth::initial(), &[ok(0), ok(30), ok(60)]);

    assert_eq!(a.state, HealthState::Offline);
    assert_eq!(b.state, HealthState::Online);
    assert!(b_signals.is_empty());
}
