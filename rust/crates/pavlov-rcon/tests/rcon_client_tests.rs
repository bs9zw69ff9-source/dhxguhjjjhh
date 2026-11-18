//! Port of `RconClientTests.cs`: the persistent session, read coalescing and the cache.
//!
//! The cost that matters is CONNECTIONS OPENED, not commands sent: every connection is a TCP
//! handshake plus an MD5 exchange serviced on Pavlov's game thread.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::{Duration, Instant};

use futures_util::future::join_all;
use pavlov_config::{RconOptions, Secret};
use pavlov_rcon::testing::FakeRconServer;
use pavlov_rcon::{RconClient, RconError, backoff};

fn options(server: &FakeRconServer, cache: Option<Duration>) -> RconOptions {
    let mut o = server.options();
    o.read_cache_duration = cache.unwrap_or(Duration::from_millis(2500));
    o
}

#[tokio::test(flavor = "multi_thread")]
async fn authenticates_and_returns_a_reply() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server, None));

    let reply = client.send("RefreshList").await.unwrap();

    assert!(reply.contains("\"Successful\":true"));
    assert_eq!(server.connections(), 1);
    // On the wire: the lowercase hex MD5 with NO newline, then the command and one "\n".
    assert_eq!(
        server.logins(),
        [b"5ebe2294ecd0e0f08eab7690d2a6ee69".to_vec()]
    );
    assert_eq!(server.wire(), [b"RefreshList\n".to_vec()]);
}

#[tokio::test(flavor = "multi_thread")]
async fn many_commands_share_one_connection() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server, Some(Duration::ZERO)));

    for i in 0..10 {
        client.send(&format!("Kick Player{i}")).await.unwrap();
    }

    assert_eq!(server.connections(), 1);
    assert_eq!(server.commands().len(), 10);
}

#[tokio::test(flavor = "multi_thread")]
async fn wrong_password_fails_fast_and_is_not_retried() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.reject_auth = true);
    let client = RconClient::new(options(&server, None));

    let error = client.send("RefreshList").await.unwrap_err();

    assert!(matches!(error, RconError::Auth(_)), "{error:?}");
    // Retrying a rejected password is just a slower way to stay broken.
    assert_eq!(server.connections(), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn concurrent_identical_reads_share_one_exchange() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.reply_delay = Duration::from_millis(60));
    let client = RconClient::new(options(&server, None));

    let replies = join_all((0..6).map(|_| client.send("RefreshList"))).await;

    assert!(replies.iter().all(Result::is_ok));
    assert_eq!(server.commands().len(), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_completed_read_is_reused_within_the_window() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server, Some(Duration::from_millis(400))));

    client.send("RefreshList").await.unwrap();
    client.send("RefreshList").await.unwrap();
    assert_eq!(server.commands().len(), 1);

    tokio::time::sleep(Duration::from_millis(500)).await;
    client.send("RefreshList").await.unwrap();
    assert_eq!(server.commands().len(), 2); // past the window it goes back to the server
}

#[tokio::test(flavor = "multi_thread")]
async fn different_reads_are_cached_separately() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server, None));

    client.send("RefreshList").await.unwrap();
    client.send("ServerInfo").await.unwrap();

    assert_eq!(server.commands().len(), 2);
}

#[tokio::test(flavor = "multi_thread")]
async fn mutations_are_never_cached_and_they_clear_the_roster() {
    // The dangerous case: kick someone, then read a roster that still lists them.
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server, None));

    client.send("RefreshList").await.unwrap();
    client.send("Kick Griefer").await.unwrap();
    client.send("RefreshList").await.unwrap();
    assert_eq!(server.commands().len(), 3);

    client.send("Kick Griefer").await.unwrap();
    client.send("Kick Griefer").await.unwrap();
    assert_eq!(server.commands().len(), 5);
}

#[tokio::test(flavor = "multi_thread")]
async fn an_unknown_command_is_treated_as_a_mutation() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server, None));

    client.send("SomeFutureCommand").await.unwrap();
    client.send("SomeFutureCommand").await.unwrap();

    assert_eq!(server.commands().len(), 2);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_dropped_session_is_rebuilt_transparently() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server, Some(Duration::ZERO)));

    client.send("RefreshList").await.unwrap();
    server.configure(|b| b.drop_after_next_command = true);
    client.send("ServerInfo").await.unwrap(); // this one kills the session

    let reply = client.send("RefreshList").await.unwrap(); // must reconnect silently
    assert!(reply.contains("\"Successful\":true"));
    assert_eq!(server.connections(), 2);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_failed_read_is_not_cached_as_an_answer() {
    // Nothing listening on this port.
    let mut o = RconOptions::new("dead", "127.0.0.1", 1, Secret::new("x"));
    o.max_attempts = 1;
    o.command_timeout = Duration::from_millis(400);
    o.command_spacing = Duration::ZERO;
    let client = RconClient::new(o);

    assert!(client.send("RefreshList").await.is_err());
    assert!(client.send("RefreshList").await.is_err());
}

#[tokio::test(flavor = "multi_thread")]
async fn command_spacing_is_honoured() {
    // The docs advise 100 ms between successive commands to prevent drops.
    let server = FakeRconServer::start().await.unwrap();
    let mut o = options(&server, Some(Duration::ZERO));
    o.command_spacing = Duration::from_millis(100);
    let client = RconClient::new(o);

    let started = Instant::now();
    for i in 0..4 {
        client.send(&format!("Kick P{i}")).await.unwrap();
    }
    let elapsed = started.elapsed();

    // Four commands means at least three gaps.
    assert!(elapsed >= Duration::from_millis(250), "took {elapsed:?}");
}

#[tokio::test(flavor = "multi_thread")]
async fn issued_commands_are_recognised_as_the_bots_own() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server, None));

    assert!(!client.was_issued_by_bot("GiveMenu Stranger"));
    client.send("GiveMenu Butter Life").await.unwrap();

    assert!(client.was_issued_by_bot("GiveMenu Butter Life"));
    assert!(client.was_issued_by_bot("  givemenu butter life  ")); // case and padding insensitive
    assert!(!client.was_issued_by_bot("GiveMenu Attacker")); // a different target must still alert
}

#[test]
fn backoff_jitter_stays_within_its_window() {
    for attempt in 0..4u32 {
        let ceiling = 30_000f64.min(500.0 * f64::from(1u32 << attempt));
        for _ in 0..50 {
            let ms = backoff::default_delay(attempt).as_secs_f64() * 1000.0;
            assert!(
                ms >= ceiling * 0.5 - 1.0 && ms <= ceiling,
                "{attempt}: {ms} not in [{}, {ceiling}]",
                ceiling * 0.5
            );
        }
    }
}
