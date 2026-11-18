//! Tailing Pavlov's Stats.log and raising the kills it records (C# `Logs/StatsLogService.cs`).
//!
//! It shares [`LogTailer`] with the Pavlov.log tail: Stats.log rotates roughly hourly, so the
//! rotation handling matters more here, not less. ONE READER PER FILE: the parser holds a partly
//! read block, and two servers sharing one would interleave their blocks into nonsense.

use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex, PoisonError};

use pavlov_core::logs::{StatsKill, StatsLogReader, StatsRecord, StatsRoundState};

use super::LogTailer;
use super::tailer::{existing_distinct, split_list};
use crate::handlers::Handlers;
use crate::metrics::{MetricLabels, MetricsRegistry};

/// Turns what Stats.log wrote in a Killer or Killed field into a display name. `Ok(None)` is
/// "nobody knows this one"; `Err` is a lookup that failed. Either leaves the raw field.
pub type NameResolver = Arc<dyn Fn(&str) -> Result<Option<String>, String> + Send + Sync>;

/// The service.
pub struct StatsLogService {
    paths: Vec<String>,
    tailer: Arc<LogTailer>,
    metrics: Arc<MetricsRegistry>,
    resolve_name: Option<NameResolver>,
    readers: Mutex<HashMap<String, StatsLogReader>>,
    /// A kill, with the timestamp the game wrote rather than the clock now.
    pub killed: Handlers<StatsKill>,
    /// A round starting or ending.
    pub round_changed: Handlers<StatsRoundState>,
}

impl std::fmt::Debug for StatsLogService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StatsLogService")
            .field("paths", &self.paths)
            .finish_non_exhaustive()
    }
}

impl StatsLogService {
    #[must_use]
    pub fn new(
        paths: Vec<String>,
        tailer: Arc<LogTailer>,
        metrics: Arc<MetricsRegistry>,
        resolve_name: Option<NameResolver>,
    ) -> Self {
        Self {
            paths,
            tailer,
            metrics,
            resolve_name,
            readers: Mutex::new(HashMap::new()),
            killed: Handlers::default(),
            round_changed: Handlers::default(),
        }
    }

    /// The stats logs found, resolved once at startup.
    #[must_use]
    pub fn paths(&self) -> &[String] {
        &self.paths
    }

    /// Whether there is anything to read. False leaves the Pavlov.log scrape in charge.
    #[must_use]
    pub fn enabled(&self) -> bool {
        !self.paths.is_empty()
    }

    /// A name for a Killer or Killed field.
    ///
    /// Stats.log writes the id RCON targets (a plain number on a Shack server, not an EOS id),
    /// so the feed was a wall of numbers shooting numbers. UNRESOLVED IS LEFT ALONE, not
    /// replaced with "unknown": the field sometimes already holds a name, and an id nobody can
    /// name is still the only handle anybody has on that player. A failing lookup must not cost
    /// the kill either.
    fn named(&self, raw: &str) -> String {
        let Some(resolve) = &self.resolve_name else {
            return raw.to_owned();
        };
        match resolve(raw) {
            Ok(Some(name)) if !name.trim().is_empty() => name,
            Ok(_) => raw.to_owned(),
            Err(error) => {
                tracing::debug!(id = raw, %error, "could not resolve a name");
                raw.to_owned()
            }
        }
    }

    /// Read whatever has been appended to each stats log since the last tick.
    pub async fn tick(&self) {
        for path in &self.paths {
            let lines = self.tailer.poll(path, false);
            if lines.is_empty() {
                continue;
            }
            // Parsed under the lock, raised after it: no lock is held across a subscriber.
            let records: Vec<StatsRecord> = {
                let mut readers = self.readers.lock().unwrap_or_else(PoisonError::into_inner);
                let reader = readers.entry(path.clone()).or_default();
                lines.iter().filter_map(|l| reader.read(&l.text)).collect()
            };
            for record in records {
                self.raise(record).await;
            }
        }
    }

    async fn raise(&self, record: StatsRecord) {
        match record {
            StatsRecord::Kill(kill) => {
                self.metrics.increment(
                    "kills_total",
                    &MetricLabels::of("headshot", if kill.headshot { "yes" } else { "no" }),
                    1.0,
                    Some("Kills read from Stats.log"),
                );
                // Resolved here rather than at each consumer, so the next one added cannot
                // quietly print ids again.
                let kill = StatsKill {
                    killer: self.named(&kill.killer),
                    killed: self.named(&kill.killed),
                    ..kill
                };
                self.killed.emit(kill).await;
            }
            StatsRecord::Round(round) => self.round_changed.emit(round).await,
        }
    }
}

/// Stats.log beside each Pavlov.log, or wherever `STATS_LOGS` says.
///
/// DERIVED FROM THE LOG PATHS: a server's `Pavlov/Saved/Stats/Stats.log` sits under the same
/// install root as its `Pavlov/Saved/Logs/Pavlov.log`, which keeps the numbering the same, so
/// "Server 2" means the same thing in both feeds. Only files that exist are returned.
#[must_use]
pub fn discover(configured: Option<&str>, pavlov_logs: &[String]) -> Vec<String> {
    if let Some(configured) = configured.filter(|c| !c.trim().is_empty()) {
        let listed: Vec<String> = split_list(configured)
            .into_iter()
            .filter(|p| Path::new(p).is_file())
            .collect();
        if listed.is_empty() {
            tracing::warn!(
                configured,
                "STATS_LOGS is set but none of those files exist - kills will be read from Pavlov.log \
                 instead, without headshots and without the game's own timestamps"
            );
        }
        return listed;
    }

    let derived = existing_distinct(
        pavlov_logs
            .iter()
            .filter_map(|log| root_of(log))
            .map(|root| {
                root.join("Pavlov")
                    .join("Saved")
                    .join("Stats")
                    .join("Stats.log")
                    .to_string_lossy()
                    .into_owned()
            })
            .collect(),
    );
    if derived.is_empty() {
        tracing::info!(
            "no Stats.log found - kills come from Pavlov.log instead, which needs bVerboseLogging=true \
             and carries no headshot flag. Set STATS_LOGS to use it"
        );
    } else {
        tracing::info!(count = derived.len(), paths = %derived.join(", "), "found stats logs");
    }
    derived
}

/// The install root above `.../<root>/Pavlov/Saved/Logs/Pavlov.log`: four levels up.
fn root_of(pavlov_log: &str) -> Option<&Path> {
    Path::new(pavlov_log).ancestors().nth(4)
}
