//! Port of `RconResilienceTests.cs`: the transport under failure - timeouts, stream state and
//! coalescing - against a real socket, because a desynchronised stream cannot be expressed in a
//! mock of the client's own abstraction.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::Duration;

use futures_util::future::join_all;
use pavlov_config::RconOptions;
use pavlov_rcon::RconClient;
use pavlov_rcon::testing::FakeRconServer;

fn options(server: &FakeRconServer) -> RconOptions {
    let mut o = server.options();
    o.name = "test".into();
    o.command_timeout = Duration::from_millis(300);
    o.max_attempts = 1;
    o.read_cache_duration = Duration::ZERO;
    o
}

#[tokio::test(flavor = "multi_thread")]
async fn a_timed_out_command_does_not_desynchronise_the_stream() {
    /* THE BUG THIS EXISTS FOR. A command answered AFTER the client gave up puts an orphaned reply
    in the socket, and the next command must not read it as its own. */
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server));

    server.configure(|b| b.reply_delay = Duration::from_millis(800)); // the timeout is 300 ms
    assert!(client.send("ServerInfo").await.is_err());

    server.configure(|b| b.reply_delay = Duration::ZERO);
    tokio::time::sleep(Duration::from_secs(1)).await; // let the orphan land

    let reply = client.send("RefreshList").await.unwrap();

    assert!(reply.contains("RefreshList"));
    assert!(!reply.contains("ServerInfo"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_timed_out_command_forces_a_fresh_session() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| {
        b.swallow.insert("ServerInfo".into());
    });
    let client = RconClient::new(options(&server));

    assert!(client.send("ServerInfo").await.is_err());
    client.send("RefreshList").await.unwrap();

    assert_eq!(server.connections(), 2);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_successful_command_keeps_the_session_open() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server));

    for _ in 0..5 {
        client.send("RefreshList").await.unwrap();
    }

    assert_eq!(server.connections(), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn every_reply_matches_its_own_command() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server));

    for command in ["RefreshList", "ServerInfo", "MapList", "Banlist"] {
        assert!(client.send(command).await.unwrap().contains(command));
    }
}

fn coalescing(server: &FakeRconServer) -> RconClient {
    let mut o = options(server);
    o.read_cache_duration = Duration::from_secs(10);
    o.command_timeout = Duration::from_secs(5);
    RconClient::new(o)
}

#[tokio::test(flavor = "multi_thread")]
async fn one_caller_cancelling_does_not_cancel_a_coalesced_read_for_everybody_else() {
    // In Rust a caller cancels by dropping its future; here the first caller's task is aborted
    // while the shared read is in flight.
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.reply_delay = Duration::from_millis(200));
    let client = coalescing(&server);

    let first = tokio::spawn({
        let c = client.clone();
        async move { c.send("RefreshList").await }
    });
    tokio::time::sleep(Duration::from_millis(20)).await;
    let second = tokio::spawn({
        let c = client.clone();
        async move { c.send("RefreshList").await }
    });
    tokio::time::sleep(Duration::from_millis(20)).await;
    first.abort();

    let reply = second.await.unwrap().unwrap();
    assert!(reply.contains("RefreshList"));
    assert_eq!(server.commands().len(), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_coalesced_read_hits_the_server_once() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.reply_delay = Duration::from_millis(150));
    let client = coalescing(&server);

    let replies = join_all((0..6).map(|_| client.send("RefreshList"))).await;

    assert!(replies.iter().all(Result::is_ok));
    assert_eq!(
        server
            .commands()
            .iter()
            .filter(|c| c.starts_with("RefreshList"))
            .count(),
        1
    );
}
