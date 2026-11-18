//! Port of `ObservabilityTests.cs`: the metrics registry and the health registry.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use futures_util::FutureExt;
use pavlov_host::health::{Check, HealthRegistry, HealthResult, HealthStatus};
use pavlov_host::metrics::{MAX_SERIES, MetricLabels, MetricsRegistry};

// ---- metrics ----

#[test]
fn label_order_does_not_create_a_second_series() {
    let m = MetricsRegistry::default();
    m.increment("things", &MetricLabels::of2("a", "1", "b", "2"), 1.0, None);
    m.increment("things", &MetricLabels::of2("b", "2", "a", "1"), 1.0, None);

    assert_eq!(m.stats().series, 1);
    assert!(m.render().contains("bot_things{a=\"1\",b=\"2\"} 2"));
}

#[test]
fn counters_accumulate_and_gauges_replace() {
    let m = MetricsRegistry::default();
    m.increment("hits", &MetricLabels::none(), 1.0, None);
    m.increment("hits", &MetricLabels::none(), 4.0, None);
    m.gauge("depth", 10.0, &MetricLabels::none(), None);
    m.gauge("depth", 3.0, &MetricLabels::none(), None);

    let rendered = m.render();
    assert!(rendered.contains("bot_hits 5\n"));
    assert!(rendered.contains("bot_depth 3\n"));
}

#[test]
fn histogram_buckets_are_cumulative() {
    let m = MetricsRegistry::default();
    let buckets = [5.0, 10.0, 100.0];
    m.observe_with("latency", 7.0, &MetricLabels::none(), None, &buckets);
    m.observe_with("latency", 60.0, &MetricLabels::none(), None, &buckets);

    let r = m.render();
    assert!(r.contains("bot_latency_bucket{le=\"5\"} 0"));
    assert!(r.contains("bot_latency_bucket{le=\"10\"} 1"));
    assert!(r.contains("bot_latency_bucket{le=\"100\"} 2"));
    assert!(r.contains("bot_latency_bucket{le=\"+Inf\"} 2"));
    assert!(r.contains("bot_latency_count 2"));
    assert!(r.contains("bot_latency_sum 67"));
}

#[test]
fn label_values_are_escaped() {
    let m = MetricsRegistry::default();
    m.increment(
        "x",
        &MetricLabels::of("name", "he said \"hi\"\nand left"),
        1.0,
        None,
    );
    assert!(
        m.render()
            .contains("name=\"he said \\\"hi\\\"\\nand left\"")
    );
}

#[test]
fn the_series_ceiling_is_enforced_and_counted() {
    let m = MetricsRegistry::default();
    for i in 0..MAX_SERIES + 50 {
        m.increment("explode", &MetricLabels::of("i", &i.to_string()), 1.0, None);
    }
    let stats = m.stats();
    assert_eq!(stats.series, MAX_SERIES);
    assert_eq!(stats.dropped, 50);
}

#[tokio::test]
async fn time_async_labels_the_outcome_and_rethrows() {
    let m = MetricsRegistry::default();
    let labels = MetricLabels::of("kind", "read");
    let ok: Result<i32, String> = m.time("op", &labels, None, async { Ok(1) }).await;
    let failed: Result<i32, String> = m
        .time("op", &labels, None, async { Err("no".into()) })
        .await;

    assert_eq!(ok, Ok(1));
    assert_eq!(failed, Err("no".to_owned()));
    let r = m.render();
    assert!(r.contains("outcome=\"success\""));
    assert!(r.contains("outcome=\"failure\""));
}

#[test]
fn every_metric_renders_its_help_and_type() {
    let m = MetricsRegistry::default();
    m.increment(
        "commands_total",
        &MetricLabels::none(),
        1.0,
        Some("Commands run"),
    );
    assert!(
        m.render()
            .contains("# HELP bot_commands_total Commands run")
    );
    assert!(m.render().contains("# TYPE bot_commands_total counter"));
}

