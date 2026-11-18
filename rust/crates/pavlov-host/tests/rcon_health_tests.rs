//! Port of `RconHealthTests.cs`: a server stopped on purpose is not an RCON fault.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashSet;
use std::sync::Arc;
use std::time::Duration;

use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_config::{RconOptions, Secret};
use pavlov_host::health::{HealthResult, HealthStatus};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;
use pavlov_host::service_control::number_for;
use pavlov_rcon::ServerLifecycle;

/// In Rust a lookup cannot throw: an implementation that cannot ask answers `false`, which is
/// the "fault stands" outcome the C# exception produced.
struct StubLifecycle {
    stopped: HashSet<String>,
    broken: bool,
}

impl StubLifecycle {
    fn new(stopped: &[&str]) -> Arc<Self> {
        Arc::new(Self {
            stopped: stopped.iter().map(|s| (*s).to_owned()).collect(),
            broken: false,
        })
    }
}

impl ServerLifecycle for StubLifecycle {
    fn is_intentionally_stopped<'a>(&'a self, name: &'a str) -> BoxFuture<'a, bool> {
        let answer = !self.broken && self.stopped.contains(name);
        async move { answer }.boxed()
    }
}

/// Servers on ports nothing listens on, failing fast.
fn registry(numbers: &[u16]) -> RconRegistry {
    let servers: Vec<RconOptions> = numbers
        .iter()
        .map(|n| {
            let mut o = RconOptions::new(
                format!("server{n}"),
                "127.0.0.1",
                i32::from(9 + *n),
                Secret::new("x"),
            );
            o.max_attempts = 1;
            o.command_timeout = Duration::from_millis(500);
            o
        })
        .collect();
    RconRegistry::with_system_clock(
        &servers,
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    )
}

async fn health_after_failing_probes(
    registry: &RconRegistry,
    lifecycle: Option<Arc<dyn ServerLifecycle>>,
) -> HealthResult {
    if let Some(lifecycle) = lifecycle {
        registry.use_lifecycle(lifecycle);
    }
    registry.probe_all().await;
    registry.health().await
}

#[tokio::test]
async fn a_server_stopped_on_purpose_does_not_count_against_health() {
    let r = registry(&[1, 2]);
    let health =
        health_after_failing_probes(&r, Some(StubLifecycle::new(&["server1", "server2"]))).await;
    assert_eq!(health.status, HealthStatus::Healthy);
    assert!(health.detail.unwrap().contains("stopped on purpose"));
}

#[tokio::test]
async fn a_server_that_is_supposed_to_be_up_still_counts_against_health() {
    let r = registry(&[1, 2]);
    let health = health_after_failing_probes(&r, Some(StubLifecycle::new(&["server1"]))).await;
    assert_eq!(health.status, HealthStatus::Degraded);
    assert!(health.detail.unwrap().contains("server2"));
}

#[tokio::test]
async fn with_no_lifecycle_source_every_unreachable_server_counts_as_before() {
    let r = registry(&[1, 2]);
    let health = health_after_failing_probes(&r, None).await;
    assert_eq!(health.status, HealthStatus::Unhealthy);
}

#[tokio::test]
async fn a_lifecycle_lookup_that_throws_leaves_the_fault_standing() {
    let r = registry(&[1]);
    let broken = Arc::new(StubLifecycle {
        stopped: ["server1".to_owned()].into(),
        broken: true,
    });
    let health = health_after_failing_probes(&r, Some(broken)).await;
    assert_eq!(health.status, HealthStatus::Unhealthy);
}

#[tokio::test]
async fn a_stopped_server_does_not_trade_a_red_rcon_check_for_a_stale_roster_one() {
    let r = registry(&[1]);
    let health = health_after_failing_probes(&r, Some(StubLifecycle::new(&["server1"]))).await;
    assert_eq!(health.status, HealthStatus::Healthy);
    assert!(!health.detail.unwrap().to_lowercase().contains("stale"));
}

#[test]
fn an_rcon_name_resolves_to_its_server_number() {
    assert_eq!(number_for("server1"), Some(1));
    assert_eq!(number_for("server12"), Some(12));
    assert_eq!(number_for("server0"), None);
    assert_eq!(number_for("prod-a"), None);
    assert_eq!(number_for("server"), None);
}

#[tokio::test]
async fn nothing_probed_yet_is_unknown_not_healthy() {
    let r = registry(&[1]);
    assert_eq!(r.health().await.status, HealthStatus::Degraded);
    let none = registry(&[]);
    assert_eq!(none.health().await.status, HealthStatus::Unhealthy);
}
