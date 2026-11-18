//! Port of `RconDeliveryTests.cs`: a command that changes state is sent once unless it provably
//! never arrived.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::Duration;

use pavlov_config::RconOptions;
use pavlov_rcon::testing::FakeRconServer;
use pavlov_rcon::{ExchangeProgress, RconClient, RconError};

fn options(server: &FakeRconServer) -> RconOptions {
    let mut o = server.options();
    o.name = "test".into();
    o.command_timeout = Duration::from_millis(300);
    o.max_attempts = 3;
    o.read_cache_duration = Duration::ZERO;
    o
}

fn count(server: &FakeRconServer, command: &str) -> usize {
    server.commands().iter().filter(|c| *c == command).count()
}

#[tokio::test(flavor = "multi_thread")]
async fn a_slow_mutation_is_not_sent_again() {
    // The server received the Kick and was slow to answer. Treating the timeout as "it did not
    // happen" re-sent it on every attempt - a double Notify, a second Kick.
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.reply_delay = Duration::from_millis(800));
    let client = RconClient::new(options(&server));

    let failure = client.send("Kick Griefer").await.unwrap_err();

    assert!(
        matches!(failure, RconError::Unconfirmed { .. }),
        "{failure:?}"
    );
    assert!(failure.to_string().contains("may have been applied"));
    assert_eq!(count(&server, "Kick Griefer"), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_slow_read_is_still_retried() {
    // Reads are harmless to repeat, so they keep their retries.
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.reply_delay = Duration::from_millis(800));
    let client = RconClient::new(options(&server));

    assert!(client.send("ServerInfo").await.is_err());

    assert!(count(&server, "ServerInfo") > 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_mutation_on_a_dead_session_is_resent_on_a_fresh_one() {
    // The write "succeeds" into a closed socket and nothing comes back - a dead connection
    // cannot have delivered it, so sending it again is safe.
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server));

    server.configure(|b| b.drop_after_next_command = true);
    client.send("RefreshList").await.unwrap(); // answered, then the server hangs up
    tokio::time::sleep(Duration::from_millis(100)).await;

    let reply = client.send("Kick Griefer").await.unwrap();

    assert!(reply.contains("Kick"));
    assert_eq!(count(&server, "Kick Griefer"), 1);
    assert_eq!(server.connections(), 2);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_multi_line_command_is_refused_before_it_reaches_the_wire() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server));

    for command in ["Kick a\nBan b", "Notify All hi\r\nGiveCash x 100", "   "] {
        let error = client.send(command).await.unwrap_err();
        assert!(matches!(error, RconError::InvalidCommand(_)), "{command:?}");
    }

    assert!(server.commands().is_empty());
    assert_eq!(server.connections(), 0);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_handshake_split_across_packets_still_authenticates() {
    // "Pass" + "word: " and "Auth" + "enticated=1" used to read as a wrong prompt / refusal.
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.split_handshake = true);
    let client = RconClient::new(options(&server));

    assert!(
        client
            .send("RefreshList")
            .await
            .unwrap()
            .contains("RefreshList")
    );
}

#[test]
fn nothing_written_means_nothing_applied() {
    let progress = |written, reply_bytes| ExchangeProgress {
        written,
        reply_bytes,
    };
    let timeout = RconError::Timeout(Duration::from_secs(1));
    let lost = RconError::Transport("connection reset".into());

    assert!(!RconClient::may_have_been_applied(
        &timeout,
        &progress(false, 0)
    ));
    assert!(RconClient::may_have_been_applied(
        &timeout,
        &progress(true, 0)
    ));
    assert!(!RconClient::may_have_been_applied(
        &lost,
        &progress(true, 0)
    ));
    assert!(RconClient::may_have_been_applied(
        &lost,
        &progress(true, 12)
    ));
}
