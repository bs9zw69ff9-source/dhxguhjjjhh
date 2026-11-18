//! The one live monitoring board: a field per server and the recent events (C#
//! `Monitoring/MonitorBoard.cs`, `MonitorFormat.cs`). CPU and RAM come from systemd and are
//! simply omitted when it cannot answer.

use std::collections::HashMap;
use std::fmt::Write as _;
use std::sync::Arc;
use std::time::Duration;

use chrono::Utc;
use pavlov_core::embed::{Embed, palette};
use pavlov_core::monitoring::{HealthState, Severity, humanize};
use pavlov_core::text::Sanitize;
use pavlov_core::text::number::group_thousands;

use super::ServerMonitor;
use crate::service_control::{ServiceControl, UnitControl, UnitStats, number_for};

const EVENTS_SHOWN: usize = 10;
const EVENTS_FIELD_BUDGET: usize = 900;
const DETAIL_MAX: usize = 110;

/// Whether RCON is answering in this state.
#[must_use]
pub fn answering(state: HealthState) -> bool {
    matches!(state, HealthState::Online | HealthState::Degraded)
}

#[must_use]
pub fn state_dot(state: HealthState) -> &'static str {
    match state {
        HealthState::Online => "🟢",
        HealthState::Degraded => "🟡",
        HealthState::Reconnecting => "🟠",
        HealthState::Offline => "🔴",
        HealthState::Unknown => "⚪",
    }
}

#[must_use]
pub fn state_colour(state: HealthState) -> u32 {
    match state {
        HealthState::Online => palette::GREEN,
        HealthState::Degraded | HealthState::Reconnecting => palette::AMBER,
        HealthState::Offline => palette::BAN_RED,
        HealthState::Unknown => palette::GREY,
    }
}

#[must_use]
pub fn severity_dot(severity: Severity) -> &'static str {
    match severity {
        Severity::Critical => "🔴",
        Severity::Warning => "🟡",
        Severity::Info => "🟢",
    }
}

#[must_use]
pub fn state_name(state: HealthState) -> &'static str {
    match state {
        HealthState::Unknown => "UNKNOWN",
        HealthState::Online => "ONLINE",
        HealthState::Degraded => "DEGRADED",
        HealthState::Offline => "OFFLINE",
        HealthState::Reconnecting => "RECONNECTING",
    }
}

fn rank(state: HealthState) -> u8 {
    match state {
        HealthState::Offline => 4,
        HealthState::Reconnecting => 3,
        HealthState::Degraded => 2,
        HealthState::Online => 1,
        HealthState::Unknown => 0,
    }
}

#[must_use]
pub fn ms(t: Duration) -> String {
    format!(
        "{}ms",
        group_thousands(i64::try_from(t.as_millis()).unwrap_or(i64::MAX))
    )
}

#[must_use]
#[allow(clippy::cast_precision_loss)]
pub fn mb(bytes: u64) -> String {
    format!("{:.0} MB", bytes as f64 / 1_048_576.0)
}

#[must_use]
pub fn cpu(u: &UnitStats) -> String {
    u.cpu_percent
        .map_or_else(|| "—".into(), |c| format!("{c:.1}%"))
}

#[must_use]
pub fn ram(u: &UnitStats) -> String {
    let Some(bytes) = u.memory_bytes else {
        return "—".into();
    };
    match u.memory_limit_bytes.filter(|l| *l > 0) {
        Some(limit) => format!("{} / {}", mb(bytes), mb(limit)),
        None => mb(bytes),
    }
}

#[must_use]
pub fn span(d: Duration) -> String {
    humanize(chrono::TimeDelta::from_std(d).unwrap_or(chrono::TimeDelta::MAX))
}

/// The board.
pub struct MonitorBoard {
    monitor: Arc<ServerMonitor>,
    service: Arc<ServiceControl>,
}

impl std::fmt::Debug for MonitorBoard {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MonitorBoard").finish_non_exhaustive()
    }
}

impl MonitorBoard {
    #[must_use]
    pub fn new(monitor: Arc<ServerMonitor>, service: Arc<ServiceControl>) -> Self {
        Self { monitor, service }
    }

    /// The board as it stands. Its header takes the WORST server's colour and dot.
    pub async fn build(&self) -> Embed {
        let servers = self.monitor.servers();
        let usage = self.usage(&servers).await;
        let worst = servers
            .iter()
            .map(|s| self.monitor.snapshot(s).state)
            .max_by_key(|s| rank(*s))
            .unwrap_or(HealthState::Unknown);

        let mut embed = Embed::new()
            .color(state_colour(worst))
            .title(format!("{} Server monitor", state_dot(worst)))
            .footer(format!(
                "{} server(s) · CPU%: cgroup, 100% = 1 core · RTT: RCON round-trip · updated {} UTC",
                servers.len(),
                Utc::now().format("%H:%M:%S")
            ))
            .timestamp(Utc::now());
        if servers.is_empty() {
            return embed.description("No servers are configured to monitor.");
        }
        for server in &servers {
            embed = embed.field(
                format!(
                    "{} {}",
                    state_dot(self.monitor.snapshot(server).state),
                    Sanitize::code(Some(server))
                ),
                self.server_lines(server, &usage),
                true,
            );
        }
        embed.field("Recent events", self.recent_events(), false)
    }

