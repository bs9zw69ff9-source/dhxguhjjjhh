//! The background services: registered, started in dependency order, ticked on an interval,
//! supervised, and reported on (C# `Services/ServiceRegistry.cs`).
//!
//! - A tick that FAILS or PANICS is contained and counted. In the Node bot it was an unhandled
//!   rejection that could take the process down.
//! - A tick is NEVER RE-ENTERED: the next one waits for the last, so a slow RCON cannot turn N
//!   servers into unbounded concurrency. A tick slower than its interval is an overrun.
//! - A tick is BOUNDED by a budget. One that never returns used to stop its service for good
//!   while it still reported Running; now it is abandoned (dropped) and counted as a failure,
//!   so the supervisor can revive it.
//! - Stopping is deterministic: no tick runs after [`ServiceRegistry::stop_one`] returns, and a
//!   stop is never recorded as a failure.
//! - Revival is BRAKED: three restarts in thirty minutes and the supervisor gives up (said
//!   once) until the service recovers, which hands its budget back.
//!
//! Cancellation is by DROPPING the tick's future, which is how a timeout or a stop abandons it.

use std::collections::VecDeque;
use std::panic::AssertUnwindSafe;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use chrono::{DateTime, Utc};
use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use indexmap::IndexMap;
use tokio::sync::watch;
use tokio::task::JoinHandle;
use tracing::Instrument;

use crate::health::{HealthResult, HealthStatus};
use crate::metrics::{MetricLabels, MetricsRegistry};

/// How many restarts the supervisor makes inside [`RESTART_WINDOW`] before it gives up.
pub const MAX_RESTARTS_PER_WINDOW: usize = 3;
/// The window [`MAX_RESTARTS_PER_WINDOW`] counts over.
pub const RESTART_WINDOW: Duration = Duration::from_secs(30 * 60);
/// The failures kept per service for `/health`.
const MAX_RECENT_ERRORS: usize = 5;
const MIN_TICK_BUDGET: Duration = Duration::from_secs(30);
const MAX_TICK_BUDGET: Duration = Duration::from_secs(10 * 60);

/// Why a tick, a start or a health check failed. `new` records WHERE: a bare message under a
/// service name identified nothing ("somewhere in the log pipeline"), and one location turns
/// it into a place to look.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TickError {
    pub message: String,
    pub origin: Option<String>,
}

impl TickError {
    #[must_use]
    #[track_caller]
    pub fn new(message: impl Into<String>) -> Self {
        let at = std::panic::Location::caller();
        let file = std::path::Path::new(at.file()).file_name().map_or_else(
            || at.file().to_owned(),
            |f| f.to_string_lossy().into_owned(),
        );
        Self {
            message: message.into(),
            origin: Some(format!("{file}:{}", at.line())),
        }
    }

    fn bare(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            origin: None,
        }
    }
}

impl std::fmt::Display for TickError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match &self.origin {
            Some(origin) => write!(f, "{}  ({origin})", self.message),
            None => f.write_str(&self.message),
        }
    }
}

impl std::error::Error for TickError {}

/// What a tick, start or stop runs.
pub type Work = Arc<dyn Fn() -> BoxFuture<'static, Result<(), TickError>> + Send + Sync>;
/// A per-service health check: "the timer is ticking" is not "the work is succeeding".
pub type HealthCheck =
    Arc<dyn Fn() -> BoxFuture<'static, Result<HealthResult, TickError>> + Send + Sync>;

/// One service.
#[derive(Clone)]
pub struct ServiceDefinition {
    pub name: String,
    pub tick: Work,
    /// `None`: no loop, only `on_start`/`on_stop`.
    pub interval: Option<Duration>,
    /// Off by default, and load-bearing: services start before the gateway connects.
    pub run_on_start: bool,
    /// A critical service that cannot start aborts startup; any other leaves its feature dark.
    pub critical: bool,
    pub depends_on: Vec<String>,
    pub on_start: Option<Work>,
    pub on_stop: Option<Work>,
    pub health_check: Option<HealthCheck>,
    /// The tick budget; `None` derives one from the interval.
    pub timeout: Option<Duration>,
}

