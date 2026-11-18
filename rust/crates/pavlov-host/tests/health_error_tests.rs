//! Port of `HealthErrorTests.cs`: each service's recent failures, and the `/health` field
//! they render into.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::services::report::recent_errors_field;
use pavlov_host::services::{
    ServiceDefinition, ServiceFailure, ServiceRegistry, ServiceState, ServiceStatus, TickError,
    Work,
};

fn registry() -> ServiceRegistry {
    ServiceRegistry::new(Arc::new(MetricsRegistry::default()))
}

fn failing(name: &str, error: impl Fn() -> TickError + Send + Sync + 'static) -> ServiceDefinition {
    let tick: Work = Arc::new(move || {
        let error = error();
        Box::pin(async move { Err(error) })
    });
    ServiceDefinition {
        interval: Some(Duration::from_millis(15)),
        run_on_start: true,
        ..ServiceDefinition::new(name, tick)
    }
}

async fn run_until_failures(registry: &ServiceRegistry, at_least: usize) -> Vec<ServiceStatus> {
    registry.start_all().await.unwrap();
    for _ in 0..200 {
        let status = registry.status();
        if status.iter().map(|s| s.errors.len()).sum::<usize>() >= at_least {
            registry.stop_all().await;
            return status;
        }
        tokio::time::sleep(Duration::from_millis(15)).await;
    }
    registry.stop_all().await;
    registry.status()
}

#[derive(Debug)]
struct NullReference;

impl std::fmt::Display for NullReference {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Object reference not set to an instance of an object")
    }
}

impl std::error::Error for NullReference {}

#[tokio::test]
async fn a_failing_tick_records_what_went_wrong() {
    let registry = registry();
    registry
        .register(failing("boom", || {
            TickError::new("RCON refused the connection")
        }))
        .unwrap();

    let status = run_until_failures(&registry, 1).await;

    let service = status.iter().find(|s| s.name == "boom").unwrap();
    assert!(!service.errors.is_empty());
    assert!(
        service.errors[0]
            .message
            .contains("RCON refused the connection")
    );
}

#[tokio::test]
async fn the_exception_type_is_kept_alongside_the_message() {
    let registry = registry();
    registry
        .register(failing("npe", || TickError::from_error(&NullReference)))
        .unwrap();

    let status = run_until_failures(&registry, 1).await;

    assert!(status[0].errors[0].message.contains("NullReference"));
}

#[tokio::test]
async fn more_than_one_failure_is_kept() {
    let count = Arc::new(AtomicUsize::new(0));
    let n = Arc::clone(&count);
    let registry = registry();
    registry
        .register(failing("varied", move || {
            TickError::new(format!("failure {}", n.fetch_add(1, Ordering::SeqCst) + 1))
        }))
        .unwrap();

    let status = run_until_failures(&registry, 3).await;

    let errors = &status[0].errors;
    assert!(errors.len() >= 3, "kept only {}", errors.len());
    assert!(errors[0].at >= errors[errors.len() - 1].at); // newest first
}

#[tokio::test]
async fn the_history_is_bounded() {
    let registry = registry();
    registry
        .register(failing("always", || TickError::new("nope")))
        .unwrap();

    let status = run_until_failures(&registry, 20).await;

    assert!(
        status[0].errors.len() <= 5,
        "kept {}",
        status[0].errors.len()
    );
}

fn with_errors(name: &str, errors: &[(i64, &str)]) -> ServiceStatus {
    ServiceStatus {
        name: name.into(),
        state: ServiceState::Running,
        uptime: Duration::from_secs(300),
        interval: Some(Duration::from_secs(60)),
        ticks: 10,
        failures: errors.len() as u64,
        consecutive_failures: 1,
        overruns: 0,
        last_error: errors.first().map(|e| e.1.to_owned()),
        last_tick_at: Some(Utc::now()),
        last_duration_ms: Some(5.0),
        errors: errors
            .iter()
            .map(|(ago, message)| ServiceFailure {
                at: Utc::now() - TimeDelta::seconds(*ago),
                message: (*message).into(),
            })
            .collect(),
    }
}

#[test]
fn no_failures_renders_nothing() {
    assert_eq!(recent_errors_field(&[with_errors("clean", &[])]), "");
}

#[test]
fn failures_from_different_services_are_interleaved_by_time() {
    let rendered = recent_errors_field(&[
        with_errors("older", &[(300, "first thing")]),
        with_errors("newer", &[(5, "second thing")]),
    ]);

    assert!(
        rendered.find("second thing").unwrap() < rendered.find("first thing").unwrap(),
        "the newest failure must come first"
    );
}

#[test]
fn a_multi_line_message_is_flattened() {
    let rendered = recent_errors_field(&[with_errors(
        "stacky",
        &[(1, "boom\n   at Thing.Method()\n   at Other()")],
    )]);
    assert_eq!(rendered.matches('\n').count(), 1);
}

#[test]
fn the_field_stays_inside_discords_limit() {
    let long = "x".repeat(300);
    let noisy: Vec<ServiceStatus> = (0..40)
        .map(|i| with_errors(&format!("service{i}"), &[(i, long.as_str())]))
        .collect();

    let rendered = recent_errors_field(&noisy);

    assert!(
        rendered.chars().count() <= 1024,
        "field is {} characters",
        rendered.chars().count()
    );
    assert!(rendered.contains("more")); // and it says what it dropped
}
