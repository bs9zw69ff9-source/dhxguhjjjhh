//! Port of `VpnAutoBanWiringTests.cs`: from a log line to a VPN ban, with NO connect webhook
//! configured, the deployment the C# bug hid in (an unset webhook switched auto-ban off).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use common::{FakeHttp, file_store};
use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::BanRecord;
use pavlov_core::time::SystemClock;
use pavlov_core::vpn::DetectorReading;
use pavlov_host::feeds::FeedWebhooks;
use pavlov_host::logs::{FeedBridge, FeedBridgeParts, IpTrackingService, LogLine};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::{AuditLog, BanService, MasterNames, VpnResponder};
use pavlov_host::registry::RconRegistry;
use pavlov_host::server_labels::ServerLabels;
use pavlov_host::vpn::{DetectorOutcome, GeoLocatorChain, VpnDetector, VpnScreeningService};
use pavlov_storage::datasets::TEMP_BANS;

const FILE: &str = "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log";
const ACCEPT: &str =
    "[2026.07.31-13.33.00:000]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777";
const OTHER_ACCEPT: &str =
    "[2026.07.31-13.33.01:000]LogNet: NotifyAcceptingConnection accepted from: 198.51.100.9:7777";
/// Two seconds after the accept: what a real handshake looks like.
const LOGIN: &str =
    "[2026.07.31-13.33.02:000]LogNet: Login request: ?Name=Pkdestroy userId: EOS:0002abc";
/// Past the correlator's window, so the address for it is unknown.
const LATE_LOGIN: &str =
    "[2026.07.31-13.33.45:000]LogNet: Login request: ?Name=Pkdestroy userId: EOS:0002abc";
const CLOSE: &str = "[2026.07.31-13.33.50:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777";
const NAME: &str = "Pkdestroy";
const ACCOUNT: &str = "0002abc";

struct FakeDetector(&'static str, bool);

impl VpnDetector for FakeDetector {
    fn name(&self) -> &'static str {
        self.0
    }
    fn tier(&self) -> i32 {
        1
    }
    fn enabled(&self) -> bool {
        true
    }
    fn lookup<'a>(&'a self, _: &'a str) -> BoxFuture<'a, DetectorOutcome> {
        let flagged = self.1;
        Box::pin(async move {
            DetectorOutcome::answered(DetectorReading {
                name: self.0.into(),
                tier: 1,
                flagged,
                vpn: flagged.then_some(true),
                detail: Some(if flagged {
                    "vpn".into()
                } else {
                    "clean".into()
                }),
                ..DetectorReading::default()
            })
        })
    }
}

struct NoMasters;

impl MasterNames for NoMasters {
    fn is_master(&self, _: &str) -> bool {
        false
    }
    fn is_exempt(&self, _: &str) -> bool {
        false
    }
    fn is_protected(&self, _: &str) -> bool {
        false
    }
    fn exempt<'a>(&'a self, _: &'a str, _: Option<Duration>) -> BoxFuture<'a, ()> {
        Box::pin(async {})
    }
}

struct Fixture {
    _dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    tracking: Arc<IpTrackingService>,
    _bridge: Arc<FeedBridge>,
}