#[test]
fn concurrent_increments_do_not_lose_counts() {
    let m = Arc::new(MetricsRegistry::default());
    let threads: Vec<_> = (0..8)
        .map(|_| {
            let m = Arc::clone(&m);
            std::thread::spawn(move || {
                for _ in 0..125 {
                    m.increment("races", &MetricLabels::of("shared", "yes"), 1.0, None);
                }
            })
        })
        .collect();
    for t in threads {
        t.join().unwrap();
    }
    assert!(m.render().contains("bot_races{shared=\"yes\"} 1000"));
}

// ---- health ----

fn returns(result: HealthResult) -> Check {
    Arc::new(move || {
        let result = result.clone();
        async move { result }.boxed()
    })
}

fn uncached() -> HealthRegistry {
    HealthRegistry::new(Duration::from_secs(3), Duration::ZERO)
}

#[tokio::test]
async fn worst_status_wins() {
    let h = uncached();
    h.register("a", true, returns(HealthResult::healthy(None)));
    h.register("b", true, returns(HealthResult::degraded("slow")));
    assert_eq!(h.report().await.status, HealthStatus::Degraded);

    h.register("c", true, returns(HealthResult::unhealthy("down")));
    assert_eq!(h.report().await.status, HealthStatus::Unhealthy);
}

#[tokio::test]
async fn a_non_critical_failure_does_not_fail_the_rollup() {
    let h = uncached();
    h.register("core", true, returns(HealthResult::healthy(None)));
    h.register(
        "webhook",
        false,
        returns(HealthResult::unhealthy("no URL set")),
    );

    let report = h.report().await;
    assert_eq!(report.status, HealthStatus::Healthy);
    assert_eq!(report.components["webhook"].status, HealthStatus::Unhealthy);
}

#[tokio::test]
async fn a_throwing_check_is_unhealthy_not_an_exception() {
    let h = uncached();
    let boom: Check = Arc::new(|| async { panic!("kaboom") }.boxed());
    h.register("boom", true, boom);

    let report = h.report().await;
    assert_eq!(report.status, HealthStatus::Unhealthy);
    assert_eq!(report.components["boom"].detail.as_deref(), Some("kaboom"));
}

#[tokio::test]
async fn a_hanging_check_is_unhealthy_after_the_timeout() {
    let h = HealthRegistry::new(Duration::from_millis(100), Duration::ZERO);
    let stuck: Check = Arc::new(|| futures_util::future::pending().boxed());
    h.register("stuck", true, stuck);

    let report = tokio::time::timeout(Duration::from_secs(5), h.report())
        .await
        .unwrap();
    assert_eq!(report.status, HealthStatus::Unhealthy);
    assert!(
        report.components["stuck"]
            .detail
            .as_deref()
            .unwrap()
            .contains("timed out")
    );
}

#[tokio::test]
async fn results_are_cached_so_a_scrape_loop_is_not_itself_load() {
    let calls = Arc::new(AtomicUsize::new(0));
    let h = HealthRegistry::new(Duration::from_secs(3), Duration::from_secs(60));
    let counted = Arc::clone(&calls);
    let check: Check = Arc::new(move || {
        counted.fetch_add(1, Ordering::SeqCst);
        async { HealthResult::healthy(None) }.boxed()
    });
    h.register("counted", true, check);

    h.report().await;
    h.report().await;
    h.report().await;
    assert_eq!(calls.load(Ordering::SeqCst), 1);
}

#[tokio::test]
async fn unregistering_stops_a_component_being_reported() {
    let h = uncached();
    h.register("temporary", true, returns(HealthResult::unhealthy("x")));
    assert_eq!(h.report().await.status, HealthStatus::Unhealthy);

    assert!(h.unregister("temporary"));
    let report = h.report().await;
    assert_eq!(report.status, HealthStatus::Healthy);
    assert!(!report.components.contains_key("temporary"));
}

#[tokio::test]
async fn an_empty_registry_is_healthy() {
    assert_eq!(
        HealthRegistry::default().report().await.status,
        HealthStatus::Healthy
    );
}
