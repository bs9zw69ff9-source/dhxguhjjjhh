//! Port of `ServiceRegistryTests.cs`: the background services, their ticks, their budgets and
//! their supervisor. Real time with short intervals, as the C# does.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::{Duration, Instant};

use pavlov_host::health::{HealthResult, HealthStatus};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::hold_open;
use pavlov_host::services::registry::{order_by_dependencies, tick_budget};
use pavlov_host::services::{ServiceDefinition, ServiceRegistry, ServiceState, TickError, Work};

fn registry() -> ServiceRegistry {
    ServiceRegistry::new(Arc::new(MetricsRegistry::default()))
}

fn ok() -> Work {
    Arc::new(|| Box::pin(async { Ok(()) }))
}

fn definition(name: &str) -> ServiceDefinition {
    ServiceDefinition::new(name, ok())
}

fn depends(name: &str, on: &[&str]) -> ServiceDefinition {
    ServiceDefinition {
        depends_on: on.iter().map(|s| (*s).to_owned()).collect(),
        ..definition(name)
    }
}

fn every(name: &str, tick: Work, interval_ms: u64) -> ServiceDefinition {
    ServiceDefinition {
        interval: Some(Duration::from_millis(interval_ms)),
        run_on_start: true,
        ..ServiceDefinition::new(name, tick)
    }
}

fn counting(counter: &Arc<AtomicUsize>) -> Work {
    let counter = Arc::clone(counter);
    Arc::new(move || {
        counter.fetch_add(1, Ordering::SeqCst);
        Box::pin(async { Ok(()) })
    })
}

fn hanging() -> Work {
    Arc::new(|| Box::pin(std::future::pending()))
}

async fn wait_until(condition: impl Fn() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(5);
    while !condition() && Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    assert!(condition(), "condition was not met within the timeout");
}

fn only(registry: &ServiceRegistry) -> pavlov_host::services::ServiceStatus {
    registry.status().into_iter().next().unwrap()
}

#[test]
fn dependencies_decide_start_order() {
    let ordered =
        order_by_dependencies(&[depends("c", &["b"]), definition("a"), depends("b", &["a"])])
            .unwrap();
    let names: Vec<&str> = ordered.iter().map(|d| d.name.as_str()).collect();
    assert_eq!(names, ["a", "b", "c"]);
}

#[test]
fn a_cycle_is_reported_with_its_participants() {
    let error = order_by_dependencies(&[depends("a", &["b"]), depends("b", &["a"])]).unwrap_err();
    assert!(error.0.to_lowercase().contains("circular"));
    assert!(error.0.contains('a'));
    assert!(error.0.contains('b'));
}

#[test]
fn an_unknown_dependency_is_reported_not_ignored() {
    let error = order_by_dependencies(&[depends("a", &["ghost"])]).unwrap_err();
    assert!(error.0.contains("unknown service \"ghost\""));
}

#[test]
fn registering_the_same_name_twice_is_refused() {
    let registry = registry();
    registry.register(definition("only-one")).unwrap();
    assert!(registry.register(definition("only-one")).is_err());
}

#[test]
fn the_rcon_session_outlasts_its_own_health_probe() {
    // Nothing sends on an idle session, so the probe is what keeps it open.
    let probe = Duration::from_secs(10 * 60);
    assert!(hold_open(Duration::from_secs(5 * 60), probe) > probe);
}

#[test]
fn a_generous_idle_timeout_is_left_alone() {
    let configured = Duration::from_secs(3600);
    assert_eq!(hold_open(configured, Duration::from_secs(60)), configured);
}

#[test]
fn the_defaults_already_hold_the_connection_open() {
    assert_eq!(
        hold_open(Duration::from_secs(5 * 60), Duration::from_secs(60)),
        Duration::from_secs(5 * 60)
    );
}

#[tokio::test]
async fn a_zero_interval_fails_to_start_instead_of_reporting_running() {
    let registry = registry();
    registry
        .register(ServiceDefinition {
            interval: Some(Duration::ZERO),
            ..definition("broken")
        })
        .unwrap();

    registry.start_all().await.unwrap();

    assert_eq!(only(&registry).state, ServiceState::Failed);
}

#[tokio::test]
async fn a_throwing_tick_is_contained_and_counted() {
    let ticks = Arc::new(AtomicUsize::new(0));
    let seen = Arc::clone(&ticks);
    let registry = registry();
    registry
        .register(every(
            "flaky",
            Arc::new(move || {
                seen.fetch_add(1, Ordering::SeqCst);
                Box::pin(async { Err(TickError::new("nope")) })
            }),
            20,
        ))
        .unwrap();

    registry.start_all().await.unwrap();
    wait_until(|| ticks.load(Ordering::SeqCst) >= 3).await;
    registry.stop_all().await;

    let status = only(&registry);
    assert_eq!(status.state, ServiceState::Stopped);
    assert!(status.failures >= 3);
    // The message AND where it came from: a bare message identified nothing.
    let error = status.last_error.unwrap();
    assert!(error.starts_with("nope"), "{error}");
    assert!(error.contains("service_registry_tests.rs:"), "{error}");
    assert!(!status.errors.is_empty());
}

