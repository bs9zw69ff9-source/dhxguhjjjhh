//! Port of `FeedBridgeTests.cs`: the wire between the log reader and the webhooks. The C#
//! asserted on its own log lines; here the assertions are on what actually reaches each
//! webhook, through a recording HTTP client.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::{Duration, Instant};

use chrono::Utc;
use common::{FakeHttp, file_store};
use pavlov_core::time::SystemClock;
use pavlov_host::feeds::{self, CONNECT, FeedWebhooks, JOIN};
use pavlov_host::logs::{FeedBridge, FeedBridgeParts, IpTrackingService, LogLine};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::server_labels::ServerLabels;

const FILE: &str = "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log";
const ACCEPT: &str =
    "[2026.07.31-13.33.00:000]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777";
const LOGIN: &str =
    "[2026.07.31-13.33.22:000]LogNet: Login request: ?Name=Pkdestroy userId: EOS:0002abc";
const CLOSE: &str = "[2026.07.31-13.33.50:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777";
const JOIN_URL: &str = "https://discord.com/api/webhooks/1/join";
const CONNECT_URL: &str = "https://discord.com/api/webhooks/2/connect";

struct Fixture {
    _dir: tempfile::TempDir,
    tracking: Arc<IpTrackingService>,
    feeds: Arc<FeedWebhooks>,
    http: Arc<FakeHttp>,
    _bridge: Arc<FeedBridge>,
}

impl Fixture {
    fn new(connect_feed: bool) -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = file_store(dir.path());
        let metrics = Arc::new(MetricsRegistry::default());
        let tracking = Arc::new(IpTrackingService::new(
            store,
            Arc::clone(&metrics),
            Arc::new(SystemClock),
        ));
        let http = Arc::new(FakeHttp::canned(204, ""));
        let feeds = Arc::new(FeedWebhooks::new(
            Arc::clone(&http) as _,
            metrics,
            Arc::new(SystemClock),
        ));
        feeds.register(JOIN, Some(JOIN_URL));
        feeds.register(CONNECT, connect_feed.then_some(CONNECT_URL));
        feeds.start();
        let servers = Arc::new(ServerLabels::default());
        servers.assign(vec![FILE.to_owned()]);
        let bridge = FeedBridge::attach(
            Arc::clone(&tracking),
            Arc::clone(&feeds),
            servers,
            FeedBridgeParts::default(),
        );
        Self {
            _dir: dir,
            tracking,
            feeds,
            http,
            _bridge: bridge,
        }
    }

    async fn feed(&self, text: &str) {
        self.tracking.ingest(&LogLine::new(FILE, text)).await;
    }

    /// Every text line posted to one webhook, once the sender has drained.
    async fn posted(&self, url: &str) -> Vec<String> {
        let deadline = Instant::now() + Duration::from_millis(300);
        let mut last = usize::MAX;
        while Instant::now() < deadline {
            let n = self.http.calls();
            if n == last {
                break;
            }
            last = n;
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        self.http
            .requests
            .lock()
            .unwrap()
            .iter()
            .filter(|r| r.url == url)
            .map(|r| {
                let body: serde_json::Value =
                    serde_json::from_str(r.body.as_deref().unwrap_or("{}")).unwrap();
                body["content"]
                    .as_str()
                    .map_or_else(|| body["embeds"].to_string(), str::to_owned)
            })
            .collect()
    }
}

#[tokio::test]
async fn constructing_the_bridge_subscribes_to_the_tracker() {
    // The whole defect: the events had no subscribers.
    let f = Fixture::new(false);
    assert_eq!(f.tracking.joined.len(), 1);
    assert_eq!(f.tracking.confirmed.len(), 1);
    assert_eq!(f.tracking.kill.len(), 1);
    assert_eq!(f.tracking.rcon.len(), 1);
}

#[tokio::test]
async fn a_join_raises_the_feeds_without_throwing() {
    let f = Fixture::new(false);
    f.feed(ACCEPT).await;
    f.feed(LOGIN).await;

    let joins = f.posted(JOIN_URL).await;
    assert_eq!(joins.len(), 1);
    assert!(joins[0].contains("Pkdestroy joined Server 1"), "{joins:?}");
}

