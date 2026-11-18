//! Port of `SecurityAlertTests.cs`, the host half: every outcome alerts, INCLUDING the ones
//! where nothing was done, and a sink that fails cannot cost the ban. The recipient list is in
//! `pavlov-config`, the direct-message sink in `pavlov-discord`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::collections::HashSet;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::Utc;
use common::{FakeHttp, file_store};
use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_config::{RconOptions, Secret};
use pavlov_core::data::SerializedStore;
use pavlov_core::evasion::{FlagMatch, FlagVerdict};
use pavlov_core::text::case::fold_case;
use pavlov_core::time::SystemClock;
use pavlov_core::vpn::{VpnDecision, VpnRecord};
use pavlov_host::feeds::FeedWebhooks;
use pavlov_host::logs::{FeedBridge, FeedBridgeParts, FlaggedJoin, IpTrackingService, LogLine};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::{
    AlertDelivery, AuditLog, AutoBanOutcome, BanService, EvasionResponder, MasterNames,
    SecurityAlert, SecurityAlertKind, SecurityAlertSink, SecurityAlerts, VpnResponder,
};
use pavlov_host::registry::RconRegistry;
use pavlov_host::server_labels::ServerLabels;

#[derive(Default)]
struct RecordingSink {
    alerts: Mutex<Vec<SecurityAlert>>,
}

impl SecurityAlertSink for RecordingSink {
    fn post<'a>(&'a self, alert: &'a SecurityAlert) -> BoxFuture<'a, Vec<AlertDelivery>> {
        self.alerts.lock().unwrap().push(alert.clone());
        async {
            vec![AlertDelivery {
                user: 1,
                delivered: true,
                problem: None,
            }]
        }
        .boxed()
    }
}

struct PanickingSink;

impl SecurityAlertSink for PanickingSink {
    fn post<'a>(&'a self, _: &'a SecurityAlert) -> BoxFuture<'a, Vec<AlertDelivery>> {
        async { panic!("the gateway is on fire") }.boxed()
    }
}

#[derive(Default)]
struct NoMasters {
    protected: HashSet<String>,
}

impl MasterNames for NoMasters {
    fn is_master(&self, _: &str) -> bool {
        false
    }
    fn is_exempt(&self, _: &str) -> bool {
        false
    }
    fn is_protected(&self, name: &str) -> bool {
        self.protected.contains(&fold_case(name))
    }
    fn exempt<'a>(&'a self, _: &'a str, _: Option<Duration>) -> BoxFuture<'a, ()> {
        async {}.boxed()
    }
}

struct Rig {
    _dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    metrics: Arc<MetricsRegistry>,
    sink: Arc<RecordingSink>,
    alerts: Arc<SecurityAlerts>,
}

impl Rig {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let sink = Arc::new(RecordingSink::default());
        let alerts = Arc::new(SecurityAlerts::new());
        alerts.use_sink(Arc::clone(&sink) as _);
        Self {
            store: file_store(dir.path()),
            _dir: dir,
            metrics: Arc::new(MetricsRegistry::default()),
            sink,
            alerts,
        }
    }

    fn alerts(&self) -> Vec<SecurityAlert> {
        self.sink.alerts.lock().unwrap().clone()
    }

    fn feeds(&self) -> Arc<FeedWebhooks> {
        Arc::new(FeedWebhooks::new(
            Arc::new(FakeHttp::canned(204, "")),
            Arc::clone(&self.metrics),
            Arc::new(SystemClock),
        ))
    }

    /// Port 1: nothing can reach a server, and none of these assertions are about RCON.
    fn bans(&self, masters: Arc<dyn MasterNames>) -> Arc<BanService> {
        let mut options = RconOptions::new("server1", "127.0.0.1", 1, Secret::new("x"));
        options.max_attempts = 1;
        options.command_timeout = Duration::from_millis(200);
        let rcon = Arc::new(RconRegistry::with_system_clock(
            &[options],
            Duration::from_secs(60),
            Arc::clone(&self.metrics),
        ));
        Arc::new(BanService::new(
            rcon,
            Arc::clone(&self.store),
            masters,
            Arc::new(SystemClock),
        ))
    }

    fn audit(&self) -> Arc<AuditLog> {
        Arc::new(AuditLog::new(
            Arc::clone(&self.store),
            Arc::new(SystemClock),
        ))
    }

    fn tracking(&self) -> Arc<IpTrackingService> {
        Arc::new(IpTrackingService::new(
            Arc::clone(&self.store),
            Arc::clone(&self.metrics),
            Arc::new(SystemClock),
        ))
    }

    fn evasion(&self, masters: NoMasters) -> Arc<EvasionResponder> {
        let masters: Arc<dyn MasterNames> = Arc::new(masters);
        EvasionResponder::attach(
            self.tracking(),
            self.bans(Arc::clone(&masters)),
            masters,
            Arc::clone(&self.store),
            self.audit(),
            self.feeds(),
            Arc::clone(&self.metrics),
            Arc::new(SystemClock),
            Some(Arc::clone(&self.alerts)),
        )
    }

    fn vpn(&self) -> VpnResponder {
        let masters: Arc<dyn MasterNames> = Arc::new(NoMasters::default());
        VpnResponder::new(
            self.bans(Arc::clone(&masters)),
            masters,
            Arc::clone(&self.store),
            self.audit(),
            self.feeds(),
            Arc::clone(&self.metrics),
            Arc::new(SystemClock),
            true,
            Some(Arc::clone(&self.alerts)),
        )
    }
}

