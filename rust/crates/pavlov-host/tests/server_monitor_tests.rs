//! Port of `ServerMonitorTests.cs`: the monitor over a scripted probe and a manual clock, the
//! board it feeds, and the per-server gate.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{TimeDelta, TimeZone, Utc};
use common::{FakeRunner, file_store};
use futures_util::future::BoxFuture;
use pavlov_core::monitoring::{
    HealthProbe, HealthState, MonitorSettings, MonitorSignal, ServerHealth, SignalKind,
};
use pavlov_core::time::{Clock, ManualClock};
use pavlov_host::monitoring::{
    MonitorAlertSink, MonitorBoard, MonitorHistory, MonitorParts, MonitorTargets, ServerMonitor,
    ServerProbe,
};
use pavlov_host::service_control::ServiceControl;

fn settings() -> MonitorSettings {
    MonitorSettings {
        retry_base_delay: Duration::from_secs(60),
        retry_max_delay: Duration::from_secs(60),
        ..MonitorSettings::default()
    }
}

#[derive(Default)]
struct CapturingSink {
    signals: Mutex<Vec<(String, SignalKind)>>,
}

impl CapturingSink {
    fn signals(&self) -> Vec<(String, SignalKind)> {
        self.signals.lock().unwrap().clone()
    }
}

impl MonitorAlertSink for CapturingSink {
    fn post<'a>(
        &'a self,
        server: &'a str,
        signal: &'a MonitorSignal,
        _health: &'a ServerHealth,
    ) -> BoxFuture<'a, ()> {
        self.signals
            .lock()
            .unwrap()
            .push((server.to_owned(), signal.kind));
        Box::pin(async {})
    }

    fn test(&self) -> BoxFuture<'_, String> {
        Box::pin(async { "test".to_owned() })
    }
}

struct FakeTargets {
    servers: Vec<String>,
    resets: Mutex<HashMap<String, usize>>,
}

impl FakeTargets {
    fn new(servers: &[&str]) -> Self {
        Self {
            servers: servers.iter().map(|s| (*s).to_owned()).collect(),
            resets: Mutex::new(HashMap::new()),
        }
    }

    fn reset_count(&self, server: &str) -> usize {
        self.resets
            .lock()
            .unwrap()
            .get(server)
            .copied()
            .unwrap_or(0)
    }
}

impl MonitorTargets for FakeTargets {
    fn servers(&self) -> Vec<String> {
        self.servers.clone()
    }

    fn reset_session<'a>(&'a self, server: &'a str) -> BoxFuture<'a, ()> {
        *self
            .resets
            .lock()
            .unwrap()
            .entry(server.to_owned())
            .or_default() += 1;
        Box::pin(async {})
    }
}

type Script = Box<dyn Fn(&str) -> HealthProbe + Send + Sync>;

struct ScriptedProbe {
    next: Script,
    counts: Mutex<HashMap<String, usize>>,
}

impl ScriptedProbe {
    fn new(next: impl Fn(&str) -> HealthProbe + Send + Sync + 'static) -> Arc<Self> {
        Arc::new(Self {
            next: Box::new(next),
            counts: Mutex::new(HashMap::new()),
        })
    }

    fn count(&self, server: &str) -> usize {
        self.counts
            .lock()
            .unwrap()
            .get(server)
            .copied()
            .unwrap_or(0)
    }
}

impl ServerProbe for ScriptedProbe {
    fn probe<'a>(&'a self, server: &'a str) -> BoxFuture<'a, HealthProbe> {
        *self
            .counts
            .lock()
            .unwrap()
            .entry(server.to_owned())
            .or_default() += 1;
        let result = (self.next)(server);
        Box::pin(async move { result })
    }
}

