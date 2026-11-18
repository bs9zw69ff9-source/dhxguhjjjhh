//! The last 25 hours of probes per server, for uptime and outage figures (C#
//! `Monitoring/MonitorHistory.cs`).

use std::sync::Arc;
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::data::{SerializedStore, StoreError};
use pavlov_core::monitoring::HealthState;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::Clock;
use pavlov_storage::datasets::MONITOR_HISTORY;
use serde::{Deserialize, Deserializer, Serialize, Serializer};

const RETENTION: TimeDelta = TimeDelta::hours(25);
const MAX_SAMPLES_PER_SERVER: usize = 4000;

/// One probe, as kept.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MonitorSample {
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub at: DateTime<Utc>,
    /// Stored as the C# enum's number, so both bots read one file.
    #[serde(with = "state_number")]
    pub state: HealthState,
    #[serde(default)]
    pub latency_ms: Option<f64>,
    #[serde(default)]
    pub players: Option<u32>,
    pub rcon_ok: bool,
    #[serde(default)]
    pub unexpected_restart: bool,
}

mod state_number {
    use super::{Deserialize, Deserializer, HealthState, Serializer};

    pub fn serialize<S: Serializer>(state: &HealthState, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_u8(match state {
            HealthState::Unknown => 0,
            HealthState::Online => 1,
            HealthState::Degraded => 2,
            HealthState::Offline => 3,
            HealthState::Reconnecting => 4,
        })
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<HealthState, D::Error> {
        Ok(match u8::deserialize(d)? {
            1 => HealthState::Online,
            2 => HealthState::Degraded,
            3 => HealthState::Offline,
            4 => HealthState::Reconnecting,
            _ => HealthState::Unknown,
        })
    }
}

/// A window's figures.
#[derive(Debug, Clone, PartialEq)]
pub struct HealthStats {
    pub uptime_percent: f64,
    pub rcon_failures: usize,
    pub unexpected_restarts: usize,
    pub average_latency_ms: Option<f64>,
    pub longest_outage: Duration,
    pub samples: usize,
}

impl HealthStats {
    #[must_use]
    pub fn empty() -> Self {
        Self {
            uptime_percent: 0.0,
            rcon_failures: 0,
            unexpected_restarts: 0,
            average_latency_ms: None,
            longest_outage: Duration::ZERO,
            samples: 0,
        }
    }
}

type Stored = IgnoreCaseMap<Option<Vec<Option<MonitorSample>>>>;

/// The history.
pub struct MonitorHistory {
    store: Arc<SerializedStore>,
    clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for MonitorHistory {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MonitorHistory").finish_non_exhaustive()
    }
}

fn samples(list: Option<&Option<Vec<Option<MonitorSample>>>>) -> Vec<MonitorSample> {
    list.and_then(Option::as_ref)
        .map(|l| l.iter().flatten().cloned().collect())
        .unwrap_or_default()
}

impl MonitorHistory {
    #[must_use]
    pub fn new(store: Arc<SerializedStore>, clock: Arc<dyn Clock>) -> Self {
        Self { store, clock }
    }

    /// Keep one probe; older than 25 hours, or past 4000 per server, goes.
    ///
    /// # Errors
    /// A store failure.
    pub async fn record(&self, server: &str, sample: MonitorSample) -> Result<(), StoreError> {
        let cutoff = self.clock.now() - RETENTION;
        let server = server.to_owned();
        self.store
            .update(MONITOR_HISTORY, Stored::new, move |mut all: Stored| {
                let mut list = samples(all.get(&server));
                list.push(sample);
                list.retain(|s| s.at >= cutoff);
                if list.len() > MAX_SAMPLES_PER_SERVER {
                    let excess = list.len() - MAX_SAMPLES_PER_SERVER;
                    list.drain(..excess);
                }
                all.insert(server, Some(list.into_iter().map(Some).collect()));
                Some(all)
            })
            .await
            .map(drop)
    }

    /// The figures over the last `window`.
    #[must_use]
    pub fn stats(&self, server: &str, window: Duration) -> HealthStats {
        let since = self.clock.now() - TimeDelta::from_std(window).unwrap_or(TimeDelta::MAX);
        let stored = self
            .store
            .read(MONITOR_HISTORY, Stored::new())
            .unwrap_or_default();
        let mut list: Vec<MonitorSample> = samples(stored.get(server))
            .into_iter()
            .filter(|s| s.at >= since)
            .collect();
        if list.is_empty() {
            return HealthStats::empty();
        }
        list.sort_by_key(|s| s.at);

        let up = list
            .iter()
            .filter(|s| matches!(s.state, HealthState::Online | HealthState::Degraded))
            .count();
        let latencies: Vec<f64> = list.iter().filter_map(|s| s.latency_ms).collect();
        #[allow(clippy::cast_precision_loss)]
        HealthStats {
            uptime_percent: 100.0 * up as f64 / list.len() as f64,
            rcon_failures: list.iter().filter(|s| !s.rcon_ok).count(),
            unexpected_restarts: list.iter().filter(|s| s.unexpected_restart).count(),
            average_latency_ms: (!latencies.is_empty())
                .then(|| latencies.iter().sum::<f64>() / latencies.len() as f64),
            longest_outage: longest_outage(&list),
            samples: list.len(),
        }
    }
}

fn longest_outage(samples: &[MonitorSample]) -> Duration {
    let mut longest = TimeDelta::zero();
    let mut start: Option<DateTime<Utc>> = None;
    let mut last = DateTime::<Utc>::UNIX_EPOCH;
    for s in samples {
        if s.state == HealthState::Offline {
            start.get_or_insert(s.at);
            last = s.at;
        } else if let Some(began) = start.take() {
            longest = longest.max(last - began);
        }
    }
    if let Some(began) = start {
        longest = longest.max(last - began);
    }
    longest.to_std().unwrap_or(Duration::ZERO)
}