fn join(name: &str) -> FlaggedJoin {
    FlaggedJoin {
        account_id: "0002913af4f445df86da1be6a2a01728".into(),
        name: Some(name.into()),
        ip: Some("203.0.113.9".into()),
        verdict: FlagVerdict {
            matched: FlagMatch::Ip,
            detail: Some("blacklisted ip 203.0.113.9".into()),
            manual: false,
        },
        at: Utc::now(),
    }
}

// ---- evasion ----

#[tokio::test]
async fn an_evasion_catch_is_alerted() {
    let r = Rig::new();
    r.evasion(NoMasters::default())
        .respond(&join("Evader"))
        .await;

    let alerts = r.alerts();
    assert_eq!(alerts.len(), 1);
    let alert = &alerts[0];
    assert_eq!(alert.kind, SecurityAlertKind::Evasion);
    assert_eq!(alert.player, "Evader");
    assert!(alert.headline.contains("blacklisted ip 203.0.113.9"));
    assert!(alert.action.contains("Banned"), "{}", alert.action);
    assert_eq!(alert.ip.as_deref(), Some("203.0.113.9"));
}

#[tokio::test]
async fn an_evasion_thatwasrefused_is_alerted_too() {
    // A protected account matching a flag: a stale flag, or a player the bot will never stop.
    let r = Rig::new();
    let mut masters = NoMasters::default();
    masters.protected.insert(fold_case("Evader"));

    let outcome = r.evasion(masters).respond(&join("Evader")).await;

    assert_eq!(outcome, AutoBanOutcome::Protected);
    let alerts = r.alerts();
    assert_eq!(alerts.len(), 1);
    assert!(alerts[0].action.contains("never-ban list"));
    assert!(alerts[0].action.contains("Nothing was done"));
}

// ---- vpn ----

fn decided(
    ip: &str,
    flagged: bool,
    confirmed: Option<bool>,
    actionable: bool,
    reason: &str,
    hits: u32,
) -> VpnRecord {
    VpnRecord {
        ip: ip.into(),
        decision: VpnDecision {
            flagged,
            confirmed,
            actionable,
            reason: reason.into(),
        },
        screen_hits: hits,
        screen_answered: 3,
        ..VpnRecord::default()
    }
}

fn actionable() -> VpnRecord {
    VpnRecord {
        organization: Some("M247 Europe".into()),
        ..decided(
            "45.10.20.30",
            true,
            Some(true),
            true,
            "confirmation agreed (1/1); 2 of 3 detector(s) flagged it",
            2,
        )
    }
}

fn below_threshold() -> VpnRecord {
    decided(
        "45.10.20.31",
        true,
        None,
        false,
        "1 of 3 detector(s) flagged it, 2 required",
        1,
    )
}

#[tokio::test]
async fn a_vpn_ban_is_alerted() {
    let r = Rig::new();
    r.vpn()
        .respond(
            "VpnUser",
            Some(&actionable()),
            Some("0002913af4f445df86da1be6a2a01728"),
            "join",
        )
        .await;

    let alerts = r.alerts();
    assert_eq!(alerts.len(), 1);
    assert_eq!(alerts[0].kind, SecurityAlertKind::Vpn);
    assert!(
        alerts[0].headline.contains("M247 Europe"),
        "{}",
        alerts[0].headline
    );
    assert!(alerts[0].action.contains("Banned"), "{}", alerts[0].action);
}

#[tokio::test]
async fn a_vpn_that_was_not_acted_on_is_alerted_with_the_reason() {
    let r = Rig::new();
    r.vpn()
        .respond("VpnUser", Some(&below_threshold()), None, "join")
        .await;

    let alerts = r.alerts();
    assert_eq!(alerts.len(), 1);
    assert!(alerts[0].action.contains("Not banned"));
    assert!(alerts[0].action.contains("under the threshold"));
}