struct Fixture {
    _dir: tempfile::TempDir,
    clock: Arc<ManualClock>,
    sink: Arc<CapturingSink>,
    targets: Arc<FakeTargets>,
    history: Arc<MonitorHistory>,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let clock = Arc::new(ManualClock::new(
            Utc.with_ymd_and_hms(2026, 1, 1, 12, 0, 0).unwrap(),
        ));
        let history = Arc::new(MonitorHistory::new(
            file_store(dir.path()),
            Arc::clone(&clock) as _,
        ));
        Self {
            _dir: dir,
            clock,
            sink: Arc::default(),
            targets: Arc::new(FakeTargets::new(&["Server 1", "Server 2"])),
            history,
        }
    }

    fn monitor_with(
        &self,
        probe: Arc<dyn ServerProbe>,
        targets: Arc<dyn MonitorTargets>,
        individual: bool,
    ) -> Arc<ServerMonitor> {
        Arc::new(ServerMonitor::new(MonitorParts {
            probe,
            alerts: Arc::clone(&self.sink) as _,
            history: Arc::clone(&self.history),
            settings: settings(),
            targets,
            clock: Arc::clone(&self.clock) as _,
            post_individual_alerts: individual,
        }))
    }

    fn monitor(&self, probe: Arc<dyn ServerProbe>, individual: bool) -> Arc<ServerMonitor> {
        self.monitor_with(probe, Arc::clone(&self.targets) as _, individual)
    }

    fn advance(&self, by: Duration) {
        self.clock.advance(TimeDelta::from_std(by).unwrap());
    }

    /// Tick `n` times, letting the back-off elapse after each.
    async fn ticks(&self, monitor: &ServerMonitor, n: u32) {
        for _ in 0..n {
            monitor.tick().await;
            self.advance(settings().retry_max_delay);
        }
    }
}

fn ok(clock: &ManualClock) -> HealthProbe {
    HealthProbe {
        rcon_ok: true,
        latency: Some(Duration::from_millis(50)),
        players: Some(20),
        max_players: Some(50),
        map: Some("Datacenter".into()),
        process_running: None,
        intentionally_stopped: false,
        last_log_activity: Some(clock.now()),
        failure_reason: None,
        at: clock.now(),
    }
}

fn fail(clock: &ManualClock) -> HealthProbe {
    HealthProbe {
        rcon_ok: false,
        latency: None,
        players: None,
        max_players: None,
        map: None,
        process_running: None,
        intentionally_stopped: false,
        last_log_activity: None,
        failure_reason: Some("timeout".into()),
        at: clock.now(),
    }
}

/// Server 1 fails while `down` is set; everything else is fine.
fn server1_down(clock: &Arc<ManualClock>, down: Arc<AtomicBool>) -> Arc<ScriptedProbe> {
    let clock = Arc::clone(clock);
    ScriptedProbe::new(move |server| {
        if server == "Server 1" && down.load(Ordering::SeqCst) {
            fail(&clock)
        } else {
            ok(&clock)
        }
    })
}

#[tokio::test]
async fn servers_healthy_across_a_bot_restart_do_not_alert() {
    let f = Fixture::new();
    let clock = Arc::clone(&f.clock);
    let monitor = f.monitor(ScriptedProbe::new(move |_| ok(&clock)), true);

    assert_eq!(monitor.snapshot("Server 1").state, HealthState::Unknown);

    monitor.tick().await;

    assert_eq!(monitor.snapshot("Server 1").state, HealthState::Online);
    assert_eq!(monitor.snapshot("Server 2").state, HealthState::Online);
    assert!(f.sink.signals().is_empty());
}

#[tokio::test]
async fn servers_are_monitored_independently() {
    let f = Fixture::new();
    let monitor = f.monitor(
        server1_down(&f.clock, Arc::new(AtomicBool::new(true))),
        true,
    );

    f.ticks(&monitor, settings().failure_threshold).await;

    assert_eq!(monitor.snapshot("Server 1").state, HealthState::Offline);
    assert_eq!(monitor.snapshot("Server 2").state, HealthState::Online);
    assert_eq!(
        f.sink.signals(),
        [("Server 1".to_owned(), SignalKind::ServerOffline)]
    );
}