impl std::fmt::Debug for ServiceDefinition {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServiceDefinition")
            .field("name", &self.name)
            .field("interval", &self.interval)
            .field("depends_on", &self.depends_on)
            .finish_non_exhaustive()
    }
}

impl ServiceDefinition {
    /// A service with the defaults: no interval, nothing on start or stop.
    #[must_use]
    pub fn new(name: impl Into<String>, tick: Work) -> Self {
        Self {
            name: name.into(),
            tick,
            interval: None,
            run_on_start: false,
            critical: false,
            depends_on: Vec::new(),
            on_start: None,
            on_stop: None,
            health_check: None,
            timeout: None,
        }
    }
}

/// Where a service is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ServiceState {
    Stopped,
    Starting,
    Running,
    Failed,
}

/// One remembered failure.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServiceFailure {
    pub at: DateTime<Utc>,
    pub message: String,
}

/// A service as `/health` reports it.
#[derive(Debug, Clone, PartialEq)]
pub struct ServiceStatus {
    pub name: String,
    pub state: ServiceState,
    pub uptime: Duration,
    pub interval: Option<Duration>,
    pub ticks: u64,
    pub failures: u64,
    pub consecutive_failures: u32,
    pub overruns: u64,
    pub last_error: Option<String>,
    pub last_tick_at: Option<DateTime<Utc>>,
    pub last_duration_ms: Option<f64>,
    /// Newest first.
    pub errors: Vec<ServiceFailure>,
}

/// Why the registry refused something.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RegistryError(pub String);

impl std::fmt::Display for RegistryError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for RegistryError {}

#[derive(Debug, Default)]
struct Counters {
    state: Option<ServiceState>,
    started_at: Option<Instant>,
    ticks: u64,
    failures: u64,
    consecutive_failures: u32,
    overruns: u64,
    last_error: Option<String>,
    restarts_at: Vec<Instant>,
    restarts_exhausted: bool,
    recent: VecDeque<ServiceFailure>,
    last_tick_at: Option<DateTime<Utc>>,
    last_duration_ms: Option<f64>,
}

impl Counters {
    fn state(&self) -> ServiceState {
        self.state.unwrap_or(ServiceState::Stopped)
    }

    fn remember(&mut self, error: &TickError) {
        let message = error.to_string();
        self.last_error = Some(message.clone());
        self.recent.push_back(ServiceFailure {
            at: Utc::now(),
            message,
        });
        while self.recent.len() > MAX_RECENT_ERRORS {
            self.recent.pop_front();
        }
    }
}

struct Record {
    definition: ServiceDefinition,
    counters: Mutex<Counters>,
    running: tokio::sync::Mutex<Option<(watch::Sender<bool>, JoinHandle<()>)>>,
}

impl Record {
    fn counters(&self) -> MutexGuard<'_, Counters> {
        self.counters.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// The registry.
pub struct ServiceRegistry {
    services: Mutex<IndexMap<String, Arc<Record>>>,
    metrics: Arc<MetricsRegistry>,
}

impl std::fmt::Debug for ServiceRegistry {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServiceRegistry")
            .field("services", &self.names())
            .finish_non_exhaustive()
    }
}

/// The tick budget: explicit, or five intervals clamped to between 30 s and 10 min. The
/// intervals span three orders of magnitude (1.5 s to 5 min), so one constant suits neither.
#[must_use]
pub fn tick_budget(definition: &ServiceDefinition, interval: Duration) -> Duration {
    definition
        .timeout
        .unwrap_or_else(|| (interval * 5).clamp(MIN_TICK_BUDGET, MAX_TICK_BUDGET))
}

