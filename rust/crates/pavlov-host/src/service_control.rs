//! Starting, stopping and asking about the game servers' systemd units (C#
//! `Servers/ServiceControl.cs`, `IUnitControl.cs`).
//!
//! Every unit name passes the allow-list before anything runs - in the runner, not only the
//! parser, so a caller reaching [`ServiceControl::run`] directly cannot skip it. There is no
//! shell (argv only), and the names still do not get through, because the cost of being wrong
//! about that reasoning exactly once is root.

use std::collections::HashMap;
use std::fmt;
use std::sync::Arc;
use std::time::{Duration, Instant};

use futures_util::future::BoxFuture;
use pavlov_core::servers::units::{DEFAULT_UNITS, is_plausible_unit_name};
use pavlov_rcon::ServerLifecycle;

use crate::process::CommandRunner;

/// How long a local state query may take; anything slower is broken.
const QUERY_TIMEOUT: Duration = Duration::from_secs(5);
/// A Pavlov server can take most of a minute to stop and come back, and a restart killed
/// halfway leaves it down - strictly worse than waiting.
const ACTION_TIMEOUT: Duration = Duration::from_secs(90);
/// CPU% is a delta over this window; one second matches `systemd-cgtop`.
const CPU_SAMPLE_WINDOW: Duration = Duration::from_secs(1);
const SHOW_PROPERTIES: &str = "ActiveState,MemoryCurrent,MemoryPeak,MemoryMax,CPUUsageNSec,TasksCurrent,ActiveEnterTimestampMonotonic";

/// What a systemctl action did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UnitResult {
    pub ok: bool,
    pub unit: String,
    pub detail: String,
}

impl UnitResult {
    #[must_use]
    pub fn new(ok: bool, unit: impl Into<String>, detail: impl Into<String>) -> Self {
        Self {
            ok,
            unit: unit.into(),
            detail: detail.into(),
        }
    }
}

/// The three things the bot does to a unit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum UnitAction {
    Start,
    Stop,
    Restart,
}

impl UnitAction {
    pub const ALL: [Self; 3] = [Self::Start, Self::Stop, Self::Restart];

    /// The systemctl verb.
    #[must_use]
    pub const fn verb(self) -> &'static str {
        match self {
            Self::Start => "start",
            Self::Stop => "stop",
            Self::Restart => "restart",
        }
    }
}

/// A unit's state as systemd reports it. UNKNOWN IS ITS OWN ANSWER: not being able to ask is
/// not the same as the answer being no.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum UnitState {
    Unknown,
    Active,
    Starting,
    Inactive,
    Failed,
}

impl UnitState {
    /// The word `systemctl is-active` (or `ActiveState=`) prints. An unrecognised word is
    /// Unknown rather than a guess, so a future state cannot silently read as Active.
    #[must_use]
    pub fn from_word(word: Option<&str>) -> Self {
        match word.map(str::trim) {
            Some("active") => Self::Active,
            Some("activating" | "reloading") => Self::Starting,
            Some("inactive" | "deactivating") => Self::Inactive,
            Some("failed") => Self::Failed,
            _ => Self::Unknown,
        }
    }
}

/// Resource use for one unit. `None` is "no answer" - never zero, which would read as a healthy
/// idle server.
#[derive(Debug, Clone, PartialEq)]
pub struct UnitStats {
    pub unit: String,
    pub state: UnitState,
    pub memory_bytes: Option<u64>,
    pub memory_peak_bytes: Option<u64>,
    pub memory_limit_bytes: Option<u64>,
    /// 100% is ONE core (the top and cgtop convention); a server across two cores reads 200%.
    pub cpu_percent: Option<f64>,
    /// CUMULATIVE processor time since the unit started - not usage.
    pub cpu_total: Option<Duration>,
    pub tasks: Option<u32>,
    pub uptime: Option<Duration>,
}

impl UnitStats {
    #[must_use]
    pub fn unknown(unit: impl Into<String>) -> Self {
        Self {
            unit: unit.into(),
            state: UnitState::Unknown,
            memory_bytes: None,
            memory_peak_bytes: None,
            memory_limit_bytes: None,
            cpu_percent: None,
            cpu_total: None,
            tasks: None,
            uptime: None,
        }
    }
}

