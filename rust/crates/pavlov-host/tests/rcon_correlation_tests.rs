//! Port of `RconCorrelationTests.cs`, the registry half (the client half is in the RCON crate).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::RconError;
use pavlov_rcon::testing::FakeRconServer;

#[tokio::test(flavor = "multi_thread")]
async fn the_registry_surfaces_a_mismatch_rather_than_the_wrong_answer() {
    // End to end through the path /manual actually uses.
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.answer_everything_as = Some("ServerInfo".into()));
    let mut o = server.options();
    o.read_cache_duration = Duration::ZERO;
    o.max_attempts = 1;
    let r = RconRegistry::with_system_clock(
        &[o],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    );

    let error = r.send("server1", "BanList").await.unwrap_err();
    assert!(
        matches!(error.root(), RconError::Mismatched { .. }),
        "{error:?}"
    );
}