#[tokio::test]
async fn a_confirmed_outage_recovers_and_resets_the_session() {
    let f = Fixture::new();
    let down = Arc::new(AtomicBool::new(true));
    let monitor = f.monitor(server1_down(&f.clock, Arc::clone(&down)), true);

    f.ticks(&monitor, settings().failure_threshold).await;
    assert_eq!(monitor.snapshot("Server 1").state, HealthState::Offline);

    down.store(false, Ordering::SeqCst);
    f.ticks(&monitor, settings().recovery_threshold).await;

    assert_eq!(monitor.snapshot("Server 1").state, HealthState::Online);
    let signals = f.sink.signals();
    assert!(signals.contains(&("Server 1".into(), SignalKind::ServerOffline)));
    assert!(signals.contains(&("Server 1".into(), SignalKind::ServerOnline)));
    // A wedged session is reset before reconnecting; a healthy server never is.
    assert!(f.targets.reset_count("Server 1") > 0);
    assert_eq!(f.targets.reset_count("Server 2"), 0);
}

#[tokio::test]
async fn an_offline_server_is_not_probed_until_the_backoff_elapses() {
    let f = Fixture::new();
    let clock = Arc::clone(&f.clock);
    let probe = ScriptedProbe::new(move |_| fail(&clock));
    let monitor = f.monitor(Arc::clone(&probe) as _, true);

    // Below the threshold it is RECONNECTING and ungated; the last failure arms the back-off.
    for i in 0..settings().failure_threshold {
        monitor.tick().await;
        if i < settings().failure_threshold - 1 {
            f.advance(Duration::from_secs(1));
        }
    }
    assert_eq!(monitor.snapshot("Server 1").state, HealthState::Offline);
    let before = probe.count("Server 1");

    f.advance(Duration::from_secs(5)); // well inside the 60 s back-off
    monitor.tick().await;
    assert_eq!(probe.count("Server 1"), before);

    f.advance(settings().retry_max_delay);
    monitor.tick().await;
    assert_eq!(probe.count("Server 1"), before + 1);
}

#[tokio::test]
async fn every_signal_is_also_recorded_as_an_event_for_the_board() {
    let f = Fixture::new();
    let monitor = f.monitor(
        server1_down(&f.clock, Arc::new(AtomicBool::new(true))),
        true,
    );

    f.ticks(&monitor, settings().failure_threshold).await;

    assert!(
        monitor
            .recent_events()
            .iter()
            .any(|e| e.server == "Server 1" && e.kind == SignalKind::ServerOffline)
    );
    assert!(
        f.sink
            .signals()
            .contains(&("Server 1".into(), SignalKind::ServerOffline))
    );
}

#[tokio::test]
async fn with_the_board_on_individual_alerts_are_suppressed_but_events_are_still_recorded() {
    let f = Fixture::new();
    let monitor = f.monitor(
        server1_down(&f.clock, Arc::new(AtomicBool::new(true))),
        false,
    );

    f.ticks(&monitor, settings().failure_threshold).await;

    assert!(f.sink.signals().is_empty()); // the one board is the only surface
    assert!(
        monitor
            .recent_events()
            .iter()
            .any(|e| e.server == "Server 1" && e.kind == SignalKind::ServerOffline)
    );
}

#[tokio::test]
async fn the_event_buffer_is_bounded_and_keeps_the_newest() {
    let f = Fixture::new();
    let down = Arc::new(AtomicBool::new(false));
    let monitor = f.monitor(server1_down(&f.clock, Arc::clone(&down)), true);

    for _ in 0..15 {
        down.store(true, Ordering::SeqCst);
        f.ticks(&monitor, settings().failure_threshold).await;
        down.store(false, Ordering::SeqCst);
        f.ticks(&monitor, settings().recovery_threshold).await;
    }

    let events = monitor.recent_events();
    assert!(
        events.len() <= 20,
        "the buffer must stay bounded, was {}",
        events.len()
    );
    assert_eq!(events.last().unwrap().kind, SignalKind::ServerOnline);
}