/// What a caller needs from systemd, as a seam for tests.
pub trait UnitControl: Send + Sync {
    /// The configured units, in server order.
    fn units(&self) -> &[String];

    /// The unit's state; Unknown when it cannot be asked.
    fn state<'a>(&'a self, unit: &'a str) -> BoxFuture<'a, UnitState>;

    /// Run one action on a unit.
    fn run<'a>(&'a self, unit: &'a str, action: UnitAction) -> BoxFuture<'a, UnitResult>;

    /// How to fix these failures, when the cause is recognisable (no privilege, unknown
    /// unit names); `None` otherwise.
    fn advice(&self, _results: &[UnitResult]) -> Option<String> {
        None
    }

    /// The unit for a 1-based server number: server 1 is the first configured unit.
    fn unit_for(&self, server_number: i32) -> Option<&str> {
        let index = usize::try_from(server_number).ok()?.checked_sub(1)?;
        self.units().get(index).map(String::as_str)
    }
}

/// systemd, through a [`CommandRunner`].
pub struct ServiceControl {
    units: Vec<String>,
    use_sudo: bool,
    privileged: bool,
    runner: Arc<dyn CommandRunner>,
}

impl fmt::Debug for ServiceControl {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ServiceControl")
            .field("units", &self.units)
            .field("use_sudo", &self.use_sudo)
            .finish_non_exhaustive()
    }
}

/// The default units, as owned strings.
#[must_use]
pub fn default_units() -> Vec<String> {
    DEFAULT_UNITS.iter().map(|u| (*u).to_owned()).collect()
}

/// `serverN`, the RCON name of server N. One place, so the status check, the broadcast and the
/// systemctl call cannot end up talking about different servers.
#[must_use]
pub fn rcon_name_for(server_number: i32) -> String {
    format!("server{server_number}")
}

/// The server number in an RCON name (`server2` is 2).
#[must_use]
pub fn number_for(rcon_server_name: &str) -> Option<i32> {
    let digits = rcon_server_name
        .get(..6)
        .filter(|p| p.eq_ignore_ascii_case("server"))
        .map(|_| &rcon_server_name[6..])?;
    digits.parse::<i32>().ok().filter(|n| *n > 0)
}

fn parse_properties(stdout: &str) -> HashMap<String, String> {
    stdout
        .split('\n')
        .filter(|l| !l.is_empty())
        .filter_map(|line| {
            // Split on the FIRST '=' only: values contain them.
            let (key, value) = line.split_once('=')?;
            (!key.is_empty()).then(|| (key.to_owned(), value.trim().to_owned()))
        })
        .collect()
}

fn number(properties: &HashMap<String, String>, key: &str) -> Option<u64> {
    // systemd prints u64::MAX ("[not set]" in some builds) for "no value".
    properties
        .get(key)
        .and_then(|v| v.parse::<u64>().ok())
        .filter(|v| *v != u64::MAX && i64::try_from(*v).is_ok())
}

/// Seconds since boot, as `/proc/uptime` reports them.
fn seconds_since_boot() -> Option<f64> {
    std::fs::read_to_string("/proc/uptime")
        .ok()?
        .split_whitespace()
        .next()?
        .parse()
        .ok()
}

fn uptime_from(active_enter_monotonic_us: Option<u64>) -> Option<Duration> {
    let since = active_enter_monotonic_us.filter(|s| *s > 0)?;
    let now = seconds_since_boot()?;
    let uptime = now - since as f64 / 1_000_000.0;
    (uptime > 0.0).then(|| Duration::from_secs_f64(uptime))
}

impl ServiceControl {
    /// `use_sudo`: `None` decides from whether the bot is root (C# `PAVLOV_SYSTEMCTL_SUDO`).
    #[must_use]
    pub fn new(units: Vec<String>, use_sudo: Option<bool>, runner: Arc<dyn CommandRunner>) -> Self {
        let privileged = pavlov_storage::ownership::is_privileged();
        Self {
            units,
            use_sudo: use_sudo.unwrap_or(!privileged),
            privileged,
            runner,
        }
    }

