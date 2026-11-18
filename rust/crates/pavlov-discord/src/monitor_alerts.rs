//! The monitor's alerts in a channel (C# `Monitoring/MonitorAlertSink.cs`: `MonitorEmbeds` and
//! `DiscordMonitorAlertSink`). The embed and the ping rule are here and tested; posting goes
//! through [`AlertPoster`], which the gateway adapter implements.
//!
//! The role is pinged only for Warning and Critical, and only that role: allowed mentions are
//! set to exactly it, so a server name in the embed can never ping anybody.

use std::sync::Arc;

use chrono::Utc;
use futures_util::future::BoxFuture;
use pavlov_core::embed::{Embed, palette};
use pavlov_core::monitoring::{HealthState, MonitorSignal, ServerHealth, Severity, SignalKind};
use pavlov_core::skin::police;
use pavlov_core::text::Sanitize;
use pavlov_core::text::number::group_thousands;
use pavlov_host::monitoring::MonitorAlertSink;
use pavlov_host::monitoring::board::state_name;

use crate::interaction::DiscordError;

/// Posts one alert, pinging `role` (and only it) when given.
pub trait AlertPoster: Send + Sync {
    fn post_alert(
        &self,
        channel_id: u64,
        role: Option<u64>,
        embed: Embed,
    ) -> BoxFuture<'_, Result<(), DiscordError>>;
}

fn heading(kind: SignalKind) -> (&'static str, &'static str) {
    match kind {
        SignalKind::ServerOffline => ("🚨", "SERVER OFFLINE"),
        SignalKind::ServerOnline => ("✅", "SERVER ONLINE"),
        SignalKind::StillOffline => ("⏳", "STILL OFFLINE"),
        SignalKind::RconDisconnected => ("🔌", "RCON DISCONNECTED"),
        SignalKind::RconReconnected => ("✅", "RCON RECONNECTED"),
        SignalKind::RepeatedRconFailures => ("🚨", "RCON FAILING"),
        SignalKind::HighLatency => ("🐌", "HIGH RCON LATENCY"),
        SignalKind::LatencyRecovered => ("✅", "LATENCY RECOVERED"),
        SignalKind::LogInactive => ("🔇", "LOG WENT QUIET"),
        SignalKind::UnexpectedRestart => ("♻️", "UNEXPECTED RESTART"),
        SignalKind::PlayerCountZero => ("📉", "PLAYERS DROPPED TO ZERO"),
        SignalKind::PlayerCapacityReached => ("📈", "SERVER FULL"),
        SignalKind::PlayerAnomaly => ("📉", "PLAYER COUNT ANOMALY"),
    }
}

/// One alert's embed (C# `MonitorEmbeds.For`).
#[must_use]
pub fn alert_embed(server: &str, signal: &MonitorSignal, health: &ServerHealth) -> Embed {
    let colors = &police().colors;
    let color = match signal.severity {
        Severity::Critical => colors.ban_red,
        Severity::Warning => colors.amber,
        Severity::Info => palette::GREEN,
    };
    let (icon, title) = heading(signal.kind);
    let mut embed = Embed::new()
        .color(color)
        .title(format!("{icon} {title}"))
        .description(Sanitize::message(Some(&signal.detail)))
        .field("Server", Sanitize::code(Some(server)), true)
        .field("Status", state_name(health.state), true);
    if let Some(latency) = health.latency.current {
        embed = embed.field(
            "RCON latency",
            format!(
                "{}ms",
                group_thousands(i64::try_from(latency.as_millis()).unwrap_or(i64::MAX))
            ),
            true,
        );
    }
    if let Some(players) = health.players {
        let max = health
            .max_players
            .map(|m| format!("/{m}"))
            .unwrap_or_default();
        embed = embed.field("Players", format!("{players}{max}"), true);
    }
    embed.timestamp(Utc::now())
}

/// Whether an alert pings the role.
#[must_use]
pub fn pings(severity: Severity) -> bool {
    severity >= Severity::Warning
}

/// The sink.
pub struct ChannelMonitorAlerts {
    poster: Arc<dyn AlertPoster>,
    channel: Option<u64>,
    role: Option<u64>,
}

impl std::fmt::Debug for ChannelMonitorAlerts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ChannelMonitorAlerts")
            .field("channel", &self.channel)
            .field("role", &self.role)
            .finish_non_exhaustive()
    }
}

impl ChannelMonitorAlerts {
    #[must_use]
    pub fn new(poster: Arc<dyn AlertPoster>, channel: Option<u64>, role: Option<u64>) -> Self {
        Self {
            poster,
            channel,
            role,
        }
    }
}

impl MonitorAlertSink for ChannelMonitorAlerts {
    fn post<'a>(
        &'a self,
        server: &'a str,
        signal: &'a MonitorSignal,
        health: &'a ServerHealth,
    ) -> BoxFuture<'a, ()> {
        Box::pin(async move {
            let Some(channel) = self.channel else {
                return;
            };
            let role = self.role.filter(|_| pings(signal.severity));
            if let Err(error) = self
                .poster
                .post_alert(channel, role, alert_embed(server, signal, health))
                .await
            {
                tracing::warn!(server, kind = ?signal.kind, %error, "could not post a monitor alert");
            }
        })
    }

    fn test(&self) -> BoxFuture<'_, String> {
        Box::pin(async move {
            let Some(channel) = self.channel else {
                return "No monitoring channel is configured. Set `MONITOR_ALERT_CHANNEL` in this \
                        bot's `.env`."
                    .to_owned();
            };
            let signal = MonitorSignal {
                kind: SignalKind::ServerOnline,
                severity: Severity::Info,
                detail: "Monitoring test alert - if you can see this, the channel and permissions \
                         are working. Real alerts fire only when something changes (offline, \
                         recovery, high latency, and so on)."
                    .to_owned(),
            };
            let health = ServerHealth {
                state: HealthState::Online,
                ..ServerHealth::initial()
            };
            match self
                .poster
                .post_alert(channel, self.role, alert_embed("test", &signal, &health))
                .await
            {
                Ok(()) => {
                    let pinged = self
                        .role
                        .map(|r| format!(" and pinged <@&{r}>"))
                        .unwrap_or_default();
                    format!(
                        "Posted a test alert to <#{channel}>{pinged}. If it did not appear there, \
                         the bot is missing a permission in that channel."
                    )
                }
                Err(error) => format!(
                    "Could not post: {error}. The bot most likely lacks **Send Messages** or \
                     **Embed Links** in <#{channel}>."
                ),
            }
        })
    }
}
