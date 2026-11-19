//! Screening an address against the providers, with a cache that is the product (C#
//! `Vpn/VpnScreeningService.cs`).
//!
//! - A cached verdict is RE-DECIDED against today's thresholds, never re-screened: the counts
//!   are what was observed, the decision is policy, and policy can change.
//! - An outage is never cached as clean: no tier-1 answer means "try again next connection".
//! - One screening per address at a time: a lobby reconnecting after a map change asks once.
//! - The cache is decoded once per snapshot window, not once per join.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use chrono::TimeDelta;
use futures_util::FutureExt;
use futures_util::future::{BoxFuture, Shared, join_all};
use indexmap::IndexMap;
use pavlov_core::data::SerializedStore;
use pavlov_core::time::Clock;
use pavlov_core::vpn::{
    TierOutcome, VpnConsensus, VpnMerge, VpnRecord, VpnThresholds, ip_addresses,
};
use pavlov_storage::datasets::VPN_CHECKS;

use super::detectors::{DetectorOutcome, VpnDetector};
use super::geo::GeoLocator;
use crate::metrics::{MetricLabels, MetricsRegistry};

const SNAPSHOT_TTL: Duration = Duration::from_secs(30);
/// How long a verdict is trusted before the address is screened again.
pub const DEFAULT_CACHE_TTL: Duration = Duration::from_secs(30 * 24 * 60 * 60);

/// The stored cache: address to record, in insertion order.
pub type VpnCache = IndexMap<String, VpnRecord>;

type InFlight = Shared<BoxFuture<'static, Option<VpnRecord>>>;

/// One detector, asked directly by `/vpncheck`.
#[derive(Debug, Clone, PartialEq)]
pub struct DetectorProbe {
    pub name: String,
    pub tier: i32,
    pub answered: bool,
    pub flagged: Option<bool>,
    pub detail: Option<String>,
    pub duration_ms: f64,
    pub error: Option<String>,
    pub configured: bool,
}

impl DetectorProbe {
    #[must_use]
    pub fn role(&self) -> &'static str {
        if self.tier == 1 {
            "Regular check (every IP)"
        } else {
            "Final confirmation (flagged IPs only)"
        }
    }

    /// A sentence the operator can act on, never a bare "no verdict".
    #[must_use]
    pub fn outcome(&self) -> String {
        if !self.configured {
            "no API key set - not checked".into()
        } else if self.answered {
            self.detail.clone().unwrap_or_else(|| "answered".into())
        } else {
            self.error
                .clone()
                .unwrap_or_else(|| "no verdict, and no reason given".into())
        }
    }
}

/// The screening service. Shared behind an `Arc`.
pub struct VpnScreeningService {
    all: Vec<Arc<dyn VpnDetector>>,
    detectors: Vec<Arc<dyn VpnDetector>>,
    store: Arc<SerializedStore>,
    geo: Arc<dyn GeoLocator>,
    thresholds: VpnThresholds,
    metrics: Arc<MetricsRegistry>,
    clock: Arc<dyn Clock>,
    cache_ttl: Duration,
    snapshot: Mutex<Option<(Instant, Arc<VpnCache>)>>,
    in_flight: Mutex<HashMap<String, InFlight>>,
}

impl std::fmt::Debug for VpnScreeningService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let names: Vec<&str> = self.detectors.iter().map(|d| d.name()).collect();
        f.debug_struct("VpnScreeningService")
            .field("detectors", &names)
            .field("thresholds", &self.thresholds)
            .finish_non_exhaustive()
    }
}

fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Removes an address from the in-flight map however its screening ends.
struct InFlightGuard {
    service: Arc<VpnScreeningService>,
    ip: String,
}

impl Drop for InFlightGuard {
    fn drop(&mut self) {
        lock(&self.service.in_flight).remove(&self.ip);
    }
}

impl VpnScreeningService {
    #[must_use]
    pub fn new(
        detectors: Vec<Arc<dyn VpnDetector>>,
        store: Arc<SerializedStore>,
        geo: Arc<dyn GeoLocator>,
        metrics: Arc<MetricsRegistry>,
        clock: Arc<dyn Clock>,
    ) -> Self {
        let enabled = detectors.iter().filter(|d| d.enabled()).cloned().collect();
        Self {
            all: detectors,
            detectors: enabled,
            store,
            geo,
            thresholds: VpnThresholds::default().sanitised(),
            metrics,
            clock,
            cache_ttl: DEFAULT_CACHE_TTL,
            snapshot: Mutex::new(None),
            in_flight: Mutex::new(HashMap::new()),
        }
    }

    #[must_use]
    pub fn with_thresholds(mut self, thresholds: VpnThresholds) -> Self {
        self.thresholds = thresholds.sanitised();
        self
    }

    /// `Duration::ZERO` trusts a cached verdict forever.
    #[must_use]
    pub fn with_cache_ttl(mut self, ttl: Duration) -> Self {
        self.cache_ttl = ttl;
        self
    }

    /// Any detector configured at all.
    #[must_use]
    pub fn enabled(&self) -> bool {
        !self.detectors.is_empty()
    }

