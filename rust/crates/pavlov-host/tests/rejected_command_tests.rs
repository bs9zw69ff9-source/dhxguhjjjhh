//! Port of `RejectedCommandTests.cs`: a command the server refused is an error, not a success.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::RconError;
use pavlov_rcon::testing::{Behaviour, FakeRconServer};

async fn setup(change: impl FnOnce(&mut Behaviour)) -> (FakeRconServer, RconRegistry) {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(change);
    let mut o = server.options();
    o.read_cache_duration = Duration::ZERO;
    o.max_attempts = 1;
    let r = RconRegistry::with_system_clock(
        &[o],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    );
    (server, r)
}

#[tokio::test(flavor = "multi_thread")]
async fn a_refused_command_throws_rather_than_returning_quietly() {
    let (_server, r) = setup(|b| b.refuse_everything = true).await;

    match r.send_verified("server1", "Ban Holosight1").await {
        Err(RconError::Rejected { command, reply }) => {
            assert!(command.contains("Ban Holosight1"));
            assert!(reply.contains("Successful"));
        }
        other => panic!("{other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn an_accepted_command_passes_through() {
    let (_server, r) = setup(|_| {}).await;
    assert!(
        r.send_verified("server1", "Ban Holosight1")
            .await
            .unwrap()
            .contains("Successful")
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_reply_that_does_not_say_is_treated_as_accepted() {
    let (_server, r) = setup(|b| b.omit_successful_field = true).await;
    assert!(r.send_verified("server1", "Ban Holosight1").await.is_ok());
}

#[tokio::test(flavor = "multi_thread")]
async fn a_non_json_reply_is_treated_as_accepted() {
    let (_server, r) = setup(|b| b.plain_text_reply = Some("OK".into())).await;
    assert!(
        r.send_verified("server1", "Ban Holosight1")
            .await
            .unwrap()
            .contains("OK")
    );
}
