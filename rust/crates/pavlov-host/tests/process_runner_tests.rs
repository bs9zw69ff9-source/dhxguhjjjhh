//! Port of `ProcessRunnerTests.cs`, against a real `/bin/sh`. A caller "cancels" in Rust by
//! dropping the future, which the two cancellation tests do with an outer timeout.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::path::PathBuf;
use std::time::Duration;

use pavlov_host::process::{ProcessOutcome, argv, run};

async fn sh(script: &str, timeout: Duration) -> ProcessOutcome {
    run("/bin/sh", &argv(&["-c", script]), timeout, None).await
}

fn marker(tag: &str) -> (tempfile::TempDir, PathBuf) {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join(tag);
    (dir, path)
}

#[tokio::test(flavor = "multi_thread")]
async fn a_deadline_actually_kills_the_child() {
    let (_dir, marker) = marker("runner");
    let outcome = sh(
        &format!("sleep 2; touch '{}'", marker.display()),
        Duration::from_millis(400),
    )
    .await;

    assert!(outcome.started);
    assert!(outcome.timed_out);
    tokio::time::sleep(Duration::from_secs(3)).await;
    assert!(
        !marker.exists(),
        "the child outlived its deadline and kept running"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn the_process_tree_goes_with_it() {
    // sh spawns a child and waits - exactly the sudo case.
    let (_dir, marker) = marker("tree");
    let outcome = sh(
        &format!("sh -c \"sleep 2; touch '{}'\" & wait", marker.display()),
        Duration::from_millis(400),
    )
    .await;

    assert!(outcome.timed_out);
    tokio::time::sleep(Duration::from_secs(3)).await;
    assert!(!marker.exists(), "a grandchild survived the kill");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_large_stderr_does_not_deadlock() {
    const SIZE: usize = 200_000;
    let outcome = sh(
        &format!("head -c {SIZE} /dev/zero | tr '\\000' 'x' >&2; echo done"),
        Duration::from_secs(20),
    )
    .await;

    assert!(!outcome.timed_out, "the pipes deadlocked");
    assert!(outcome.ok());
    assert_eq!(outcome.stderr.len(), SIZE);
    assert_eq!(outcome.stdout.trim(), "done");
}

#[tokio::test(flavor = "multi_thread")]
async fn both_streams_come_back_separately() {
    let outcome = sh("echo out; echo err >&2", Duration::from_secs(10)).await;

    assert_eq!(outcome.stdout.trim(), "out");
    assert_eq!(outcome.stderr.trim(), "err");
    assert!(outcome.combined().contains("out"));
    assert!(outcome.combined().contains("err"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_non_zero_exit_is_reported_rather_than_thrown() {
    let outcome = sh("echo nope >&2; exit 3", Duration::from_secs(10)).await;

    assert!(outcome.started);
    assert!(!outcome.timed_out);
    assert!(!outcome.ok());
    assert_eq!(outcome.exit_code, 3);
    assert_eq!(outcome.stderr.trim(), "nope");
}

#[tokio::test(flavor = "multi_thread")]
async fn output_written_before_the_deadline_survives_it() {
    let outcome = sh("echo early; sleep 5", Duration::from_millis(500)).await;

    assert!(outcome.timed_out);
    assert_eq!(outcome.stdout.trim(), "early");
}

#[tokio::test(flavor = "multi_thread")]
async fn caller_cancellation_is_rethrown_rather_than_reported_as_a_timeout() {
    // The caller going away is not a command timeout: the caller sees its own cancellation
    // (here, the outer timeout elapsing), never a ProcessOutcome claiming one.
    let outer = tokio::time::timeout(
        Duration::from_millis(300),
        sh("sleep 5", Duration::from_secs(30)),
    )
    .await;

    assert!(outer.is_err());
}

#[tokio::test(flavor = "multi_thread")]
async fn cancelling_the_caller_still_kills_the_child() {
    let (_dir, marker) = marker("cancel");
    let script = format!("sleep 2; touch '{}'", marker.display());

    let outer = tokio::time::timeout(
        Duration::from_millis(300),
        sh(&script, Duration::from_secs(30)),
    )
    .await;
    assert!(outer.is_err());

    tokio::time::sleep(Duration::from_secs(3)).await;
    assert!(
        !marker.exists(),
        "cancelling the caller left the child running"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn stdin_is_written_and_closed_so_the_child_sees_it_and_exits() {
    let outcome = run(
        "/bin/cat",
        &[],
        Duration::from_secs(5),
        Some("steam:hunter2\n"),
    )
    .await;

    assert!(outcome.ok());
    assert_eq!(outcome.stdout, "steam:hunter2\n");
}

#[tokio::test(flavor = "multi_thread")]
async fn null_stdin_leaves_existing_call_sites_unaffected() {
    let outcome = sh("echo unaffected", Duration::from_secs(5)).await;

    assert!(outcome.ok());
    assert_eq!(outcome.stdout, "unaffected\n");
}
