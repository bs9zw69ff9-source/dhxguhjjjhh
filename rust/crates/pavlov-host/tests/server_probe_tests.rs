//! The monitor's RCON probe (C# `RconServerProbe`; the C# had no test file for it): a real
//! round trip timed, ServerInfo read for map and capacity, a timeout named as one, a stopped
//! server reported as stopped, and the log time read by RCON number.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::monitoring::MonitorSettings;
use pavlov_core::time::SystemClock;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::monitoring::{RconServerProbe, ServerProbe, log_activity};
use pavlov_host::registry::RconRegistry;
use pavlov_host::server_labels::ServerLabels;
use pavlov_rcon::ServerLifecycle;
use pavlov_rcon::testing::FakeRconServer;

struct Stopped;
impl ServerLifecycle for Stopped {
    fn is_intentionally_stopped<'a>(&'a self, _: &'a str) -> BoxFuture<'a, bool> {
        Box::pin(async { true })
    }
}

fn registry(server: &FakeRconServer) -> Arc<RconRegistry> {
    let mut options = server.options();
    options.read_cache_duration = Duration::ZERO;
    options.max_attempts = 1;
    Arc::new(RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ))
}

fn settings(timeout: Duration) -> MonitorSettings {
    MonitorSettings {
        rcon_timeout: timeout,
        ..MonitorSettings::default()
    }
}

#[tokio::test]
async fn a_live_server_reports_its_map_capacity_and_a_measured_latency() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| {
        b.exact_reply = Some(
            r#"{"Command":"ServerInfo","ServerInfo":{"MapLabel":"Station","PlayerCount":"3/24"},"Successful":true}"#
                .into(),
        );
    });
    let probe = RconServerProbe::new(
        registry(&server),
        settings(Duration::from_secs(3)),
        Arc::new(SystemClock),
        None,
        None,
    );

    let result = probe.probe("server1").await;

    assert!(result.rcon_ok, "{:?}", result.failure_reason);
    assert!(result.latency.is_some());
    assert_eq!(result.map.as_deref(), Some("Station"));
    assert_eq!(result.max_players, Some(24));
    assert!(!result.intentionally_stopped);
    assert_eq!(
        server.commands(),
        ["ServerInfo"],
        "a real round trip, not a cache"
    );
}

#[tokio::test]
async fn no_reply_in_time_is_named_as_a_timeout() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.reply_delay = Duration::from_secs(2));
    let probe = RconServerProbe::new(
        registry(&server),
        settings(Duration::from_millis(200)),
        Arc::new(SystemClock),
        None,
        None,
    );

    let result = probe.probe("server1").await;

    assert!(!result.rcon_ok);
    assert!(result.latency.is_none());
    assert!(
        result
            .failure_reason
            .as_deref()
            .unwrap()
            .starts_with("no reply within")
    );
}

#[tokio::test]
async fn a_stopped_server_is_reported_as_stopped_even_when_rcon_fails() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.refuse_everything = true);
    let probe = RconServerProbe::new(
        registry(&server),
        settings(Duration::from_millis(500)),
        Arc::new(SystemClock),
        Some(Arc::new(Stopped)),
        None,
    );

    let result = probe.probe("server1").await;

    assert!(result.intentionally_stopped);
}

#[test]
fn the_log_time_is_read_by_rcon_number_and_a_missing_log_is_unknown() {
    let dir = tempfile::tempdir().unwrap();
    let one = dir.path().join("server1/Pavlov/Saved/Logs/Pavlov.log");
    std::fs::create_dir_all(one.parent().unwrap()).unwrap();
    std::fs::write(&one, "line").unwrap();
    let paths = vec![one.to_string_lossy().into_owned()];
    let labels = Arc::new(ServerLabels::default());
    labels.assign(paths.clone());

    let read = log_activity(labels, paths);

    assert!(read("server1").is_some());
    assert!(read("server7").is_none());
}