    /// How systemctl is reached, for the startup summary.
    #[must_use]
    pub fn elevation(&self) -> &'static str {
        if self.use_sudo {
            "via sudo -n (the bot is not root)"
        } else if self.privileged {
            "directly (the bot is already root)"
        } else {
            "directly (forced by PAVLOV_SYSTEMCTL_SUDO - the bot is NOT root, so this will be refused)"
        }
    }

    async fn show(&self, unit: &str, only: Option<&str>) -> HashMap<String, String> {
        if !is_plausible_unit_name(unit) {
            return HashMap::new();
        }
        let argv = vec![
            "show".to_owned(),
            unit.to_owned(),
            format!("--property={}", only.unwrap_or(SHOW_PROPERTIES)),
        ];
        let run = self
            .runner
            .run("systemctl", &argv, QUERY_TIMEOUT, None)
            .await;
        if !run.started || run.timed_out {
            return HashMap::new();
        }
        parse_properties(&run.stdout)
    }

    /// Memory, CPU% over a one-second window, tasks and uptime for every unit, in order.
    pub async fn stats(&self) -> Vec<UnitStats> {
        let mut shown = Vec::with_capacity(self.units.len());
        for unit in &self.units {
            let properties = self.show(unit, None).await;
            let first = number(&properties, "CPUUsageNSec").map(|cpu| (cpu, Instant::now()));
            shown.push((properties, first));
        }
        if shown.iter().any(|(_, first)| first.is_some()) {
            tokio::time::sleep(CPU_SAMPLE_WINDOW).await;
        }
        let mut results = Vec::with_capacity(self.units.len());
        for (unit, (properties, first)) in self.units.iter().zip(shown) {
            let mut percent = None;
            if let Some((before, at)) = first {
                let after = self.show(unit, Some("CPUUsageNSec")).await;
                if let Some(now) = number(&after, "CPUUsageNSec") {
                    let elapsed = at.elapsed().as_nanos() as f64;
                    // A restart between samples resets the counter: dropped, not shown as zero.
                    if elapsed > 0.0 && now >= before {
                        percent = Some((now - before) as f64 / elapsed * 100.0);
                    }
                }
            }
            results.push(UnitStats {
                unit: unit.clone(),
                state: UnitState::from_word(properties.get("ActiveState").map(String::as_str)),
                memory_bytes: number(&properties, "MemoryCurrent"),
                memory_peak_bytes: number(&properties, "MemoryPeak"),
                memory_limit_bytes: number(&properties, "MemoryMax"),
                cpu_percent: percent,
                cpu_total: number(&properties, "CPUUsageNSec").map(Duration::from_nanos),
                tasks: number(&properties, "TasksCurrent").and_then(|t| u32::try_from(t).ok()),
                uptime: uptime_from(number(&properties, "ActiveEnterTimestampMonotonic")),
            });
        }
        results
    }

    /// Restart a unit.
    pub async fn restart(&self, unit: &str) -> UnitResult {
        self.run_action(unit, UnitAction::Restart).await
    }

    async fn state_of(&self, unit: &str) -> UnitState {
        if !is_plausible_unit_name(unit) {
            return UnitState::Unknown;
        }
        let argv = vec!["is-active".to_owned(), unit.to_owned()];
        let run = self
            .runner
            .run("systemctl", &argv, QUERY_TIMEOUT, None)
            .await;
        if !run.started || run.timed_out {
            return UnitState::Unknown;
        }
        // The EXIT CODE is not the answer - is-active exits non-zero for anything not running.
        UnitState::from_word(Some(&run.stdout))
    }

    async fn run_action(&self, unit: &str, action: UnitAction) -> UnitResult {
        if !is_plausible_unit_name(unit) {
            return UnitResult::new(
                false,
                unit,
                "not a usable systemd unit name - refused before running anything",
            );
        }
        let verb = action.verb();
        let (file, argv) = if self.use_sudo {
            // -n: fail rather than block forever on a password prompt nobody can answer.
            (
                "sudo",
                vec!["-n".into(), "systemctl".into(), verb.into(), unit.into()],
            )
        } else {
            ("systemctl", vec![verb.to_owned(), unit.to_owned()])
        };
        // At WARNING, always: "who took server 2 offline and when" is a question that gets asked.
        tracing::warn!(
            verb,
            unit,
            file,
            "systemctl action - players on it will be disconnected"
        );

        let run = self.runner.run(file, &argv, ACTION_TIMEOUT, None).await;
        if !run.started {
            return UnitResult::new(false, unit, format!("could not start {file}"));
        }
        if run.timed_out {
            tracing::error!(
                verb,
                unit,
                "systemctl timed out after 90s - it may still be in progress"
            );
            return UnitResult::new(
                false,
                unit,
                format!(
                    "`{verb}` timed out after 90s. It may still be in progress - check `systemctl status {unit}`"
                ),
            );
        }
        let output = run.combined();
        let ok = run.exit_code == 0;
        if ok {
            tracing::info!(verb, unit, "systemctl succeeded");
        } else {
            tracing::error!(verb, unit, code = run.exit_code, %output, "systemctl FAILED");
        }
        let detail = if !output.is_empty() {
            output
        } else if ok {
            format!("{verb} ok")
        } else {
            format!("exit code {}", run.exit_code)
        };
        UnitResult::new(ok, unit, detail)
    }

    /// What to do about a set of failures, when the cause is recognisable; `None` rather than a
    /// guess otherwise - confident wrong advice sends somebody to edit sudoers for nothing.
    #[must_use]
    pub fn advice(&self, results: &[UnitResult]) -> Option<String> {
        let failed: Vec<&UnitResult> = results.iter().filter(|r| !r.ok).collect();
        if failed.is_empty() {
            return None;
        }
        let text = failed
            .iter()
            .map(|f| f.detail.as_str())
            .collect::<Vec<_>>()
            .join(" ")
            .to_lowercase();
        let any = |needles: &[&str]| needles.iter().any(|n| text.contains(n));
        if any(&[
            "access denied",
            "permission denied",
            "interactive authentication required",
            "password is required",
            "a terminal is required",
        ]) {
            let permitted = self
                .units
                .iter()
                .flat_map(|u| {
                    UnitAction::ALL
                        .iter()
                        .map(move |a| format!("/bin/systemctl {} {u}", a.verb()))
                })
                .collect::<Vec<_>>()
                .join(", ");
            let user = std::env::var("USER").unwrap_or_else(|_| "pavlov-bot".into());
            return Some(format!(
                "The bot may not run `systemctl` as root. Add this line with `visudo`:\n```\n{user} ALL=(root) NOPASSWD: {permitted}\n```\nNo bot restart is needed."
            ));
        }
        if any(&["not found", "no such file"]) {
            return Some(format!(
                "systemd does not know these unit names. Check with `systemctl list-units 'pavlov*'` and set `PAVLOV_UNITS` (currently `{}`).",
                self.units.join(", ")
            ));
        }
        None
    }
}

impl UnitControl for ServiceControl {
    fn units(&self) -> &[String] {
        &self.units
    }

    fn state<'a>(&'a self, unit: &'a str) -> BoxFuture<'a, UnitState> {
        Box::pin(self.state_of(unit))
    }

    fn run<'a>(&'a self, unit: &'a str, action: UnitAction) -> BoxFuture<'a, UnitResult> {
        Box::pin(self.run_action(unit, action))
    }

    fn advice(&self, results: &[UnitResult]) -> Option<String> {
        ServiceControl::advice(self, results)
    }
}

impl ServerLifecycle for ServiceControl {
    /// A server somebody stopped is not an RCON fault: health asks systemd at report time.
    fn is_intentionally_stopped<'a>(&'a self, rcon_server_name: &'a str) -> BoxFuture<'a, bool> {
        Box::pin(async move {
            let Some(unit) = number_for(rcon_server_name).and_then(|n| self.unit_for(n)) else {
                return false;
            };
            self.state_of(unit).await == UnitState::Inactive
        })
    }
}
