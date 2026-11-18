//! `/monitor`: the status card renders the monitor's live snapshot with the board's own
//! formatting, an unknown server is named back, systemd being unavailable just omits CPU/RAM,
//! and `test` needs Admin. (The C# has no test file for the command.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use chrono::Utc;
use futures_util::future::BoxFuture;
use pavlov_core::monitoring::{HealthProbe, MonitorSettings, MonitorSignal, ServerHealth};
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::monitor::MonitorCommand;
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::monitoring::{
    MonitorAlertSink, MonitorHistory, MonitorParts, MonitorTargets, ServerMonitor, ServerProbe,
};
use pavlov_host::process::{CommandRunner, ProcessOutcome};
use pavlov_host::service_control::ServiceControl;

const OWNER: u64 = 1;

struct Sink;
impl MonitorAlertSink for Sink {
    fn post<'a>(
        &'a self,
        _: &'a str,
        _: &'a MonitorSignal,
        _: &'a ServerHealth,
    ) -> BoxFuture<'a, ()> {
        Box::pin(async {})
    }
    fn test(&self) -> BoxFuture<'_, String> {
        Box::pin(async { "posted to #monitoring".to_owned() })
    }
}

struct Targets;
impl MonitorTargets for Targets {
    fn servers(&self) -> Vec<String> {
        vec!["server1".into()]
    }
    fn reset_session<'a>(&'a self, _: &'a str) -> BoxFuture<'a, ()> {
        Box::pin(async {})
    }
}

struct Up;
impl ServerProbe for Up {
    fn probe<'a>(&'a self, _: &'a str) -> BoxFuture<'a, HealthProbe> {
        Box::pin(async {
            HealthProbe {
                rcon_ok: true,
                latency: Some(Duration::from_millis(42)),
                players: Some(3),
                max_players: Some(24),
                map: Some("Station".into()),
                process_running: Some(true),
                intentionally_stopped: false,
                last_log_activity: Some(Utc::now()),
                failure_reason: None,
                at: Utc::now(),
            }
        })
    }
}

/// No systemd here: every call fails to start.
#[derive(Debug)]
struct NoSystemd;
impl CommandRunner for NoSystemd {
    fn run<'a>(
        &'a self,
        _: &'a str,
        _: &'a [String],
        _: Duration,
        _: Option<&'a str>,
    ) -> BoxFuture<'a, ProcessOutcome> {
        Box::pin(async {
            ProcessOutcome {
                started: false,
                timed_out: false,
                exit_code: -1,
                stdout: String::new(),
                stderr: "not found".into(),
            }
        })
    }
}

async fn command() -> MonitorCommand {
    let store = common::commands::store();
    let monitor = Arc::new(ServerMonitor::new(MonitorParts {
        probe: Arc::new(Up),
        alerts: Arc::new(Sink),
        history: Arc::new(MonitorHistory::new(
            Arc::clone(&store),
            Arc::new(SystemClock),
        )),
        settings: MonitorSettings::default(),
        targets: Arc::new(Targets),
        clock: Arc::new(SystemClock),
        post_individual_alerts: false,
    }));
    monitor.tick().await;
    MonitorCommand {
        monitor,
        service: Arc::new(ServiceControl::new(
            vec!["pavlovserver".into()],
            Some(false),
            Arc::new(NoSystemd),
        )),
        alerts: Arc::new(Sink),
        access: common::commands::access(&store, &[OWNER]),
    }
}

fn sub(name: &str, server: Option<&str>, user: u64) -> Invocation {
    let options = server
        .map(|s| {
            vec![InvokedOption::value(
                "server",
                OptionKind::String,
                OptionValue::String(s.into()),
            )]
        })
        .unwrap_or_default();
    Invocation::new("monitor", Caller::user(user)).with(InvokedOption::sub(name, options))
}

async fn reply(command: &MonitorCommand, invocation: &Invocation) -> pavlov_core::embed::Embed {
    let responder = common::RecordingResponder::default();
    command
        .handle(CommandContext {
            invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    let common::Sent::Edit { reply } = responder.sent().last().cloned().unwrap() else {
        panic!()
    };
    reply.embeds[0].clone()
}

#[tokio::test]
async fn the_status_card_shows_the_live_snapshot_and_unknown_cpu_without_systemd() {
    let command = command().await;
    let card = reply(&command, &sub("status", Some("SERVER1"), OWNER)).await;
    assert_eq!(card.field_named("Status").unwrap().value, "ONLINE");
    assert_eq!(card.field_named("Players").unwrap().value, "3/24");
    assert_eq!(card.field_named("Map").unwrap().value, "Station");
    assert!(
        card.field_named("Latency")
            .unwrap()
            .value
            .starts_with("42ms")
    );
    assert_eq!(
        card.field_named("CPU").unwrap().value,
        "—",
        "systemd could not answer"
    );
}

#[tokio::test]
async fn an_unknown_server_is_named_back() {
    let command = command().await;
    let card = reply(&command, &sub("status", Some("server9"), OWNER)).await;
    assert!(
        card.description
            .as_deref()
            .unwrap()
            .contains("`server9` is not one of: server1")
    );
}

#[tokio::test]
async fn the_overview_lists_every_server() {
    let command = command().await;
    let card = reply(&command, &sub("status", None, OWNER)).await;
    assert!(
        card.description
            .as_deref()
            .unwrap()
            .contains("**server1** — ONLINE · 3/24 · 42ms")
    );
}

#[tokio::test]
async fn test_needs_admin_and_reports_what_the_sink_did() {
    let command = command().await;
    assert!(
        reply(&command, &sub("test", None, 99))
            .await
            .title
            .as_deref()
            .unwrap()
            .contains("Not allowed")
    );
    let done = reply(&command, &sub("test", None, OWNER)).await;
    assert_eq!(done.description.as_deref(), Some("posted to #monitoring"));
}