    /// The configured detectors.
    #[must_use]
    pub fn detectors(&self) -> &[Arc<dyn VpnDetector>] {
        &self.detectors
    }

    /// Every detector, configured or not.
    #[must_use]
    pub fn all_detectors(&self) -> &[Arc<dyn VpnDetector>] {
        &self.all
    }

    #[must_use]
    pub fn thresholds(&self) -> VpnThresholds {
        self.thresholds
    }

    fn tier(&self, tier: i32) -> Vec<Arc<dyn VpnDetector>> {
        self.detectors
            .iter()
            .filter(|d| d.tier() == tier)
            .cloned()
            .collect()
    }

    fn tier_count(&self, tier: i32) -> u32 {
        u32::try_from(self.tier(tier).len()).unwrap_or(u32::MAX)
    }

    /// The whole cache, decoded at most once per snapshot window. Racing readers may each
    /// rebuild once; the loser publishes an equally valid snapshot, and the join path takes
    /// no lock across a decode.
    #[must_use]
    pub fn load_cache(&self) -> Arc<VpnCache> {
        if let Some((at, snapshot)) = lock(&self.snapshot).as_ref()
            && at.elapsed() < SNAPSHOT_TTL
        {
            return Arc::clone(snapshot);
        }
        match self.store.read::<VpnCache>(VPN_CHECKS, IndexMap::new()) {
            Ok(fresh) => {
                let fresh = Arc::new(fresh);
                *lock(&self.snapshot) = Some((Instant::now(), Arc::clone(&fresh)));
                fresh
            }
            Err(error) => {
                // Not cached as a snapshot: the next reader tries the store again.
                tracing::warn!(%error, "could not read the VPN cache");
                Arc::new(IndexMap::new())
            }
        }
    }

    #[must_use]
    pub fn cached(&self, ip: &str) -> Option<VpnRecord> {
        self.load_cache().get(ip).cloned()
    }

    fn is_usable(&self, record: &VpnRecord) -> bool {
        // An older merge never saw the newer detectors.
        record.schema == VpnMerge::SCHEMA
            && (self.cache_ttl.is_zero()
                || TimeDelta::from_std(self.cache_ttl)
                    .is_ok_and(|ttl| self.clock.now() - record.checked_at < ttl))
    }

    fn redecide(&self, record: VpnRecord) -> VpnRecord {
        /* Nothing to count (a private address, or one saved with detection off) is left as it
        is: re-deciding it would replace a deliberate verdict with "every check failed". */
        if record.local || record.screen_answered == 0 {
            return record;
        }
        let screen = TierOutcome {
            readings: record.detectors.clone(),
            ..TierOutcome::counts(record.screen_hits, record.screen_answered, 0)
        };
        let confirm = (record.confirm_answered > 0)
            .then(|| TierOutcome::counts(record.confirm_hits, record.confirm_answered, 0));
        let decision = VpnConsensus::decide(
            Some(&screen),
            confirm.as_ref(),
            self.tier_count(2),
            self.thresholds,
        );
        if decision == record.decision {
            record
        } else {
            VpnRecord { decision, ..record }
        }
    }

    fn count(&self, outcome: &str) {
        self.metrics.increment(
            "vpn_checks_total",
            &MetricLabels::of("outcome", outcome),
            1.0,
            Some("VPN screenings by outcome"),
        );
    }

    /// The verdict for an address: cached (and re-decided), or screened now. `None` for a
    /// blank address, an outage, or a failure (all logged).
    pub async fn check(self: &Arc<Self>, ip: &str) -> Option<VpnRecord> {
        if ip.trim().is_empty() {
            return None;
        }
        if let Some(cached) = self.cached(ip).filter(|r| self.is_usable(r)) {
            self.count("cached");
            return Some(self.redecide(cached));
        }
        let flight = lock(&self.in_flight)
            .entry(ip.to_owned())
            .or_insert_with(|| {
                let guard = InFlightGuard {
                    service: Arc::clone(self),
                    ip: ip.to_owned(),
                };
                // Its own task: a caller giving up does not abandon the others waiting on it.
                let task = tokio::spawn(async move {
                    let record = guard.service.run(&guard.ip).await;
                    drop(guard);
                    record
                });
                async move { task.await.ok().flatten() }.boxed().shared()
            })
            .clone();
        flight.await
    }