/// The definitions in start order: every dependency before what needs it.
///
/// # Errors
/// A cycle (naming its members) or a dependency on a service that does not exist: a typo'd
/// dependency that quietly does nothing is worse than a startup failure.
pub fn order_by_dependencies(
    definitions: &[ServiceDefinition],
) -> Result<Vec<ServiceDefinition>, RegistryError> {
    #[derive(PartialEq)]
    enum Mark {
        Visiting,
        Done,
    }
    fn visit<'a>(
        name: &'a str,
        required_by: Option<&str>,
        by_name: &IndexMap<&'a str, &'a ServiceDefinition>,
        marks: &mut IndexMap<&'a str, Mark>,
        path: &mut Vec<&'a str>,
        ordered: &mut Vec<ServiceDefinition>,
    ) -> Result<(), RegistryError> {
        match marks.get(name) {
            Some(Mark::Done) => return Ok(()),
            Some(Mark::Visiting) => {
                let mut cycle = path.clone();
                cycle.push(name);
                return Err(RegistryError(format!(
                    "circular service dependency: {}",
                    cycle.join(" -> ")
                )));
            }
            None => {}
        }
        let Some(definition) = by_name.get(name) else {
            return Err(RegistryError(format!(
                "service \"{}\" depends on unknown service \"{name}\"",
                required_by.unwrap_or("?")
            )));
        };
        marks.insert(name, Mark::Visiting);
        path.push(name);
        for dependency in &definition.depends_on {
            visit(dependency, Some(name), by_name, marks, path, ordered)?;
        }
        path.pop();
        marks.insert(name, Mark::Done);
        ordered.push((*definition).clone());
        Ok(())
    }

    let by_name: IndexMap<&str, &ServiceDefinition> =
        definitions.iter().map(|d| (d.name.as_str(), d)).collect();
    let mut marks = IndexMap::new();
    let mut path = Vec::new();
    let mut ordered = Vec::new();
    for definition in definitions {
        visit(
            &definition.name,
            None,
            &by_name,
            &mut marks,
            &mut path,
            &mut ordered,
        )?;
    }
    Ok(ordered)
}

impl ServiceRegistry {
    #[must_use]
    pub fn new(metrics: Arc<MetricsRegistry>) -> Self {
        Self {
            services: Mutex::new(IndexMap::new()),
            metrics,
        }
    }

    fn services(&self) -> MutexGuard<'_, IndexMap<String, Arc<Record>>> {
        self.services.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn record(&self, name: &str) -> Option<Arc<Record>> {
        self.services().get(name).cloned()
    }

    fn records(&self) -> Vec<Arc<Record>> {
        self.services().values().cloned().collect()
    }

    /// Add a service.
    ///
    /// # Errors
    /// A blank name, or a name already registered.
    pub fn register(&self, definition: ServiceDefinition) -> Result<(), RegistryError> {
        if definition.name.trim().is_empty() {
            return Err(RegistryError("a service needs a name".into()));
        }
        let mut services = self.services();
        if services.contains_key(&definition.name) {
            return Err(RegistryError(format!(
                "service \"{}\" is already registered",
                definition.name
            )));
        }
        services.insert(
            definition.name.clone(),
            Arc::new(Record {
                definition,
                counters: Mutex::new(Counters::default()),
                running: tokio::sync::Mutex::new(None),
            }),
        );
        Ok(())
    }

    #[must_use]
    pub fn count(&self) -> usize {
        self.services().len()
    }

    #[must_use]
    pub fn running(&self) -> usize {
        self.records()
            .iter()
            .filter(|r| r.counters().state() == ServiceState::Running)
            .count()
    }

    #[must_use]
    pub fn names(&self) -> Vec<String> {
        self.services().keys().cloned().collect()
    }

    fn definitions(&self) -> Vec<ServiceDefinition> {
        self.records()
            .iter()
            .map(|r| r.definition.clone())
            .collect()
    }

    /// Start every service in dependency order.
    ///
    /// # Errors
    /// A bad dependency graph, or a CRITICAL service that failed to start.
    pub async fn start_all(&self) -> Result<(), RegistryError> {
        let ordered = order_by_dependencies(&self.definitions())?;
        for definition in &ordered {
            self.start_one(&definition.name).await?;
        }
        tracing::info!(
            running = self.running(),
            total = ordered.len(),
            "background services running"
        );
        Ok(())
    }