impl Fixture {
    /// Two detectors that agree, because one is deliberately never enough to ban.
    fn new(flagged: bool) -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = file_store(dir.path());
        let metrics = Arc::new(MetricsRegistry::default());
        let clock = Arc::new(SystemClock);
        let tracking = Arc::new(IpTrackingService::new(
            Arc::clone(&store),
            Arc::clone(&metrics),
            clock.clone(),
        ));
        // NOTHING CONFIGURED.
        let feeds = Arc::new(FeedWebhooks::new(
            Arc::new(FakeHttp::canned(204, "")),
            Arc::clone(&metrics),
            clock.clone(),
        ));
        let servers = Arc::new(ServerLabels::default());
        servers.assign(vec![FILE.to_owned()]);
        let vpn = Arc::new(VpnScreeningService::new(
            vec![
                Arc::new(FakeDetector("alpha", flagged)),
                Arc::new(FakeDetector("beta", flagged)),
            ],
            Arc::clone(&store),
            Arc::new(GeoLocatorChain::new(Vec::new())),
            Arc::clone(&metrics),
            clock.clone(),
        ));
        // No RCON servers: the ban record is under test, not the wire.
        let rcon = Arc::new(RconRegistry::with_system_clock(
            &[],
            Duration::from_secs(60),
            Arc::clone(&metrics),
        ));
        let bans = Arc::new(BanService::new(
            rcon,
            Arc::clone(&store),
            Arc::new(NoMasters),
            clock.clone(),
        ));
        let responder = Arc::new(VpnResponder::new(
            bans,
            Arc::new(NoMasters),
            Arc::clone(&store),
            Arc::new(AuditLog::new(Arc::clone(&store), clock.clone())),
            Arc::clone(&feeds),
            metrics,
            clock,
            true,
            None,
        ));
        let bridge = FeedBridge::attach(
            Arc::clone(&tracking),
            feeds,
            servers,
            FeedBridgeParts {
                vpn: Some(vpn),
                masters: Some(Arc::new(NoMasters)),
                vpn_bans: Some(responder),
                ..FeedBridgeParts::default()
            },
        );
        Self {
            _dir: dir,
            store,
            tracking,
            _bridge: bridge,
        }
    }

    async fn feed(&self, text: &str) {
        self.tracking.ingest(&LogLine::new(FILE, text)).await;
    }

    async fn connect(&self) {
        self.feed(ACCEPT).await;
        self.feed(LOGIN).await;
        self.feed(CLOSE).await;
    }

    fn bans(&self) -> Vec<BanRecord> {
        self.store.read(TEMP_BANS, Vec::new()).unwrap()
    }
}

#[tokio::test]
async fn a_flagged_address_is_banned_with_no_connect_webhook_configured() {
    let f = Fixture::new(true);
    f.connect().await;

    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert_eq!(bans[0].player_id, NAME);
    assert!(bans[0].permanent);
    assert_eq!(bans[0].moderator.as_deref(), Some("auto"));
}

#[tokio::test]
async fn the_ban_carries_the_account_id_from_the_disconnect_line() {
    let f = Fixture::new(true);
    f.connect().await;
    assert_eq!(f.bans()[0].unique_id.as_deref(), Some(ACCOUNT));
}

#[tokio::test]
async fn a_clean_address_is_not_banned() {
    let f = Fixture::new(false);
    f.connect().await;
    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn a_flagged_address_is_banned_on_the_join_line_before_any_disconnect() {
    let f = Fixture::new(true);
    f.feed(ACCEPT).await;
    f.feed(LOGIN).await;

    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert_eq!(bans[0].player_id, NAME);
    assert_eq!(bans[0].unique_id.as_deref(), Some(ACCOUNT));
}

#[tokio::test]
async fn an_ambiguous_correlation_is_not_acted_on_at_join_but_still_is_at_disconnect() {
    // Banning on a guess bans whoever else was connecting at that moment.
    let f = Fixture::new(true);
    f.feed(ACCEPT).await;
    f.feed(OTHER_ACCEPT).await;
    f.feed(LOGIN).await;
    assert!(f.bans().is_empty());

    f.feed(CLOSE).await;
    assert_eq!(f.bans().len(), 1);
}

#[tokio::test]
async fn a_whole_connection_produces_one_ban_rather_than_two() {
    let f = Fixture::new(true);
    f.connect().await;
    assert_eq!(f.bans().len(), 1);
}

#[tokio::test]
async fn a_login_too_long_after_the_accept_is_left_to_the_disconnect() {
    let f = Fixture::new(true);
    f.feed(ACCEPT).await;
    f.feed(LATE_LOGIN).await;
    assert!(f.bans().is_empty());

    f.feed(CLOSE).await;
    assert_eq!(f.bans().len(), 1);
}

#[tokio::test]
async fn a_nameless_join_is_left_to_the_disconnect() {
    // Master protection is BY NAME: a nameless login cannot be acted on here.
    let f = Fixture::new(true);
    f.feed(ACCEPT).await;
    f.feed("[2026.07.31-13.33.02:000]LogNet: Login request: userId: EOS:0002abc")
        .await;
    assert!(f.bans().is_empty());
}