    fn server_lines(&self, server: &str, usage: &HashMap<String, UnitStats>) -> String {
        let h = self.monitor.snapshot(server);
        let mut out = format!("**{}**", state_name(h.state));
        let players = h.players.map_or_else(
            || "—".to_owned(),
            |p| match h.max_players {
                Some(m) => format!("{p}/{m}"),
                None => p.to_string(),
            },
        );
        let map = h
            .map
            .as_deref()
            .filter(|m| !m.is_empty())
            .map_or_else(|| "—".to_owned(), |m| Sanitize::code(Some(m)));
        let _ = writeln!(out, " · 👥 {players} · 🗺️ {map}");

        out.push_str("📶 RTT ");
        let lat = &h.latency;
        match lat.current {
            Some(current) => {
                out.push_str(&ms(current));
                if let (Some(avg), Some(min), Some(max)) = (lat.average, lat.min, lat.max) {
                    let _ = write!(
                        out,
                        " (avg {} / min {} / max {})",
                        ms(avg),
                        ms(min),
                        ms(max)
                    );
                }
                if lat.samples > 0 {
                    let _ = write!(out, " · n={}", lat.samples);
                }
            }
            None => out.push('—'),
        }
        out.push('\n');

        if let Some(u) = usage.get(server) {
            let _ = write!(out, "🔥 CPU {}", cpu(u));
            if let Some(tasks) = u.tasks {
                let _ = write!(out, " · 🧵 {tasks} thr");
            }
            if let Some(total) = u.cpu_total {
                let _ = write!(out, " · ⏳ {} cpu-time", span(total));
            }
            out.push('\n');
            let _ = write!(out, "💾 {}", ram(u));
            if let Some(peak) = u.memory_peak_bytes {
                let _ = write!(out, " · peak {}", mb(peak));
            }
            out.push('\n');
            let _ = writeln!(out, "⏱️ up {}", u.uptime.map_or_else(|| "—".into(), span));
        }

        let _ = write!(
            out,
            "🔌 {}",
            if answering(h.state) {
                "RCON up"
            } else {
                "RCON down"
            }
        );
        if let Some(at) = h.last_probe_at {
            let _ = write!(out, " · probed <t:{}:R>", at.timestamp());
        }
        out
    }

    fn recent_events(&self) -> String {
        let events = self.monitor.recent_events();
        if events.is_empty() {
            return "_No events yet. Transitions (offline, recovery, latency, player drops) show here._".into();
        }
        let mut out = String::new();
        for e in events.iter().rev().take(EVENTS_SHOWN) {
            let line = format!(
                "{} <t:{}:R> **{}** — {}\n",
                severity_dot(e.severity),
                e.at.timestamp(),
                Sanitize::code(Some(&e.server)),
                Sanitize::message(Some(&trim(&e.detail)))
            );
            if out.chars().count() + line.chars().count() > EVENTS_FIELD_BUDGET {
                break;
            }
            out.push_str(&line);
        }
        if out.is_empty() {
            "_(events pending)_".into()
        } else {
            out
        }
    }

    /// Unit stats by server. Anything systemd cannot answer is just left out.
    async fn usage(&self, servers: &[String]) -> HashMap<String, UnitStats> {
        let by_unit: HashMap<String, UnitStats> = self
            .service
            .stats()
            .await
            .into_iter()
            .map(|s| (s.unit.clone(), s))
            .collect();
        servers
            .iter()
            .filter_map(|server| {
                let unit = self.service.unit_for(number_for(server)?)?;
                by_unit.get(unit).map(|s| (server.clone(), s.clone()))
            })
            .collect()
    }
}

fn trim(detail: &str) -> String {
    let flat = detail.replace(['\r', '\n'], " ");
    if flat.chars().count() <= DETAIL_MAX {
        flat
    } else {
        let cut: String = flat.chars().take(DETAIL_MAX - 1).collect();
        format!("{cut}…")
    }
}

/// `🟢` or `🔴`.
#[must_use]
pub fn tick(ok: bool) -> &'static str {
    if ok { "🟢" } else { "🔴" }
}

/// `🟢`, `🔴`, or `⚪` when it could not be asked.
#[must_use]
pub fn unknownable(state: Option<bool>) -> &'static str {
    match state {
        Some(true) => "🟢",
        Some(false) => "🔴",
        None => "⚪",
    }
}