#[tokio::test]
async fn one_connection_is_one_message_even_though_it_is_screened_twice() {
    let r = Rig::new();
    let responder = r.vpn();
    let record = below_threshold();

    responder
        .respond("VpnUser", Some(&record), None, "join")
        .await;
    responder
        .respond("VpnUser", Some(&record), None, "disconnect")
        .await;

    assert_eq!(r.alerts().len(), 1);
}

// ---- alt accounts ----

const LOG_FILE: &str = "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log";

/// One whole connection for one account, on a given address.
fn connection(time: &str, account: &str, name: &str, ip: &str) -> Vec<String> {
    vec![
        format!(
            "[2026.07.31-{time}:000]LogNet: NotifyAcceptingConnection accepted from: {ip}:7777"
        ),
        format!("[2026.07.31-{time}:100]LogNet: Login request: ?Name={name} userId: EOS:{account}"),
        format!(
            "[2026.07.31-{time}:900]LogNet: UChannel::Close: UniqueId: EOS:{account} RemoteAddr: {ip}:7777"
        ),
    ]
}

async fn connections(r: &Rig, connections: &[Vec<String>]) -> Arc<FeedBridge> {
    let tracking = r.tracking();
    let servers = Arc::new(ServerLabels::default());
    servers.assign(vec![LOG_FILE.to_owned()]);
    let bridge = FeedBridge::attach(
        Arc::clone(&tracking),
        r.feeds(),
        servers,
        FeedBridgeParts {
            alerts: Some(Arc::clone(&r.alerts)),
            ..FeedBridgeParts::default()
        },
    );
    for line in connections.iter().flatten() {
        tracking
            .ingest(&LogLine {
                file: LOG_FILE.into(),
                text: line.clone(),
            })
            .await;
    }
    // The handlers run off the ingest path; give them a moment.
    tokio::time::sleep(Duration::from_millis(100)).await;
    bridge
}

fn alt_alerts(r: &Rig) -> Vec<SecurityAlert> {
    r.alerts()
        .into_iter()
        .filter(|a| a.kind == SecurityAlertKind::Alts)
        .collect()
}

#[tokio::test]
async fn two_accounts_on_one_address_are_alerted() {
    let r = Rig::new();
    let _bridge = connections(
        &r,
        &[
            connection("13.33.00", "0002aaa", "FirstAccount", "203.0.113.5"),
            connection("13.40.00", "0002bbb", "SecondAccount", "203.0.113.5"),
        ],
    )
    .await;

    let alerts = alt_alerts(&r);
    assert_eq!(alerts.len(), 1, "{alerts:?}");
    assert_eq!(alerts[0].player, "SecondAccount");
    assert!(alerts[0].alts.contains(&"FirstAccount".to_owned()));
    // NOT A BAN, and the alert says so: a household or a carrier NAT shares an address too.
    assert!(alerts[0].action.contains("Nothing"));
}

#[tokio::test]
async fn the_same_alt_pair_is_not_reported_every_time_they_reconnect() {
    let r = Rig::new();
    let _bridge = connections(
        &r,
        &[
            connection("13.33.00", "0002aaa", "FirstAccount", "203.0.113.5"),
            connection("13.40.00", "0002bbb", "SecondAccount", "203.0.113.5"),
            // Well past the connection debounce, well inside the alert's day.
            connection("18.05.00", "0002bbb", "SecondAccount", "203.0.113.5"),
        ],
    )
    .await;

    assert_eq!(alt_alerts(&r).len(), 1);
}

#[tokio::test]
async fn one_account_on_its_own_address_is_not_an_alt() {
    let r = Rig::new();
    let _bridge = connections(
        &r,
        &[connection("13.33.00", "0002aaa", "Solo", "203.0.113.5")],
    )
    .await;
    assert!(alt_alerts(&r).is_empty());
}

// ---- delivery ----

#[tokio::test]
async fn a_sink_that_throws_cannot_cost_the_ban_the_caller_is_issuing() {
    let alerts = SecurityAlerts::new();
    alerts.use_sink(Arc::new(PanickingSink));
    let sample = SecurityAlert {
        kind: SecurityAlertKind::Evasion,
        player: "Evader".into(),
        headline: "matched".into(),
        action: "Banned".into(),
        account_id: None,
        ip: None,
        server: None,
        alts: Vec::new(),
        at: None,
    };
    assert!(alerts.post(&sample).await.is_empty());
}
