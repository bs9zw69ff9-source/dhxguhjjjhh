//! All-time and daily player peaks (C# `Core/Stats/PeakTracker.cs`).

use chrono::{DateTime, Utc};
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};

use crate::json::{epoch_ms, epoch_ms_opt, null_as_default};

/// One peak and when it happened.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PeakEntry {
    #[serde(default)]
    pub peak: i64,
    #[serde(with = "epoch_ms", default = "epoch")]
    pub peak_at: DateTime<Utc>,
}

fn epoch() -> DateTime<Utc> {
    DateTime::UNIX_EPOCH
}

impl PeakEntry {
    #[must_use]
    pub fn new(peak: i64, peak_at: DateTime<Utc>) -> Self {
        Self { peak, peak_at }
    }
}

/// The peaks for one Eastern calendar day.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DailyPeaks {
    #[serde(default)]
    pub date: String,
    #[serde(default, deserialize_with = "null_as_default")]
    pub per_server: IndexMap<String, PeakEntry>,
    pub combined: PeakEntry,
}

/// Everything tracked: per server, combined across servers, and today.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PeakStats {
    #[serde(deserialize_with = "null_as_default")]
    pub per_server: IndexMap<String, PeakEntry>,
    pub combined: Option<PeakEntry>,
    pub daily: Option<DailyPeaks>,
    #[serde(with = "epoch_ms_opt")]
    pub updated_at: Option<DateTime<Utc>>,
}

/// The new stats, and whether anything changed (so the caller can skip the write).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PeakUpdate {
    pub changed: bool,
    pub stats: PeakStats,
}

/// Namespace for the reducer, mirroring the C# static class.
#[derive(Debug)]
pub struct PeakTracker;

impl PeakTracker {
    /// Fold one sample of per-server player counts into the stats.
    ///
    /// The combined peak is the SUM across servers at one moment, not the max of their peaks.
    /// Passing no `date` skips daily tracking entirely. A day roll-over is reported as a
    /// change even at zero players, or a quiet midnight leaves yesterday's numbers on display
    /// as though they were today's.
    #[must_use]
    pub fn reduce(
        counts: &IndexMap<String, i64>,
        current: Option<&PeakStats>,
        now: DateTime<Utc>,
        date: Option<&str>,
    ) -> PeakUpdate {
        let empty = PeakStats::default();
        let current = current.unwrap_or(&empty);
        let combined: i64 = counts.values().sum();
        let mut per_server = current.per_server.clone();
        let mut changed = false;

        // ---- all-time ----
        for (server, &count) in counts {
            if count <= per_server.get(server).map_or(0, |e| e.peak) {
                continue;
            }
            per_server.insert(server.clone(), PeakEntry::new(count, now));
            changed = true;
        }

        let mut all_time_combined = current.combined;
        if combined > all_time_combined.map_or(0, |c| c.peak) {
            all_time_combined = Some(PeakEntry::new(combined, now));
            changed = true;
        }

        // ---- daily ----
        let mut daily = current.daily.clone();
        if let Some(date) = date {
            let rolled_over = daily.as_ref().is_none_or(|d| d.date != date);
            let (mut daily_per_server, mut daily_combined) = match (&daily, rolled_over) {
                (Some(d), false) => (d.per_server.clone(), d.combined),
                _ => (IndexMap::new(), PeakEntry::new(0, now)),
            };
            if rolled_over {
                changed = true;
            }
            for (server, &count) in counts {
                if count <= daily_per_server.get(server).map_or(0, |e| e.peak) {
                    continue;
                }
                daily_per_server.insert(server.clone(), PeakEntry::new(count, now));
                changed = true;
            }
            if combined > daily_combined.peak {
                daily_combined = PeakEntry::new(combined, now);
                changed = true;
            }
            daily = Some(DailyPeaks {
                date: date.to_owned(),
                per_server: daily_per_server,
                combined: daily_combined,
            });
        }

        let stats = PeakStats {
            per_server,
            combined: all_time_combined,
            daily,
            updated_at: if changed {
                Some(now)
            } else {
                current.updated_at
            },
        };
        PeakUpdate { changed, stats }
    }

    /// Today's combined peak, or 0 when the stats hold another day (or nothing).
    #[must_use]
    pub fn daily_peak(stats: Option<&PeakStats>, date: &str) -> i64 {
        stats
            .and_then(|s| s.daily.as_ref())
            .filter(|d| d.date == date)
            .map_or(0, |d| d.combined.peak)
    }
}
