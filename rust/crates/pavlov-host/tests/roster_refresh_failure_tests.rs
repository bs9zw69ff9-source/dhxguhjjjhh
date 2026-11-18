//! Port of `RosterRefreshFailureTests.cs`: what the registry reports when a roster refresh
//! fails, end to end from the wire. "Could not be parsed" named the one thing nobody needed
//! telling; what the server actually said is the diagnosis.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;

async fn registry(server: &FakeRconServer) -> RconRegistry {
    let mut options = server.options();
    options.read_cache_duration = Duration::ZERO;
    options.command_timeout = Duration::from_secs(5);
    options.max_attempts = 1;
    RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    )
}

#[tokio::test(flavor = "multi_thread")]
async fn a_non_json_reply_is_reported_with_what_the_server_actually_said() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.plain_text_reply = Some("Error: unknown command RefreshList".into()));
    let rcon = registry(&server).await;

    rcon.refresh_rosters().await;

    let problem = rcon.roster_problem("server1").unwrap();
    assert!(
        problem.contains("Error: unknown command RefreshList"),
        "{problem}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_json_reply_clears_the_problem_and_records_the_roster() {
    let server = FakeRconServer::start().await.unwrap();
    let rcon = registry(&server).await;

    rcon.refresh_rosters().await;

    assert_eq!(rcon.roster_problem("server1"), None);
    assert!(rcon.roster("server1").taken_at.is_some());
}

#[tokio::test(flavor = "multi_thread")]
async fn a_refusal_is_reported_as_a_refusal_rather_than_a_parse_failure() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.refuse_everything = true);
    let rcon = registry(&server).await;

    rcon.refresh_rosters().await;

    assert_eq!(
        rcon.roster_problem("server1").as_deref(),
        Some("the server refused RefreshList")
    );
}