    async fn run(&self, ip: &str) -> Option<VpnRecord> {
        let now = self.clock.now();
        if ip_addresses::is_unroutable(Some(ip)) {
            let local = ip_addresses::local_record(ip, now);
            self.save(&local).await;
            tracing::debug!(ip, "a private/unroutable address - skipped all detectors");
            self.count("local");
            return Some(local);
        }

        // Geolocation first: free, keyless, and useful whatever detector keys are set.
        let geo = match self.geo.locate(ip).await {
            Ok(geo) => geo,
            Err(error) => {
                tracing::warn!(ip, %error, "geolocation failed");
                None
            }
        };

        let mut screen = None;
        let mut confirm = None;
        if self.enabled() {
            let tier1 = self.run_tier(1, ip).await;
            if tier1.answered == 0 {
                tracing::warn!(
                    ip,
                    "every regular check failed - not caching, will retry on the next connection"
                );
                self.count("outage");
                return None;
            }
            if tier1.hits >= self.thresholds.screen_min {
                confirm = Some(self.run_tier(2, ip).await);
            }
            screen = Some(tier1);
        }

        let decision = VpnConsensus::decide(
            screen.as_ref(),
            confirm.as_ref(),
            self.tier_count(2),
            self.thresholds,
        );
        let record = VpnMerge::merge(
            ip,
            decision.clone(),
            screen.as_ref(),
            confirm.as_ref(),
            geo.as_ref(),
            now,
        );
        let breakdown: Vec<String> = record
            .detectors
            .iter()
            .map(|d| format!("{}:{}", d.name, if d.flagged { "HIT" } else { "clean" }))
            .collect();
        tracing::info!(
            "{ip} -> screen {}/{}{} | verdict={} | action={} ({}) | {}",
            record.screen_hits,
            record.screen_answered,
            if confirm.is_some() {
                format!(
                    " | confirm {}/{}",
                    record.confirm_hits, record.confirm_answered
                )
            } else {
                String::new()
            },
            decision.label(),
            if decision.actionable { "BAN" } else { "none" },
            decision.reason,
            breakdown.join(" ")
        );
        self.save(&record).await;
        self.count(
            &decision
                .label()
                .split(' ')
                .next()
                .unwrap_or_default()
                .to_lowercase(),
        );
        Some(record)
    }

    async fn run_tier(&self, tier: i32, ip: &str) -> TierOutcome {
        let detectors = self.tier(tier);
        if detectors.is_empty() {
            return TierOutcome::counts(0, 0, 0);
        }
        let outcomes: Vec<DetectorOutcome> = join_all(detectors.iter().map(|d| d.lookup(ip))).await;
        /* Said out loud, once per screen: a tier where nothing answered yields "not checked"
        everywhere downstream, with no way to tell a wrong key from a spent quota from no
        internet. */
        if outcomes.iter().all(|o| o.reading.is_none()) {
            let reasons: Vec<String> = detectors
                .iter()
                .zip(&outcomes)
                .map(|(d, o)| {
                    format!(
                        "{}: {}",
                        d.name(),
                        o.failure.as_deref().unwrap_or("no verdict")
                    )
                })
                .collect();
            tracing::info!(tier, ip, reasons = %reasons.join("; "), "no detector in this tier could screen the address");
        }
        TierOutcome::from_results(outcomes.into_iter().map(|o| o.reading))
    }

    async fn save(&self, record: &VpnRecord) {
        let written = self
            .store
            .update(VPN_CHECKS, IndexMap::new, |mut cache: VpnCache| {
                cache.insert(record.ip.clone(), record.clone());
                Some(cache)
            })
            .await;
        match written {
            /* PUBLISHED IMMEDIATELY, so the snapshot window never delays a verdict this process
            just wrote - or an address screened now is screened again within it, spending a
            lookup from a quota measured in tens a day. */
            Ok(result) => *lock(&self.snapshot) = Some((Instant::now(), Arc::new(result.value))),
            Err(error) => tracing::warn!(ip = %record.ip, %error, "could not save the VPN verdict"),
        }
    }

    /// Every detector asked directly, unconfigured ones listed rather than hidden: "no key
    /// set" and "broken" must not be the same blank space.
    pub async fn probe(&self, ip: &str) -> Vec<DetectorProbe> {
        join_all(self.all.iter().map(|detector| async move {
            if !detector.enabled() {
                return DetectorProbe {
                    name: detector.name().into(),
                    tier: detector.tier(),
                    answered: false,
                    flagged: None,
                    detail: None,
                    duration_ms: 0.0,
                    error: None,
                    configured: false,
                };
            }
            let started = Instant::now();
            let outcome = detector.lookup(ip).await;
            DetectorProbe {
                name: detector.name().into(),
                tier: detector.tier(),
                answered: outcome.reading.is_some(),
                flagged: outcome.reading.as_ref().map(|r| r.flagged),
                detail: outcome.reading.and_then(|r| r.detail),
                duration_ms: started.elapsed().as_secs_f64() * 1000.0,
                error: outcome.failure,
                configured: true,
            }
        }))
        .await
    }

    /// Why auto-ban can never fire in this configuration, or `None`.
    #[must_use]
    pub fn configuration_warning(&self) -> Option<String> {
        if !self.enabled() {
            return Some("No VPN detector is configured - screening is disabled entirely.".into());
        }
        if VpnConsensus::can_ever_auto_ban(self.tier_count(1), self.tier_count(2), self.thresholds)
        {
            return None;
        }
        Some(format!(
            "Only {} detector(s) are configured, but {} must agree before an address can be auto-banned. Auto-ban cannot trigger in this configuration - add another detector, or lower VPN_BAN_MIN.",
            self.detectors.len(),
            self.thresholds.ban_min
        ))
    }
}
