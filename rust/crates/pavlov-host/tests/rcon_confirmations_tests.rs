//! Port of `RconConfirmationsTests.cs`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use pavlov_core::time::{Clock, ManualClock};
use pavlov_host::confirmations::RconConfirmations;

const COMMAND: &str = "GiveMenu Bob 0010010000000000101000000000000 10001000000000";

fn manual() -> (Arc<ManualClock>, RconConfirmations) {
    let clock = Arc::new(ManualClock::at_now());
    let sut = RconConfirmations::new(Arc::clone(&clock) as Arc<dyn Clock>);
    (clock, sut)
}

#[test]
fn a_command_seen_at_or_after_the_send_counts() {
    let (clock, sut) = manual();
    let sent_at = clock.now();
    sut.note(COMMAND, None);
    assert!(sut.confirmed_since(None, COMMAND, sent_at));
}

#[test]
fn a_command_seen_before_the_send_does_not_count() {
    // An earlier identical grant left a log line; this send must not be confirmed by it.
    let (clock, sut) = manual();
    sut.note(COMMAND, None);
    clock.advance(TimeDelta::seconds(30));
    let sent_at = clock.now();
    assert!(!sut.confirmed_since(None, COMMAND, sent_at));
}

#[test]
fn an_unseen_command_is_not_confirmed() {
    let (clock, sut) = manual();
    assert!(!sut.confirmed_since(None, COMMAND, clock.now()));
}

#[test]
fn spacing_differences_do_not_cause_a_false_miss() {
    let (clock, sut) = manual();
    let sent_at = clock.now();
    sut.note(
        "GiveMenu Bob 0010010000000000101000000000000   10001000000000",
        None,
    );
    assert!(sut.confirmed_since(None, COMMAND, sent_at));
}

#[tokio::test(flavor = "multi_thread")]
async fn confirmed_async_returns_true_once_the_line_arrives() {
    let sut = Arc::new(RconConfirmations::default());
    let sent_at = Utc::now();
    let noting = Arc::clone(&sut);
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(150)).await;
        noting.note(COMMAND, None);
    });
    assert!(
        sut.confirmed(None, COMMAND, sent_at, Duration::from_secs(2))
            .await
    );
}

#[tokio::test]
async fn confirmed_async_gives_up_when_the_line_never_arrives() {
    let sut = RconConfirmations::default();
    assert!(
        !sut.confirmed(None, COMMAND, Utc::now(), Duration::from_millis(200))
            .await
    );
}

#[test]
fn a_command_is_confirmed_per_server_log() {
    // One server echoing "Kick Bob" says nothing about whether the other two ran it.
    let (clock, sut) = manual();
    let sent_at = clock.now();
    sut.note("Kick Bob", Some("/a/Pavlov.log"));
    assert!(sut.confirmed_since(Some("/a/Pavlov.log"), "Kick Bob", sent_at));
    assert!(!sut.confirmed_since(Some("/b/Pavlov.log"), "Kick Bob", sent_at));
    assert!(sut.confirmed_since(None, "kick bob", sent_at));
}