    /// Start one service. A non-critical one that fails is left Failed and reported, not
    /// propagated: the bot comes up with one feature dark rather than not at all.
    ///
    /// # Errors
    /// An unknown name, or a critical service that failed to start.
    pub async fn start_one(&self, name: &str) -> Result<(), RegistryError> {
        let record = self
            .record(name)
            .ok_or_else(|| RegistryError(format!("unknown service \"{name}\"")))?;
        let mut running = record.running.lock().await;
        if record.counters().state() == ServiceState::Running {
            return Ok(());
        }
        record.counters().state = Some(ServiceState::Starting);

        let started = match &record.definition.on_start {
            Some(on_start) => contained(on_start()).await,
            None => Ok(()),
        }
        .and_then(|()| match record.definition.interval {
            // Refused HERE: a loop that dies at once would leave a "Running" service.
            Some(interval) if interval.is_zero() => Err(TickError::bare(format!(
                "service \"{name}\" has a non-positive interval"
            ))),
            _ => Ok(()),
        });

        let labels = MetricLabels::of("service", name);
        if let Err(error) = started {
            {
                let mut counters = record.counters();
                counters.state = Some(ServiceState::Failed);
                counters.remember(&error);
            }
            self.metrics.gauge("service_up", 0.0, &labels, None);
            if record.definition.critical {
                return Err(RegistryError(format!(
                    "critical service \"{name}\" failed to start: {error}"
                )));
            }
            tracing::warn!(service = name, %error, "service failed to start (non-critical, continuing)");
            return Ok(());
        }

        if let Some(interval) = record.definition.interval {
            let (stop, stopped) = watch::channel(false);
            let handle = tokio::spawn(run_loop(
                Arc::clone(&record),
                Arc::clone(&self.metrics),
                interval,
                stopped,
            ));
            *running = Some((stop, handle));
        }
        {
            let mut counters = record.counters();
            counters.state = Some(ServiceState::Running);
            counters.started_at = Some(Instant::now());
            counters.consecutive_failures = 0;
        }
        self.metrics.gauge(
            "service_up",
            1.0,
            &labels,
            Some("1 when a service is running"),
        );
        tracing::debug!(service = name, interval = ?record.definition.interval, "service started");
        Ok(())
    }

    /// Stop one service. No tick runs after this returns.
    pub async fn stop_one(&self, name: &str) {
        let Some(record) = self.record(name) else {
            return;
        };
        let mut running = record.running.lock().await;
        if record.counters().state() == ServiceState::Stopped {
            return;
        }
        if let Some((stop, handle)) = running.take() {
            stop.send_replace(true);
            // A loop that panicked outside a tick is already over; nothing to wait for.
            let _ = handle.await;
        }
        if let Some(on_stop) = &record.definition.on_stop {
            if let Err(error) = contained(on_stop()).await {
                tracing::warn!(service = name, %error, "service stop handler failed");
            }
        }
        {
            let mut counters = record.counters();
            counters.state = Some(ServiceState::Stopped);
            counters.started_at = None;
        }
        self.metrics
            .gauge("service_up", 0.0, &MetricLabels::of("service", name), None);
        tracing::debug!(service = name, "service stopped");
    }

    /// Stop every service, in reverse dependency order.
    pub async fn stop_all(&self) {
        let ordered =
            order_by_dependencies(&self.definitions()).unwrap_or_else(|_| self.definitions());
        for definition in ordered.iter().rev() {
            self.stop_one(&definition.name).await;
        }
    }

    /// Stop then start.
    ///
    /// # Errors
    /// As for [`start_one`](Self::start_one).
    pub async fn restart(&self, name: &str) -> Result<(), RegistryError> {
        self.stop_one(name).await;
        self.metrics.increment(
            "service_restarts_total",
            &MetricLabels::of("service", name),
            1.0,
            Some("Service restarts"),
        );
        self.start_one(name).await
    }

