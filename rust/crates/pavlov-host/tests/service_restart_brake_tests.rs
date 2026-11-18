//! Port of `ServiceRestartBrakeTests.cs`: the supervisor gives up after three restarts in the
//! window, keeps reporting the service as broken, and hands the budget back once it recovers.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::Duration;

use pavlov_host::health::HealthStatus;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::services::registry::MAX_RESTARTS_PER_WINDOW;
use pavlov_host::services::{ServiceDefinition, ServiceRegistry, TickError};

fn registry() -> ServiceRegistry {
    ServiceRegistry::new(Arc::new(MetricsRegistry::default()))
}

/// A service that fails while `fail` is set, counting its starts.
fn flaky(name: &str, starts: &Arc<AtomicUsize>, fail: &Arc<AtomicBool>) -> ServiceDefinition {
    let starts = Arc::clone(starts);
    let fail = Arc::clone(fail);
    ServiceDefinition {
        interval: Some(Duration::from_millis(20)),
        run_on_start: true,
        on_start: Some(Arc::new(move || {
            starts.fetch_add(1, Ordering::SeqCst);
            Box::pin(async { Ok(()) })
        })),
        ..ServiceDefinition::new(
            name,
            Arc::new(move || {
                let failing = fail.load(Ordering::SeqCst);
                Box::pin(async move {
                    if failing {
                        Err(TickError::new("its dependency is down"))
                    } else {
                        Ok(())
                    }
                })
            }),
        )
    }
}

async fn sweeps(registry: &ServiceRegistry, n: usize) {
    for _ in 0..n {
        tokio::time::sleep(Duration::from_millis(30)).await;
        registry.revive_failed(1).await;
    }
}

#[tokio::test]
async fn a_restart_loop_is_capped_rather_than_running_forever() {
    let starts = Arc::new(AtomicUsize::new(0));
    let registry = registry();
    registry
        .register(flaky("doomed", &starts, &Arc::new(AtomicBool::new(true))))
        .unwrap();
    registry.start_all().await.unwrap();

    const SWEEPS: usize = 12;
    sweeps(&registry, SWEEPS).await;

    // Two assertions: the first alone passes for any value of the constant.
    let starts = starts.load(Ordering::SeqCst);
    assert!(starts <= 1 + MAX_RESTARTS_PER_WINDOW, "saw {starts} starts");
    assert!(
        starts < SWEEPS,
        "restarts scale with the outage ({starts} over {SWEEPS})"
    );
    registry.stop_all().await;
}

#[tokio::test]
async fn giving_up_still_leaves_the_service_visibly_broken() {
    let registry = registry();
    registry
        .register(flaky(
            "doomed",
            &Arc::new(AtomicUsize::new(0)),
            &Arc::new(AtomicBool::new(true)),
        ))
        .unwrap();
    registry.start_all().await.unwrap();

    sweeps(&registry, 12).await;

    assert_ne!(registry.health().await.status, HealthStatus::Healthy);
    registry.stop_all().await;
}

#[tokio::test]
async fn a_service_that_recovers_gets_its_restart_budget_back() {
    let starts = Arc::new(AtomicUsize::new(0));
    let fail = Arc::new(AtomicBool::new(true));
    let registry = registry();
    registry.register(flaky("flaky", &starts, &fail)).unwrap();
    registry.start_all().await.unwrap();

    sweeps(&registry, 8).await;
    let after_outage = starts.load(Ordering::SeqCst);

    // It recovers, ticks successfully, then breaks again.
    fail.store(false, Ordering::SeqCst);
    tokio::time::sleep(Duration::from_millis(120)).await;
    fail.store(true, Ordering::SeqCst);

    sweeps(&registry, 8).await;

    assert!(
        starts.load(Ordering::SeqCst) > after_outage,
        "a recovered service should be revivable again"
    );
    registry.stop_all().await;
}
