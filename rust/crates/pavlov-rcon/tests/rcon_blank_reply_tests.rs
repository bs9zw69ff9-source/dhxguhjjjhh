//! Port of `RconBlankReplyTests.cs`: a blank line arriving where a reply should be. A stray
//! "\r\n" returned AS the answer left the real reply in the socket, and every command after it
//! read one behind, permanently.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::Duration;

use pavlov_config::RconOptions;
use pavlov_rcon::RconClient;
use pavlov_rcon::testing::FakeRconServer;

fn options(server: &FakeRconServer, timeout_seconds: u64) -> RconOptions {
    let mut o = server.options();
    o.command_timeout = Duration::from_secs(timeout_seconds);
    o.read_cache_duration = Duration::ZERO;
    o
}

const ROSTER: &str = "{\"Command\":\"RefreshList\",\"Successful\":true,\"PlayerList\":[{\"Username\":\"Alice\"}]}\r\n";

#[tokio::test(flavor = "multi_thread")]
async fn a_blank_line_before_the_reply_is_not_mistaken_for_it() {
    // SPLIT AFTER THE BLANK LINE, so it arrives in a read of its own.
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| {
        b.exact_reply = Some(format!("\r\n{ROSTER}"));
        b.split_reply_after_bytes = Some(2);
    });
    let client = RconClient::new(options(&server, 5));

    let reply = client.send("RefreshList").await.unwrap();

    assert!(reply.contains("Alice"));
    assert!(reply.contains("PlayerList"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_reply_of_nothing_but_whitespace_is_never_returned_as_a_reply() {
    // The right end is the command timeout, which drops the session so the next one reconnects.
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.exact_reply = Some("\r\n".into()));
    let mut o = options(&server, 2);
    o.max_attempts = 1;
    let client = RconClient::new(o);

    assert!(client.send("RefreshList").await.is_err());
}

#[tokio::test(flavor = "multi_thread")]
async fn a_plain_text_reply_with_actual_content_still_settles() {
    // THE CONTROL: a refusal or a status line is a real answer. Only whitespace is not.
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| b.exact_reply = Some("Password accepted\r\n".into()));
    let client = RconClient::new(options(&server, 5));

    let reply = client.send("RefreshList").await.unwrap();

    assert!(reply.contains("Password accepted"));
}