    /// Restart every service that failed to start or has failed `threshold` ticks in a row,
    /// within the brake. The names restarted.
    pub async fn revive_failed(&self, threshold: u32) -> Vec<String> {
        let mut revived = Vec::new();
        let now = Instant::now();
        for record in self.records() {
            let name = record.definition.name.clone();
            let (exhausted, announce, recent, last_error, consecutive) = {
                let mut c = record.counters();
                let stuck = c.state() == ServiceState::Failed
                    || (c.state() == ServiceState::Running && c.consecutive_failures >= threshold);
                if !stuck {
                    continue;
                }
                c.restarts_at
                    .retain(|at| now.duration_since(*at) <= RESTART_WINDOW);
                let recent = c.restarts_at.len();
                let exhausted = recent >= MAX_RESTARTS_PER_WINDOW;
                // SAID ONCE: repeating it every sweep buries the original failure.
                let announce = exhausted && !c.restarts_exhausted;
                if exhausted {
                    c.restarts_exhausted = true;
                } else {
                    c.restarts_at.push(now);
                }
                (
                    exhausted,
                    announce,
                    recent,
                    c.last_error.clone(),
                    c.consecutive_failures,
                )
            };
            if exhausted {
                if announce {
                    tracing::error!(
                        service = %name,
                        restarts = recent,
                        minutes = RESTART_WINDOW.as_secs() / 60,
                        last_error = last_error.as_deref().unwrap_or("(none recorded)"),
                        "service keeps failing after restarts - giving up until it recovers or the bot restarts"
                    );
                }
                continue;
            }
            tracing::warn!(service = %name, failures = consecutive, "restarting a failing service");
            if let Err(error) = self.restart(&name).await {
                tracing::error!(service = %name, %error, "restart failed");
            }
            revived.push(name);
        }
        revived
    }

    /// Every service's state and counters.
    #[must_use]
    pub fn status(&self) -> Vec<ServiceStatus> {
        self.records()
            .iter()
            .map(|r| {
                let c = r.counters();
                ServiceStatus {
                    name: r.definition.name.clone(),
                    state: c.state(),
                    uptime: c.started_at.map_or(Duration::ZERO, |at| at.elapsed()),
                    interval: r.definition.interval,
                    ticks: c.ticks,
                    failures: c.failures,
                    consecutive_failures: c.consecutive_failures,
                    overruns: c.overruns,
                    last_error: c.last_error.clone(),
                    last_tick_at: c.last_tick_at,
                    last_duration_ms: c.last_duration_ms,
                    errors: c.recent.iter().rev().cloned().collect(),
                }
            })
            .collect()
    }

    /// The worst of every service. Stopped is DEGRADED, not unhealthy: an intentionally
    /// stopped service is not a fault.
    pub async fn health(&self) -> HealthResult {
        let mut worst = HealthStatus::Healthy;
        let mut notes = Vec::new();
        for record in self.records() {
            let (state, consecutive, last_error) = {
                let c = record.counters();
                (c.state(), c.consecutive_failures, c.last_error.clone())
            };
            let mut status = match state {
                ServiceState::Running => HealthStatus::Healthy,
                ServiceState::Stopped => HealthStatus::Degraded,
                ServiceState::Starting | ServiceState::Failed => HealthStatus::Unhealthy,
            };
            let mut detail = last_error;
            if state == ServiceState::Running && consecutive > 0 {
                status = HealthStatus::Degraded;
                detail = Some(format!("{consecutive} consecutive tick failures"));
            }
            if state == ServiceState::Running {
                if let Some(check) = &record.definition.health_check {
                    match AssertUnwindSafe(check()).catch_unwind().await {
                        Ok(Ok(result)) if result.status != HealthStatus::Healthy => {
                            status = result.status;
                            detail = result.detail.or(detail);
                        }
                        Ok(Ok(_)) => {}
                        Ok(Err(error)) => {
                            status = HealthStatus::Unhealthy;
                            detail = Some(error.message);
                        }
                        Err(panic) => {
                            status = HealthStatus::Unhealthy;
                            detail = Some(panic_message(&*panic));
                        }
                    }
                }
            }
            if status != HealthStatus::Healthy {
                notes.push(format!(
                    "{}: {}",
                    record.definition.name,
                    detail.unwrap_or_else(|| format!("{status:?}"))
                ));
            }
            worst = worst.max(status);
        }
        let detail = if notes.is_empty() {
            format!("{}/{} running", self.running(), self.count())
        } else {
            notes.join("; ")
        };
        HealthResult {
            status: worst,
            detail: Some(detail),
        }
    }
}

