//! Port of `RconChunkedReplyTests.cs`: replies that arrive in more than one TCP chunk, split
//! where the fake chooses - including through the middle of a multi-byte character.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::Duration;

use pavlov_config::RconOptions;
use pavlov_rcon::RconClient;
use pavlov_rcon::testing::FakeRconServer;

fn options(server: &FakeRconServer) -> RconOptions {
    let mut o = server.options();
    o.command_timeout = Duration::from_secs(5);
    o.read_cache_duration = Duration::ZERO;
    o
}

/// A reply carrying a name whose characters are not one byte each.
const REPLY: &str = "{\"Command\":\"RefreshList\",\"Successful\":true,\"PlayerList\":[{\"Username\":\"Réçkeré\"}]}\r\n";

async fn send_split(at: usize) -> String {
    let server = FakeRconServer::start().await.unwrap();
    server.configure(|b| {
        b.exact_reply = Some(REPLY.into());
        b.split_reply_after_bytes = Some(at);
    });
    RconClient::new(options(&server))
        .send("RefreshList")
        .await
        .unwrap()
}

#[tokio::test(flavor = "multi_thread")]
async fn a_reply_split_through_the_middle_of_a_character_still_decodes() {
    // The split lands inside the two bytes of "é".
    let bytes_before_accent = REPLY.find('é').unwrap();
    assert!(
        REPLY.len() > REPLY.chars().count(),
        "the fixture must contain multi-byte characters"
    );

    let reply = send_split(bytes_before_accent + 1).await;

    assert_eq!(
        reply.trim_end_matches(['\r', '\n']),
        REPLY.trim_end_matches(['\r', '\n'])
    );
    assert!(!reply.contains('\u{FFFD}'));
    assert!(reply.contains("Réçkeré"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_reply_split_on_a_character_boundary_is_unaffected() {
    let reply = send_split(10).await;

    assert_eq!(
        reply.trim_end_matches(['\r', '\n']),
        REPLY.trim_end_matches(['\r', '\n'])
    );
    assert!(!reply.contains('\u{FFFD}'));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_split_json_reply_is_not_settled_until_it_is_complete() {
    let reply = send_split(20).await;

    assert!(reply.trim_end_matches(['\r', '\n']).ends_with('}'));
}
