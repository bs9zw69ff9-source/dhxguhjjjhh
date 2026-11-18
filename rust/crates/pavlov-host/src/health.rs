//! Component health, aggregated for `/health` and the watchdog (C#
//! `Observability/HealthRegistry.cs`).
//!
//! Every check is bounded by a timeout, cached briefly so a burst of probes costs one run, and
//! marked critical or not; the overall status is the worst CRITICAL one.

use std::collections::BTreeMap;
use std::fmt;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use chrono::{DateTime, Utc};
use std::panic::AssertUnwindSafe;

use futures_util::FutureExt;
use futures_util::future::{BoxFuture, join_all};

/// Ordered: a worse status compares greater.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum HealthStatus {
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2,
}

/// One check's answer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HealthResult {
    pub status: HealthStatus,
    pub detail: Option<String>,
}

impl HealthResult {
    #[must_use]
    pub fn healthy(detail: Option<String>) -> Self {
        Self {
            status: HealthStatus::Healthy,
            detail,
        }
    }

    #[must_use]
    pub fn degraded(detail: impl Into<String>) -> Self {
        Self {
            status: HealthStatus::Degraded,
            detail: Some(detail.into()),
        }
    }

    #[must_use]
    pub fn unhealthy(detail: impl Into<String>) -> Self {
        Self {
            status: HealthStatus::Unhealthy,
            detail: Some(detail.into()),
        }
    }
}

/// One component in a report.
#[derive(Debug, Clone, PartialEq)]
pub struct ComponentReport {
    pub name: String,
    pub status: HealthStatus,
    pub detail: Option<String>,
    pub duration_ms: f64,
    pub checked_at: DateTime<Utc>,
    pub critical: bool,
}

/// Every component, and the worst critical status.
#[derive(Debug, Clone, PartialEq)]
pub struct HealthReport {
    pub status: HealthStatus,
    pub checked_at: DateTime<Utc>,
    pub components: BTreeMap<String, ComponentReport>,
}

/// A health check.
pub type Check = Arc<dyn Fn() -> BoxFuture<'static, HealthResult> + Send + Sync>;

struct Entry {
    check: Check,
    critical: bool,
    cached: Option<(ComponentReport, Instant)>,
}

/// The registered checks.
pub struct HealthRegistry {
    checks: Mutex<BTreeMap<String, Entry>>,
    timeout: Duration,
    cache_duration: Duration,
}

impl fmt::Debug for HealthRegistry {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("HealthRegistry")
            .field("names", &self.names())
            .finish_non_exhaustive()
    }
}

impl Default for HealthRegistry {
    fn default() -> Self {
        Self::new(Duration::from_secs(3), Duration::from_secs(1))
    }
}

impl HealthRegistry {
    #[must_use]
    pub fn new(timeout: Duration, cache_duration: Duration) -> Self {
        Self {
            checks: Mutex::new(BTreeMap::new()),
            timeout,
            cache_duration,
        }
    }

    fn lock(&self) -> MutexGuard<'_, BTreeMap<String, Entry>> {
        self.checks.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Register (or replace) a check.
    pub fn register(&self, name: &str, critical: bool, check: Check) {
        self.lock().insert(
            name.to_owned(),
            Entry {
                check,
                critical,
                cached: None,
            },
        );
    }

    /// Remove a check; whether it was there.
    pub fn unregister(&self, name: &str) -> bool {
        self.lock().remove(name).is_some()
    }

    #[must_use]
    pub fn names(&self) -> Vec<String> {
        self.lock().keys().cloned().collect()
    }

    async fn run(&self, name: String) -> Option<ComponentReport> {
        let (check, critical) = {
            let checks = self.lock();
            let entry = checks.get(&name)?;
            if let Some((cached, at)) = &entry.cached {
                if at.elapsed() < self.cache_duration {
                    return Some(cached.clone());
                }
            }
            (Arc::clone(&entry.check), entry.critical)
        };
        let started = Instant::now();
        // A check that panics is UNHEALTHY, not a crash: the framework must not be able to take
        // down the thing it is monitoring.
        let guarded = AssertUnwindSafe(check()).catch_unwind();
        let result = match tokio::time::timeout(self.timeout, guarded).await {
            Ok(Ok(result)) => result,
            Ok(Err(panic)) => HealthResult::unhealthy(
                panic
                    .downcast_ref::<&str>()
                    .map(|s| (*s).to_owned())
                    .or_else(|| panic.downcast_ref::<String>().cloned())
                    .unwrap_or_else(|| "the health check panicked".into()),
            ),
            Err(_) => HealthResult::unhealthy(format!(
                "health check timed out after {}ms",
                self.timeout.as_millis()
            )),
        };
        let report = ComponentReport {
            name: name.clone(),
            status: result.status,
            detail: result.detail,
            duration_ms: started.elapsed().as_secs_f64() * 1000.0,
            checked_at: Utc::now(),
            critical,
        };
        if let Some(entry) = self.lock().get_mut(&name) {
            entry.cached = Some((report.clone(), Instant::now()));
        }
        Some(report)
    }

    /// Run every check (concurrently) and aggregate.
    pub async fn report(&self) -> HealthReport {
        let reports = join_all(self.names().into_iter().map(|n| self.run(n))).await;
        let components: BTreeMap<String, ComponentReport> = reports
            .into_iter()
            .flatten()
            .map(|r| (r.name.clone(), r))
            .collect();
        let status = components
            .values()
            .filter(|r| r.critical)
            .map(|r| r.status)
            .max()
            .unwrap_or(HealthStatus::Healthy);
        HealthReport {
            status,
            checked_at: Utc::now(),
            components,
        }
    }

    /// One check by name.
    pub async fn check(&self, name: &str) -> Option<ComponentReport> {
        self.run(name.to_owned()).await
    }
}