fn no_units() -> Arc<ServiceControl> {
    // No units: stats() asks systemd nothing, which is the graceful-degrade path.
    Arc::new(ServiceControl::new(
        Vec::new(),
        Some(false),
        Arc::new(FakeRunner::default()),
    ))
}

#[tokio::test]
async fn the_board_shows_every_server_and_the_recent_events() {
    let f = Fixture::new();
    let monitor = f.monitor(
        server1_down(&f.clock, Arc::new(AtomicBool::new(true))),
        false,
    );
    f.ticks(&monitor, settings().failure_threshold).await;

    let embed = MonitorBoard::new(Arc::clone(&monitor), no_units())
        .build()
        .await;

    let title = embed.title.clone().unwrap();
    assert!(title.contains("Server monitor"));
    assert!(title.starts_with("🔴")); // the worst server: offline
    let field = |name: &str| {
        embed
            .fields
            .iter()
            .find(|f| f.name.contains(name))
            .unwrap()
            .value
            .clone()
    };
    assert!(field("Server 1").contains("OFFLINE"));
    let s2 = field("Server 2");
    assert!(s2.contains("ONLINE"));
    for detail in ["RTT", "avg", "min", "max", "n=", "probed <t:"] {
        assert!(s2.contains(detail), "{detail} missing from {s2}");
    }
    let events = embed
        .fields
        .iter()
        .find(|f| f.name == "Recent events")
        .unwrap();
    assert!(events.value.contains("Server 1"));
}

#[tokio::test]
async fn the_board_stands_up_with_no_events_and_no_servers() {
    let f = Fixture::new();
    let clock = Arc::clone(&f.clock);
    let empty = f.monitor_with(
        ScriptedProbe::new(move |_| ok(&clock)),
        Arc::new(FakeTargets::new(&[])),
        false,
    );

    let embed = MonitorBoard::new(empty, no_units()).build().await;

    assert!(embed.description.unwrap().contains("No servers"));
}

/// A probe that waits until released, counting how many run at once per server.
struct GatedProbe {
    result: HealthProbe,
    gate: tokio::sync::Notify,
    released: AtomicBool,
    live: AtomicUsize,
    max_concurrent: AtomicUsize,
}

impl ServerProbe for GatedProbe {
    fn probe<'a>(&'a self, _server: &'a str) -> BoxFuture<'a, HealthProbe> {
        Box::pin(async move {
            let now = self.live.fetch_add(1, Ordering::SeqCst) + 1;
            self.max_concurrent.fetch_max(now, Ordering::SeqCst);
            while !self.released.load(Ordering::SeqCst) {
                self.gate.notified().await;
            }
            self.live.fetch_sub(1, Ordering::SeqCst);
            self.result.clone()
        })
    }
}

#[tokio::test]
async fn concurrent_ticks_never_overlap_probes_for_one_server() {
    let f = Fixture::new();
    let probe = Arc::new(GatedProbe {
        result: ok(&f.clock),
        gate: tokio::sync::Notify::new(),
        released: AtomicBool::new(false),
        live: AtomicUsize::new(0),
        max_concurrent: AtomicUsize::new(0),
    });
    let monitor = f.monitor_with(
        Arc::clone(&probe) as _,
        Arc::new(FakeTargets::new(&["Server 1"])),
        true,
    );

    let first = tokio::spawn({
        let m = Arc::clone(&monitor);
        async move { m.tick().await }
    });
    tokio::task::yield_now().await;
    let second = tokio::spawn({
        let m = Arc::clone(&monitor);
        async move { m.tick().await }
    });
    tokio::time::sleep(Duration::from_millis(50)).await;

    probe.released.store(true, Ordering::SeqCst);
    probe.gate.notify_waiters();
    first.await.unwrap();
    second.await.unwrap();

    assert_eq!(probe.max_concurrent.load(Ordering::SeqCst), 1); // the per-server gate held
}
