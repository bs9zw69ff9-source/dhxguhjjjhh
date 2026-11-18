//! Port of `CrashRecoveryTests.cs`: restarting a FAILED unit, within limits.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};

use chrono::TimeDelta;
use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::time::ManualClock;
use pavlov_host::crash_recovery::{ATTEMPT_WINDOW, COOLDOWN, CrashRecovery, MAX_ATTEMPTS};
use pavlov_host::service_control::{UnitAction, UnitControl, UnitResult, UnitState};

#[derive(Default)]
struct FakeUnits {
    units: Vec<String>,
    states: Mutex<HashMap<String, UnitState>>,
    restarts: Mutex<Vec<String>>,
    restart_fails: HashSet<String>,
    restart_heals: bool,
}

impl FakeUnits {
    fn new(units: &[&str]) -> Self {
        Self {
            units: units.iter().map(|u| (*u).to_owned()).collect(),
            ..Self::default()
        }
    }

    fn set(&self, unit: &str, state: UnitState) {
        self.states.lock().unwrap().insert(unit.to_owned(), state);
    }

    fn restarts(&self) -> Vec<String> {
        self.restarts.lock().unwrap().clone()
    }
}

impl UnitControl for FakeUnits {
    fn units(&self) -> &[String] {
        &self.units
    }

    fn state<'a>(&'a self, unit: &'a str) -> BoxFuture<'a, UnitState> {
        let state = self
            .states
            .lock()
            .unwrap()
            .get(unit)
            .copied()
            .unwrap_or(UnitState::Active);
        async move { state }.boxed()
    }

    fn run<'a>(&'a self, unit: &'a str, _action: UnitAction) -> BoxFuture<'a, UnitResult> {
        self.restarts.lock().unwrap().push(unit.to_owned());
        let result = if self.restart_fails.contains(unit) {
            UnitResult::new(false, unit, "job failed")
        } else {
            if self.restart_heals {
                self.set(unit, UnitState::Active);
            }
            UnitResult::new(true, unit, "restart ok")
        };
        async move { result }.boxed()
    }
}

struct Fixture {
    units: Arc<FakeUnits>,
    clock: Arc<ManualClock>,
    recovery: CrashRecovery,
}

fn build(units: FakeUnits) -> Fixture {
    let units = Arc::new(units);
    let clock = Arc::new(ManualClock::at_now());
    let recovery = CrashRecovery::new(Arc::clone(&units) as Arc<_>, Arc::clone(&clock) as Arc<_>);
    Fixture {
        units,
        clock,
        recovery,
    }
}

fn failed(units: &[&str], unit: &str) -> FakeUnits {
    let fake = FakeUnits::new(units);
    fake.set(unit, UnitState::Failed);
    fake
}

fn past_cooldown() -> TimeDelta {
    COOLDOWN + TimeDelta::seconds(1)
}

#[tokio::test]
async fn a_failed_unit_is_restarted() {
    let f = build(failed(&["pavlovserver"], "pavlovserver"));
    let sweep = f.recovery.sweep().await;
    assert_eq!(sweep.restarted, ["pavlovserver"]);
    assert_eq!(f.units.restarts(), ["pavlovserver"]);
}

#[tokio::test]
async fn only_the_failed_unit_is_touched() {
    let f = build(failed(
        &["pavlovserver", "pavlovserver1", "pavlovserver2"],
        "pavlovserver1",
    ));
    let sweep = f.recovery.sweep().await;
    assert_eq!(sweep.restarted, ["pavlovserver1"]);
    assert_eq!(f.units.restarts().len(), 1);
}

#[tokio::test]
async fn nothing_but_failed_is_restarted() {
    // INACTIVE is somebody's /serverswitch stop; UNKNOWN is not evidence of anything.
    for state in [
        UnitState::Inactive,
        UnitState::Unknown,
        UnitState::Active,
        UnitState::Starting,
    ] {
        let fake = FakeUnits::new(&["pavlovserver"]);
        fake.set("pavlovserver", state);
        let f = build(fake);
        let sweep = f.recovery.sweep().await;
        assert!(f.units.restarts().is_empty(), "{state:?}");
        assert!(!sweep.did_anything());
    }
}