#[tokio::test]
async fn a_slow_tick_is_never_re_entered() {
    let concurrent = Arc::new(AtomicUsize::new(0));
    let peak = Arc::new(AtomicUsize::new(0));
    let (c, p) = (Arc::clone(&concurrent), Arc::clone(&peak));
    let registry = registry();
    registry
        .register(every(
            "slow",
            Arc::new(move || {
                let (c, p) = (Arc::clone(&c), Arc::clone(&p));
                Box::pin(async move {
                    let now = c.fetch_add(1, Ordering::SeqCst) + 1;
                    p.fetch_max(now, Ordering::SeqCst);
                    tokio::time::sleep(Duration::from_millis(60)).await;
                    c.fetch_sub(1, Ordering::SeqCst);
                    Ok(())
                })
            }),
            10,
        ))
        .unwrap();

    registry.start_all().await.unwrap();
    tokio::time::sleep(Duration::from_millis(300)).await;
    registry.stop_all().await;

    assert_eq!(peak.load(Ordering::SeqCst), 1);
}

fn sleeping(ms: u64) -> Work {
    Arc::new(move || {
        Box::pin(async move {
            tokio::time::sleep(Duration::from_millis(ms)).await;
            Ok(())
        })
    })
}

#[tokio::test]
async fn a_slow_tick_is_counted_as_an_overrun() {
    let registry = registry();
    registry.register(every("slow", sleeping(60), 10)).unwrap();

    registry.start_all().await.unwrap();
    wait_until(|| only(&registry).overruns >= 2).await;
    registry.stop_all().await;

    assert!(only(&registry).overruns >= 2);
}

#[tokio::test]
async fn a_hung_tick_is_abandoned_rather_than_stopping_the_service_forever() {
    let started = Arc::new(AtomicUsize::new(0));
    let seen = Arc::clone(&started);
    let registry = registry();
    registry
        .register(ServiceDefinition {
            timeout: Some(Duration::from_millis(200)),
            ..every(
                "hung",
                Arc::new(move || {
                    seen.fetch_add(1, Ordering::SeqCst);
                    Box::pin(std::future::pending())
                }),
                50,
            )
        })
        .unwrap();

    registry.start_all().await.unwrap();
    // The point: it ticks AGAIN.
    wait_until(|| started.load(Ordering::SeqCst) >= 3).await;
    registry.stop_all().await;

    let status = only(&registry);
    assert!(
        status.failures >= 2,
        "only {} failures recorded",
        status.failures
    );
    assert!(status.last_error.unwrap().contains("budget"));
}

#[tokio::test]
async fn a_hung_tick_counts_towards_revival() {
    let registry = registry();
    registry
        .register(ServiceDefinition {
            timeout: Some(Duration::from_millis(100)),
            ..every("hung", hanging(), 50)
        })
        .unwrap();

    registry.start_all().await.unwrap();
    wait_until(|| only(&registry).consecutive_failures >= 3).await;
    registry.stop_all().await;

    assert!(only(&registry).consecutive_failures >= 3);
}

#[test]
fn the_default_tick_budget_scales_with_the_interval() {
    let plain = definition("x");
    assert_eq!(
        tick_budget(&plain, Duration::from_millis(1500)),
        Duration::from_secs(30)
    );
    assert_eq!(
        tick_budget(&plain, Duration::from_secs(60)),
        Duration::from_secs(5 * 60)
    );
    assert_eq!(
        tick_budget(&plain, Duration::from_secs(3600)),
        Duration::from_secs(10 * 60)
    );

    let explicit = ServiceDefinition {
        timeout: Some(Duration::from_secs(7)),
        ..definition("x")
    };
    assert_eq!(
        tick_budget(&explicit, Duration::from_secs(3600)),
        Duration::from_secs(7)
    );
}

#[tokio::test]
async fn shutdown_is_not_reported_as_a_tick_failure() {
    let registry = registry();
    registry
        .register(ServiceDefinition {
            timeout: Some(Duration::from_secs(300)),
            ..every("slow", hanging(), 50)
        })
        .unwrap();

    registry.start_all().await.unwrap();
    tokio::time::sleep(Duration::from_millis(100)).await;
    registry.stop_all().await;

    assert_eq!(only(&registry).failures, 0);
}

fn failing_start(message: &'static str) -> Work {
    Arc::new(move || Box::pin(async move { Err(TickError::new(message)) }))
}

#[tokio::test]
async fn a_non_critical_service_that_fails_to_start_does_not_abort_startup() {
    let registry = registry();
    registry
        .register(ServiceDefinition {
            on_start: Some(failing_start("no socket")),
            ..definition("broken")
        })
        .unwrap();
    registry
        .register(ServiceDefinition {
            interval: Some(Duration::from_secs(300)),
            ..definition("fine")
        })
        .unwrap();

    registry.start_all().await.unwrap();

    let status = registry.status();
    assert_eq!(
        status.iter().find(|s| s.name == "broken").unwrap().state,
        ServiceState::Failed
    );
    assert_eq!(
        status.iter().find(|s| s.name == "fine").unwrap().state,
        ServiceState::Running
    );
    registry.stop_all().await;
}

