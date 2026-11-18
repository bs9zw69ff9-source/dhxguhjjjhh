//! Port of `RconCorrelationTests.cs`: whether a reply can be handed to the wrong command.
//!
//! From production, twice: `/manual BanList` answered with a ServerInfo document and, later, a
//! RefreshList one. The transport must not carry a reply across commands, and the client must
//! refuse a reply whose Command is not what it sent. `TheRegistrySurfacesAMismatch...` joins
//! this file with the registry (M6).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::Duration;

use pavlov_config::RconOptions;
use pavlov_rcon::testing::FakeRconServer;
use pavlov_rcon::{RconClient, RconError, reply};

fn options(server: &FakeRconServer) -> RconOptions {
    let mut o = server.options();
    o.command_timeout = Duration::from_millis(400);
    o.read_cache_duration = Duration::ZERO; // the cache would mask the transport
    o.max_attempts = 1; // one attempt, so the sequence is legible
    o
}

fn command_of(raw: &str) -> Option<String> {
    reply::text(&reply::try_parse(Some(raw))?, &["Command"])
}

#[tokio::test(flavor = "multi_thread")]
async fn a_slow_reply_is_not_handed_to_the_next_command() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.reply_delay = Duration::from_secs(2));
    let client = RconClient::new(options(&server));

    assert!(client.send("ServerInfo").await.is_err());

    server.configure(|b| b.reply_delay = Duration::ZERO);
    let reply = client.send("RefreshList").await.unwrap();

    assert_eq!(command_of(&reply).as_deref(), Some("RefreshList"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_command_the_server_never_answers_does_not_steal_the_next_reply() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| {
        b.swallow.insert("BanList".into());
    });
    let client = RconClient::new(options(&server));

    assert!(client.send("BanList").await.is_err());

    let reply = client.send("ServerInfo").await.unwrap();

    assert_eq!(command_of(&reply).as_deref(), Some("ServerInfo"));
}

#[tokio::test(flavor = "multi_thread")]
async fn concurrent_callers_each_get_their_own_reply() {
    let server = FakeRconServer::start().await.unwrap();
    let client = RconClient::new(options(&server));

    let verbs = [
        "ServerInfo",
        "RefreshList",
        "ItemList",
        "MapList",
        "ModeratorList",
    ];
    let tasks: Vec<_> = verbs
        .iter()
        .map(|v| {
            let c = client.clone();
            let v = (*v).to_owned();
            tokio::spawn(async move { c.send(&v).await })
        })
        .collect();

    for (verb, task) in verbs.iter().zip(tasks) {
        let reply = task.await.unwrap().unwrap();
        assert_eq!(command_of(&reply).as_deref(), Some(*verb));
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_mismatched_reply_is_rejected_rather_than_returned() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.answer_everything_as = Some("ServerInfo".into()));
    let client = RconClient::new(options(&server));

    let error = client.send("RefreshList").await.unwrap_err();

    assert!(
        matches!(error.root(), RconError::Mismatched { .. }),
        "{error:?}"
    );
    let message = error.to_string();
    assert!(message.contains("RefreshList"));
    assert!(message.contains("ServerInfo"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_replied_verb_that_is_simply_absent_is_accepted() {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.omit_command_field = true);
    let client = RconClient::new(options(&server));

    let reply = client.send("ServerInfo").await.unwrap();

    assert!(reply.contains("Successful"));
}