#[tokio::test]
async fn it_gives_up_after_the_attempt_cap() {
    let f = build(failed(&["pavlovserver"], "pavlovserver"));
    for _ in 0..MAX_ATTEMPTS {
        f.recovery.sweep().await;
        f.clock.advance(past_cooldown());
    }
    assert_eq!(f.units.restarts().len(), MAX_ATTEMPTS as usize);

    let after_cap = f.recovery.sweep().await;
    assert_eq!(f.units.restarts().len(), MAX_ATTEMPTS as usize); // no fourth attempt
    assert!(after_cap.suppressed["pavlovserver"].contains("human"));
}

#[tokio::test]
async fn giving_up_is_announced_once_rather_than_every_sweep() {
    let f = build(failed(&["pavlovserver"], "pavlovserver"));
    for _ in 0..MAX_ATTEMPTS {
        f.recovery.sweep().await;
        f.clock.advance(past_cooldown());
    }
    let first = f.recovery.sweep().await;
    let second = f.recovery.sweep().await;
    let third = f.recovery.sweep().await;

    assert!(!first.suppressed.is_empty());
    assert!(second.suppressed.is_empty());
    assert!(third.suppressed.is_empty());
}

#[tokio::test]
async fn the_cooldown_stops_two_restarts_in_a_minute() {
    let f = build(failed(&["pavlovserver"], "pavlovserver"));
    f.recovery.sweep().await;
    f.recovery.sweep().await; // inside the cooldown
    f.clock.advance(TimeDelta::seconds(30));
    f.recovery.sweep().await; // still inside it
    assert_eq!(f.units.restarts().len(), 1);

    f.clock.advance(COOLDOWN);
    f.recovery.sweep().await;
    assert_eq!(f.units.restarts().len(), 2);
}

#[tokio::test]
async fn a_cooldown_is_not_reported_as_a_suppression() {
    let f = build(failed(&["pavlovserver"], "pavlovserver"));
    f.recovery.sweep().await;
    let during = f.recovery.sweep().await;
    assert!(during.suppressed.is_empty());
    assert!(during.restarted.is_empty());
}

#[tokio::test]
async fn coming_back_healthy_clears_the_history() {
    let mut fake = failed(&["pavlovserver"], "pavlovserver");
    fake.restart_heals = true;
    let f = build(fake);

    f.recovery.sweep().await;
    assert_eq!(f.recovery.attempts_for("pavlovserver"), 1);
    f.recovery.sweep().await; // healthy now - history cleared
    assert_eq!(f.recovery.attempts_for("pavlovserver"), 0);

    f.clock.advance(TimeDelta::hours(2));
    f.units.set("pavlovserver", UnitState::Failed);
    f.recovery.sweep().await;
    assert_eq!(f.units.restarts().len(), 2);
}

#[tokio::test]
async fn attempts_outside_the_window_stop_counting() {
    let f = build(failed(&["pavlovserver"], "pavlovserver"));
    f.recovery.sweep().await;
    f.clock.advance(ATTEMPT_WINDOW + TimeDelta::minutes(1));
    assert_eq!(f.recovery.attempts_for("pavlovserver"), 0);
}

#[tokio::test]
async fn a_failed_restart_is_reported_rather_than_counted_as_success() {
    let mut fake = failed(&["pavlovserver"], "pavlovserver");
    fake.restart_fails.insert("pavlovserver".into());
    let f = build(fake);

    let sweep = f.recovery.sweep().await;
    assert!(sweep.restarted.is_empty());
    assert!(sweep.suppressed["pavlovserver"].contains("job failed"));
}

#[tokio::test]
async fn a_failed_restart_still_counts_toward_the_cap() {
    let mut fake = failed(&["pavlovserver"], "pavlovserver");
    fake.restart_fails.insert("pavlovserver".into());
    let f = build(fake);
    for _ in 0..MAX_ATTEMPTS + 2 {
        f.recovery.sweep().await;
        f.clock.advance(past_cooldown());
    }
    assert_eq!(f.units.restarts().len(), MAX_ATTEMPTS as usize);
}