#[tokio::test]
async fn a_critical_service_that_fails_to_start_aborts_startup() {
    let registry = registry();
    registry
        .register(ServiceDefinition {
            critical: true,
            on_start: Some(failing_start("no database")),
            ..definition("essential")
        })
        .unwrap();

    assert!(registry.start_all().await.is_err());
}

#[tokio::test]
async fn run_on_start_is_off_by_default() {
    // Services start BEFORE the gateway connects.
    let ticks = Arc::new(AtomicUsize::new(0));
    let registry = registry();
    registry
        .register(ServiceDefinition {
            interval: Some(Duration::from_secs(600)),
            ..ServiceDefinition::new("board", counting(&ticks))
        })
        .unwrap();

    registry.start_all().await.unwrap();
    tokio::time::sleep(Duration::from_millis(50)).await;
    registry.stop_all().await;

    assert_eq!(ticks.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn stopping_is_deterministic_no_tick_runs_after_stop_returns() {
    let ticks = Arc::new(AtomicUsize::new(0));
    let registry = registry();
    registry
        .register(every("counter", counting(&ticks), 10))
        .unwrap();

    registry.start_all().await.unwrap();
    wait_until(|| ticks.load(Ordering::SeqCst) > 0).await;
    registry.stop_all().await;

    let at_stop = ticks.load(Ordering::SeqCst);
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert_eq!(ticks.load(Ordering::SeqCst), at_stop);
}

#[tokio::test]
async fn repeated_failures_are_revived_by_the_supervisor() {
    let fail = Arc::new(AtomicBool::new(true));
    let flag = Arc::clone(&fail);
    let registry = registry();
    registry
        .register(every(
            "wobbly",
            Arc::new(move || {
                let failing = flag.load(Ordering::SeqCst);
                Box::pin(async move {
                    if failing {
                        Err(TickError::new("x"))
                    } else {
                        Ok(())
                    }
                })
            }),
            10,
        ))
        .unwrap();

    registry.start_all().await.unwrap();
    wait_until(|| only(&registry).consecutive_failures >= 5).await;

    // Revive while it is STILL failing.
    assert_eq!(registry.revive_failed(5).await, ["wobbly"]);

    fail.store(false, Ordering::SeqCst);
    wait_until(|| only(&registry).consecutive_failures == 0).await;
    registry.stop_all().await;
}

#[tokio::test]
async fn health_distinguishes_running_stopped_and_failing() {
    let registry = registry();
    registry
        .register(ServiceDefinition {
            interval: Some(Duration::from_secs(300)),
            ..definition("ok")
        })
        .unwrap();
    registry.start_all().await.unwrap();
    assert_eq!(registry.health().await.status, HealthStatus::Healthy);

    registry.stop_one("ok").await;
    // DEGRADED, not unhealthy: an intentionally stopped service is not a fault.
    assert_eq!(registry.health().await.status, HealthStatus::Degraded);
}

#[tokio::test]
async fn a_per_service_health_check_can_downgrade_a_running_service() {
    let registry = registry();
    registry
        .register(ServiceDefinition {
            interval: Some(Duration::from_secs(300)),
            health_check: Some(Arc::new(|| {
                Box::pin(async { Ok(HealthResult::degraded("roster is 20 minutes stale")) })
            })),
            ..definition("ticking")
        })
        .unwrap();

    registry.start_all().await.unwrap();
    let health = registry.health().await;

    assert_eq!(health.status, HealthStatus::Degraded);
    assert!(health.detail.unwrap().contains("stale"));
    registry.stop_all().await;
}

#[tokio::test]
async fn a_throwing_health_check_is_unhealthy_not_an_exception() {
    let registry = registry();
    registry
        .register(ServiceDefinition {
            interval: Some(Duration::from_secs(300)),
            health_check: Some(Arc::new(|| Box::pin(async { panic!("check blew up") }))),
            ..definition("x")
        })
        .unwrap();

    registry.start_all().await.unwrap();
    assert_eq!(registry.health().await.status, HealthStatus::Unhealthy);
    registry.stop_all().await;
}

#[tokio::test]
async fn a_panicking_tick_is_contained_like_a_failing_one() {
    // Rust-only: a panic is the nearest thing to the C# unhandled exception.
    let registry = registry();
    registry
        .register(every(
            "panicky",
            Arc::new(|| Box::pin(async { panic!("boom") })),
            20,
        ))
        .unwrap();

    registry.start_all().await.unwrap();
    wait_until(|| only(&registry).failures >= 2).await;
    registry.stop_all().await;

    assert!(only(&registry).last_error.unwrap().contains("boom"));
}