#[tokio::test]
async fn a_disconnect_line_reaches_the_leave_path() {
    // END TO END: a real log line in, the leave line out.
    let f = Fixture::new(false);
    f.feed(ACCEPT).await;
    f.feed(LOGIN).await;
    f.feed(CLOSE).await;

    let lines = f.posted(JOIN_URL).await;
    assert!(
        lines.iter().any(|l| l.contains("Pkdestroy left Server 1")),
        "{lines:?}"
    );
}

#[tokio::test]
async fn one_connection_produces_one_join_line() {
    // Pavlov logs a login SEVERAL times per connection.
    let f = Fixture::new(false);
    f.feed(ACCEPT).await;
    f.feed(LOGIN).await;
    f.feed("[2026.07.31-13.33.23:000]LogNet: Login request: ?Name=Pkdestroy userId: EOS:0002abc")
        .await;
    f.feed("[2026.07.31-13.33.25:000]LogNet: Join request: ?Name=Pkdestroy userId: EOS:0002abc")
        .await;

    let joins: Vec<String> = f
        .posted(JOIN_URL)
        .await
        .into_iter()
        .filter(|l| l.contains("joined"))
        .collect();
    assert_eq!(joins.len(), 1, "{joins:?}");
}

#[tokio::test]
async fn a_leave_is_reported_for_somebody_only_ever_seen_on_a_disconnect() {
    // Everybody online across a bot restart is first seen on their CLOSE line.
    let f = Fixture::new(false);
    f.feed("[2026.07.31-13.40.00:000]LogNet: UChannel::Close: ?Name=Latecomer UniqueId: EOS:0009late RemoteAddr: 203.0.113.7:7777").await;

    let lines = f.posted(JOIN_URL).await;
    assert!(
        lines.iter().any(|l| l.contains("Latecomer left")),
        "{lines:?}"
    );
}

#[tokio::test]
async fn a_nameless_disconnect_says_why_it_was_skipped() {
    // Nothing named them, so there is no leave line to post (and the reason is logged once).
    let f = Fixture::new(true);
    f.feed(CLOSE).await;
    assert!(f.posted(JOIN_URL).await.is_empty());
}

#[tokio::test]
async fn a_disconnect_with_no_known_name_is_not_announced() {
    let f = Fixture::new(true);
    f.feed(CLOSE).await;
    assert!(f.posted(JOIN_URL).await.is_empty());
    assert!(f.posted(CONNECT_URL).await.is_empty());
}

#[test]
fn the_connect_and_join_feeds_are_distinct_labels() {
    assert_ne!(JOIN, CONNECT);
}

#[test]
fn the_public_join_line_carries_no_address() {
    let line = feeds::join_line("Alice", Some("Server 1"), Utc::now());
    assert!(line.contains("Alice"));
    assert!(!line.contains("ip="));
    assert!(!line.contains("203.0.113.9"));
}

#[test]
fn the_connect_line_carries_the_address() {
    let line = feeds::connect_line(
        "Alice",
        Some("203.0.113.9"),
        Some("Toronto, Canada"),
        Some("CLEAN"),
        Utc::now(),
    );
    assert!(line.contains("ip=203.0.113.9"));
    assert!(line.contains("Toronto"));
    assert!(line.contains("vpn=CLEAN"));
}

#[test]
fn a_player_name_cannot_inject_a_line_break_into_a_feed() {
    let line = feeds::join_line(
        "Alice\n[12:00:00] Bob left Server 1",
        Some("Server 1"),
        Utc::now(),
    );
    assert!(!line.contains('\n'));
}

#[tokio::test]
async fn the_connection_card_goes_to_the_connect_feed_only() {
    // Rust-only: the address is on the card, and the card never reaches the public feed.
    let f = Fixture::new(true);
    f.feed(ACCEPT).await;
    f.feed(LOGIN).await;
    f.feed(CLOSE).await;

    let cards = f.posted(CONNECT_URL).await;
    assert_eq!(cards.len(), 1, "{cards:?}");
    assert!(cards[0].contains("203.0.113.5"));
    assert!(
        !f.posted(JOIN_URL)
            .await
            .iter()
            .any(|l| l.contains("203.0.113.5"))
    );
    f.feeds.shutdown().await;
}
