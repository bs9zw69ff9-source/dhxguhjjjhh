//! The monitor's channel alerts (C# `MonitorEmbeds` and `DiscordMonitorAlertSink`; no C#
//! test file): the heading per signal, the role pinged only for warnings and worse, nothing
//! posted without a channel, and `/monitor test` saying what happened.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::monitoring::{
    HealthState, LatencyStats, MonitorSignal, ServerHealth, Severity, SignalKind,
};
use pavlov_discord::interaction::DiscordError;
use pavlov_discord::monitor_alerts::{AlertPoster, ChannelMonitorAlerts, alert_embed};
use pavlov_host::monitoring::MonitorAlertSink;

#[derive(Default)]
struct Poster {
    posted: Mutex<Vec<(u64, Option<u64>, Embed)>>,
    fail: bool,
}

impl AlertPoster for Poster {
    fn post_alert(
        &self,
        channel_id: u64,
        role: Option<u64>,
        embed: Embed,
    ) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.posted.lock().unwrap().push((channel_id, role, embed));
        let fail = self.fail;
        Box::pin(async move {
            if fail {
                Err(DiscordError("Missing Permissions".into()))
            } else {
                Ok(())
            }
        })
    }
}

fn signal(kind: SignalKind, severity: Severity) -> MonitorSignal {
    MonitorSignal {
        kind,
        severity,
        detail: "server1 stopped answering".into(),
    }
}

fn health() -> ServerHealth {
    ServerHealth {
        state: HealthState::Offline,
        players: Some(3),
        max_players: Some(24),
        latency: LatencyStats {
            current: Some(Duration::from_millis(1_250)),
            ..LatencyStats::default()
        },
        ..ServerHealth::initial()
    }
}

#[test]
fn an_alert_names_the_signal_the_server_and_what_was_last_seen() {
    let embed = alert_embed(
        "server1",
        &signal(SignalKind::ServerOffline, Severity::Critical),
        &health(),
    );
    assert_eq!(embed.title.as_deref(), Some("🚨 SERVER OFFLINE"));
    assert_eq!(embed.field_named("Status").unwrap().value, "OFFLINE");
    assert_eq!(embed.field_named("RCON latency").unwrap().value, "1,250ms");
    assert_eq!(embed.field_named("Players").unwrap().value, "3/24");
    assert!(embed.timestamp.is_some());
}

#[tokio::test]
async fn the_role_is_pinged_only_for_warnings_and_worse() {
    let poster = Arc::new(Poster::default());
    let sink = ChannelMonitorAlerts::new(poster.clone(), Some(10), Some(20));

    sink.post(
        "server1",
        &signal(SignalKind::ServerOffline, Severity::Critical),
        &health(),
    )
    .await;
    sink.post(
        "server1",
        &signal(SignalKind::ServerOnline, Severity::Info),
        &health(),
    )
    .await;

    let posted = poster.posted.lock().unwrap();
    assert_eq!((posted[0].0, posted[0].1), (10, Some(20)));
    assert_eq!(posted[1].1, None, "a recovery does not ping");
}

#[tokio::test]
async fn nothing_is_posted_without_a_channel_and_the_test_says_so() {
    let poster = Arc::new(Poster::default());
    let sink = ChannelMonitorAlerts::new(poster.clone(), None, Some(20));
    sink.post(
        "server1",
        &signal(SignalKind::ServerOffline, Severity::Critical),
        &health(),
    )
    .await;
    assert!(poster.posted.lock().unwrap().is_empty());
    assert!(sink.test().await.contains("MONITOR_ALERT_CHANNEL"));
}

#[tokio::test]
async fn the_test_alert_reports_success_and_a_refusal() {
    let sink = ChannelMonitorAlerts::new(Arc::new(Poster::default()), Some(10), Some(20));
    let said = sink.test().await;
    assert!(said.starts_with("Posted a test alert to <#10> and pinged <@&20>"));

    let refused = ChannelMonitorAlerts::new(
        Arc::new(Poster {
            fail: true,
            ..Poster::default()
        }),
        Some(10),
        None,
    );
    assert!(refused.test().await.contains("Missing Permissions"));
}
