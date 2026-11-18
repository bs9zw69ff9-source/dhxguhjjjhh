//! The screening service end to end (C# `VpnScreeningService`, which the C# suite only reached
//! through the auto-ban wiring): cache, re-decision, dedupe, outage, local addresses.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::time::SystemClock;
use pavlov_core::vpn::{DetectorReading, VpnThresholds};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::vpn::{DetectorOutcome, GeoLocatorChain, VpnDetector, VpnScreeningService};
use pavlov_storage::datasets::VPN_CHECKS;

struct Fake {
    name: &'static str,
    tier: i32,
    flagged: Option<bool>, // None: no verdict
    delay: Duration,
    calls: AtomicUsize,
}

impl Fake {
    fn new(name: &'static str, tier: i32, flagged: Option<bool>) -> Arc<Self> {
        Arc::new(Self {
            name,
            tier,
            flagged,
            delay: Duration::ZERO,
            calls: AtomicUsize::new(0),
        })
    }

    fn slow(name: &'static str, flagged: bool) -> Arc<Self> {
        Arc::new(Self {
            name,
            tier: 1,
            flagged: Some(flagged),
            delay: Duration::from_millis(50),
            calls: AtomicUsize::new(0),
        })
    }

    fn calls(&self) -> usize {
        self.calls.load(Ordering::SeqCst)
    }
}

impl VpnDetector for Fake {
    fn name(&self) -> &'static str {
        self.name
    }
    fn tier(&self) -> i32 {
        self.tier
    }
    fn enabled(&self) -> bool {
        true
    }
    fn lookup<'a>(&'a self, _: &'a str) -> BoxFuture<'a, DetectorOutcome> {
        self.calls.fetch_add(1, Ordering::SeqCst);
        Box::pin(async move {
            tokio::time::sleep(self.delay).await;
            match self.flagged {
                Some(flagged) => DetectorOutcome::answered(DetectorReading {
                    name: self.name.into(),
                    tier: self.tier,
                    flagged,
                    vpn: Some(flagged),
                    detail: Some(format!("vpn:{flagged}")),
                    ..DetectorReading::default()
                }),
                None => DetectorOutcome::none("HTTP 403 - check the API key"),
            }
        })
    }
}

struct Setup {
    service: Arc<VpnScreeningService>,
    backend: Arc<MemoryBackend>,
}

fn setup(detectors: &[&Arc<Fake>], thresholds: VpnThresholds) -> Setup {
    let backend = Arc::new(MemoryBackend::new());
    let store = Arc::new(SerializedStore::new(Arc::clone(&backend) as _));
    let service = VpnScreeningService::new(
        detectors
            .iter()
            .map(|d| Arc::clone(d) as Arc<dyn VpnDetector>)
            .collect(),
        store,
        Arc::new(GeoLocatorChain::new(Vec::new())),
        Arc::new(MetricsRegistry::default()),
        Arc::new(SystemClock),
    )
    .with_thresholds(thresholds);
    Setup {
        service: Arc::new(service),
        backend,
    }
}

fn thresholds(screen_min: u32, confirm_min: u32, ban_min: u32) -> VpnThresholds {
    VpnThresholds {
        screen_min,
        confirm_min,
        ban_min,
    }
}

#[tokio::test]
async fn a_flagged_address_is_confirmed_and_cached() {
    let a = Fake::new("a", 1, Some(true));
    let b = Fake::new("b", 1, Some(true));
    let confirmer = Fake::new("ipqs", 2, Some(true));
    let s = setup(&[&a, &b, &confirmer], thresholds(1, 1, 2));

    let record = s.service.check("203.0.113.9").await.unwrap();

    assert!(record.decision.flagged);
    assert_eq!(record.decision.confirmed, Some(true));
    assert!(record.decision.actionable);
    assert_eq!((record.screen_hits, record.screen_answered), (2, 2));
    assert_eq!(confirmer.calls(), 1);

    // Asked again: from the cache, no detector called.
    let again = s.service.check("203.0.113.9").await.unwrap();
    assert_eq!(again.decision, record.decision);
    assert_eq!(a.calls(), 1);
    assert!(s.backend.writes() >= 1);
}

#[tokio::test]
async fn a_clean_screen_never_spends_the_confirming_quota() {
    let a = Fake::new("a", 1, Some(false));
    let confirmer = Fake::new("ipqs", 2, Some(true));
    let s = setup(&[&a, &confirmer], thresholds(1, 1, 2));

    let record = s.service.check("203.0.113.9").await.unwrap();

    assert!(!record.decision.flagged);
    assert_eq!(confirmer.calls(), 0);
}