/// Run `work`, turning a panic into an error rather than losing the task.
async fn contained(work: BoxFuture<'static, Result<(), TickError>>) -> Result<(), TickError> {
    match AssertUnwindSafe(work).catch_unwind().await {
        Ok(result) => result,
        Err(panic) => Err(TickError::bare(format!(
            "panicked: {}",
            panic_message(&*panic)
        ))),
    }
}

fn panic_message(panic: &(dyn std::any::Any + Send)) -> String {
    panic
        .downcast_ref::<&str>()
        .map(|s| (*s).to_owned())
        .or_else(|| panic.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "a panic with no message".into())
}

async fn run_loop(
    record: Arc<Record>,
    metrics: Arc<MetricsRegistry>,
    interval: Duration,
    mut stopped: watch::Receiver<bool>,
) {
    let span = tracing::info_span!("service", name = %record.definition.name);
    async move {
        if record.definition.run_on_start
            && !run_tick(&record, &metrics, interval, &mut stopped).await
        {
            return;
        }
        let mut timer = tokio::time::interval_at(tokio::time::Instant::now() + interval, interval);
        timer.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            let due = tokio::select! {
                _ = timer.tick() => true,
                _ = stopped.wait_for(|s| *s) => false,
            };
            if !due || !run_tick(&record, &metrics, interval, &mut stopped).await {
                return;
            }
        }
    }
    .instrument(span)
    .await;
}

/// One bounded tick. False when the service is stopping (the tick was abandoned, and that is
/// NOT a failure).
async fn run_tick(
    record: &Record,
    metrics: &MetricsRegistry,
    interval: Duration,
    stopped: &mut watch::Receiver<bool>,
) -> bool {
    let name = record.definition.name.as_str();
    let budget = tick_budget(&record.definition, interval);
    let started = Instant::now();

    let outcome = tokio::select! {
        result = tokio::time::timeout(budget, contained((record.definition.tick)())) => Some(result),
        _ = stopped.wait_for(|s| *s) => None,
    };
    let Some(outcome) = outcome else {
        return false;
    };
    let result = outcome.unwrap_or_else(|_| {
        Err(TickError::bare(format!(
            "tick exceeded its {:.1}s budget and was abandoned",
            budget.as_secs_f64()
        )))
    });

    let elapsed = started.elapsed();
    let labels = MetricLabels::of("service", name);
    {
        let mut c = record.counters();
        match &result {
            Ok(()) => {
                // A SUCCESSFUL TICK CLEARS THE BRAKE, or three restarts in any half hour would
                // end supervision of the service for the life of the process.
                c.consecutive_failures = 0;
                c.restarts_at.clear();
                c.restarts_exhausted = false;
            }
            Err(error) => {
                c.failures += 1;
                c.consecutive_failures = c.consecutive_failures.saturating_add(1);
                c.remember(error);
            }
        }
        c.ticks += 1;
        c.last_tick_at = Some(Utc::now());
        c.last_duration_ms = Some(elapsed.as_secs_f64() * 1000.0);
        if elapsed > interval {
            c.overruns += 1;
        }
    }
    let outcome_label = if result.is_ok() { "success" } else { "failure" };
    metrics.increment(
        "service_ticks_total",
        &labels.with("outcome", outcome_label),
        1.0,
        Some("Service ticks by outcome"),
    );
    if let Err(error) = &result {
        tracing::warn!(service = name, %error, "service tick failed");
    }
    metrics.observe(
        "service_tick_duration_ms",
        elapsed.as_secs_f64() * 1000.0,
        &labels,
        Some("Service tick duration in milliseconds"),
    );
    if elapsed > interval {
        metrics.increment(
            "service_tick_overruns_total",
            &labels,
            1.0,
            Some("Ticks that took longer than the service's interval"),
        );
    }
    true
}
