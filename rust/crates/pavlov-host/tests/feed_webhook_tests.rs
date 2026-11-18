//! Port of `FeedWebhookTests.cs`: registering a feed webhook, and being able to tell what it
//! is doing. Registration touches no network; a post never waits on Discord; a dead webhook
//! is reported, and not retried on every post.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::{Duration, Instant};

use common::FakeHttp;
use pavlov_core::time::SystemClock;
use pavlov_host::feeds::{CONNECT, FeedWebhooks, JOIN, KILL};
use pavlov_host::metrics::MetricsRegistry;

const PLAUSIBLE_URL: &str = "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnop";

fn build(http: &Arc<FakeHttp>) -> Arc<FeedWebhooks> {
    Arc::new(FeedWebhooks::new(
        Arc::clone(http) as _,
        Arc::new(MetricsRegistry::default()),
        Arc::new(SystemClock),
    ))
}

fn quiet() -> Arc<FakeHttp> {
    Arc::new(FakeHttp::canned(204, ""))
}

async fn wait_for(mut done: impl FnMut() -> bool) -> bool {
    let deadline = Instant::now() + Duration::from_secs(5);
    while Instant::now() < deadline {
        if done() {
            return true;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    done()
}

#[test]
fn registering_does_not_touch_the_network() {
    let http = quiet();
    let feeds = build(&http);

    feeds.register(CONNECT, Some(PLAUSIBLE_URL));

    assert!(feeds.is_configured(CONNECT));
    assert_eq!(http.calls(), 0);
}

#[test]
fn an_unset_url_is_a_choice_not_a_fault() {
    let feeds = build(&quiet());
    feeds.register(CONNECT, None);

    assert!(!feeds.is_configured(CONNECT));
    assert_eq!(feeds.status()[CONNECT], "not configured");
}

#[test]
fn whitespace_counts_as_unset() {
    let feeds = build(&quiet());
    feeds.register(KILL, Some("   "));
    assert_eq!(feeds.status()[KILL], "not configured");
}

#[test]
fn something_that_is_not_a_webhook_url_is_rejected_immediately() {
    for url in [
        "not a url at all",
        "https://example.com/not-a-webhook",
        "discord.com/api/webhooks/1/2",
    ] {
        let feeds = build(&quiet());
        feeds.register(CONNECT, Some(url));

        assert!(!feeds.is_configured(CONNECT), "{url}");
        assert!(feeds.status()[CONNECT].contains("invalid URL"), "{url}");
    }
}

#[test]
fn a_url_with_surrounding_whitespace_still_works() {
    let feeds = build(&quiet());
    feeds.register(CONNECT, Some(&format!("  {PLAUSIBLE_URL}\n")));
    assert!(feeds.is_configured(CONNECT));
}

#[test]
fn every_feed_reports_a_status() {
    let feeds = build(&quiet());
    feeds.register(JOIN, Some(PLAUSIBLE_URL));
    feeds.register(CONNECT, None);
    feeds.register(KILL, Some("nonsense"));

    let status = feeds.status();
    assert_eq!(status.len(), 3);
    assert!(status.values().all(|s| !s.trim().is_empty()));
    // Three states, three fixes, and they must not read alike.
    assert_ne!(status[JOIN], status[CONNECT]);
    assert_ne!(status[CONNECT], status[KILL]);
}

#[tokio::test]
async fn posting_to_an_unconfigured_feed_is_silent_and_harmless() {
    let http = quiet();
    let feeds = build(&http);
    feeds.start();
    feeds.register(CONNECT, None);

    feeds.post(CONNECT, "hello");
    feeds.shutdown().await;

    assert_eq!(http.calls(), 0);
}

#[tokio::test]
async fn posting_to_a_dead_webhook_never_throws() {
    // The post is dropped, and the failure is REPORTED by the background sender.
    let http = Arc::new(FakeHttp::canned(
        404,
        r#"{"message":"Unknown Webhook","code":10015}"#,
    ));
    let feeds = build(&http);
    feeds.start();
    feeds.register(CONNECT, Some(PLAUSIBLE_URL));

    feeds.post(CONNECT, "hello");

    assert!(
        wait_for(|| feeds.status()[CONNECT].contains("not delivering")).await,
        "{}",
        feeds.status()[CONNECT]
    );
    assert!(feeds.status()[CONNECT].contains("no longer exists"));
    feeds.shutdown().await;
}

#[tokio::test]
async fn posting_never_waits_on_discord() {
    // The log-tail tick calls this for every kill and join.
    let http = Arc::new(FakeHttp::hanging());
    let feeds = build(&http);
    feeds.start();
    feeds.register(CONNECT, Some(PLAUSIBLE_URL));

    let started = Instant::now();
    feeds.post(CONNECT, "hello");

    assert!(started.elapsed() < Duration::from_millis(50));
}

#[tokio::test]
async fn a_dead_webhook_is_not_retried_on_every_post() {
    let http = Arc::new(FakeHttp::canned(404, "{}"));
    let feeds = build(&http);
    feeds.start();
    feeds.register(CONNECT, Some(PLAUSIBLE_URL));

    feeds.post(CONNECT, "first"); // pays for the attempt
    assert!(wait_for(|| http.calls() == 1).await);
    feeds.post(CONNECT, "second");
    feeds.shutdown().await;

    assert_eq!(
        http.calls(),
        1,
        "the second post retried instead of backing off"
    );
}

#[test]
fn the_join_and_connect_feeds_stay_distinct() {
    // Connect carries player addresses; collapsing them publishes addresses.
    let feeds = build(&quiet());
    feeds.register(JOIN, Some(PLAUSIBLE_URL));
    feeds.register(CONNECT, None);

    assert!(feeds.is_configured(JOIN));
    assert!(!feeds.is_configured(CONNECT));
}

#[tokio::test]
async fn a_post_goes_out_with_mentions_forbidden_and_long_lines_cut() {
    // Rust-only: the wire. A name containing @everyone must not ping the server.
    let http = quiet();
    let feeds = build(&http);
    feeds.start();
    feeds.register(JOIN, Some(PLAUSIBLE_URL));

    feeds.post(JOIN, &format!("@everyone {}", "x".repeat(3000)));
    assert!(wait_for(|| http.calls() == 1).await);
    feeds.shutdown().await;

    let request = http.requests.lock().unwrap()[0].clone();
    assert_eq!(request.url, PLAUSIBLE_URL);
    let body: serde_json::Value = serde_json::from_str(request.body.as_deref().unwrap()).unwrap();
    assert_eq!(body["allowed_mentions"]["parse"], serde_json::json!([]));
    let content = body["content"].as_str().unwrap();
    assert!(
        content.chars().count() <= 1902,
        "{}",
        content.chars().count()
    );
    assert!(content.ends_with(" …"));
    assert_eq!(feeds.status()[JOIN], "delivering");
}