#[tokio::test]
async fn an_outage_is_not_cached_as_clean() {
    let broken = Fake::new("a", 1, None);
    let s = setup(&[&broken], VpnThresholds::default());

    assert_eq!(s.service.check("203.0.113.9").await, None);
    assert_eq!(s.service.cached("203.0.113.9"), None);
    // And the next connection tries again.
    assert_eq!(s.service.check("203.0.113.9").await, None);
    assert_eq!(broken.calls(), 2);
}

#[tokio::test]
async fn a_private_address_asks_nobody() {
    let a = Fake::new("a", 1, Some(true));
    let s = setup(&[&a], VpnThresholds::default());

    let record = s.service.check("192.168.1.20").await.unwrap();

    assert!(record.local);
    assert!(!record.decision.flagged);
    assert_eq!(a.calls(), 0);
    assert!(s.service.cached("192.168.1.20").is_some());
}

#[tokio::test]
async fn a_blank_address_is_not_screened() {
    let a = Fake::new("a", 1, Some(true));
    let s = setup(&[&a], VpnThresholds::default());
    assert_eq!(s.service.check("  ").await, None);
    assert_eq!(a.calls(), 0);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn a_lobby_reconnecting_at_once_screens_each_address_once() {
    let a = Fake::slow("a", true);
    let s = setup(&[&a], thresholds(1, 1, 1));

    let joins: Vec<_> = (0..10)
        .map(|_| {
            let service = Arc::clone(&s.service);
            tokio::spawn(async move { service.check("203.0.113.9").await })
        })
        .collect();
    for join in joins {
        assert!(join.await.unwrap().unwrap().decision.flagged);
    }
    assert_eq!(a.calls(), 1);
}

#[tokio::test]
async fn a_cached_verdict_is_redecided_under_todays_thresholds() {
    // Two detectors agreed, which banned under ban_min 2; the owner raises it to 3.
    let a = Fake::new("a", 1, Some(true));
    let b = Fake::new("b", 1, Some(true));
    let s = setup(&[&a, &b], thresholds(1, 1, 2));
    assert!(
        s.service
            .check("203.0.113.9")
            .await
            .unwrap()
            .decision
            .actionable
    );

    let backend = Arc::clone(&s.backend);
    let store = Arc::new(SerializedStore::new(backend as _));
    let stricter = VpnScreeningService::new(
        vec![Arc::clone(&a) as Arc<dyn VpnDetector>, Arc::clone(&b) as _],
        store,
        Arc::new(GeoLocatorChain::new(Vec::new())),
        Arc::new(MetricsRegistry::default()),
        Arc::new(SystemClock),
    )
    .with_thresholds(thresholds(1, 1, 3));

    let redecided = Arc::new(stricter).check("203.0.113.9").await.unwrap();

    assert!(redecided.decision.flagged);
    assert!(!redecided.decision.actionable);
    assert_eq!(a.calls(), 1, "re-decided from the counts, not re-screened");
}

#[tokio::test]
async fn an_older_schema_is_screened_again() {
    let a = Fake::new("a", 1, Some(false));
    let s = setup(&[&a], VpnThresholds::default());
    s.backend.seed(
        VPN_CHECKS,
        &format!(r#"{{"203.0.113.9":{{"ip":"203.0.113.9","schema":1,"screenAnswered":1,"checkedAt":{}}}}}"#, chrono::Utc::now().timestamp_millis()),
    );

    let _ = s.service.check("203.0.113.9").await.unwrap();

    assert_eq!(a.calls(), 1);
}

#[test]
fn a_configuration_that_can_never_ban_says_so() {
    let only = Fake::new("a", 1, Some(true));
    let warning = setup(&[&only], thresholds(1, 1, 2))
        .service
        .configuration_warning()
        .unwrap();
    assert!(warning.contains("Auto-ban cannot trigger"), "{warning}");

    let none = setup(&[], VpnThresholds::default())
        .service
        .configuration_warning()
        .unwrap();
    assert!(none.contains("disabled entirely"), "{none}");

    let two = Fake::new("b", 1, Some(true));
    assert_eq!(
        setup(&[&only, &two], thresholds(1, 1, 2))
            .service
            .configuration_warning(),
        None
    );
}

#[tokio::test]
async fn the_probe_asks_every_detector_and_says_why_each_failed() {
    let good = Fake::new("good", 1, Some(true));
    let bad = Fake::new("bad", 2, None);
    let s = setup(&[&good, &bad], VpnThresholds::default());

    let probes = s.service.probe("203.0.113.9").await;

    assert_eq!(probes.len(), 2);
    assert_eq!(probes[0].outcome(), "vpn:true");
    assert_eq!(probes[1].outcome(), "HTTP 403 - check the API key");
    assert_eq!(probes[1].role(), "Final confirmation (flagged IPs only)");
}
