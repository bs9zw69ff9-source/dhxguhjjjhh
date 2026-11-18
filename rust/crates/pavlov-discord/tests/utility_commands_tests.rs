//! `/kick`, `/announce`, `/health` and `/help`. The two RCON commands are checked against the
//! fake server's recorded commands, not their format strings. (The C# has no test file.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::catalog::{CommandCatalog, CommandSummary};
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::utility::{AnnounceCommand, KickCommand, help_embed, recent_errors};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::moderation::bans::{BanService, MasterNames};
use pavlov_host::registry::RconRegistry;
use pavlov_host::services::registry::{ServiceFailure, ServiceState, ServiceStatus};
use pavlov_rcon::testing::FakeRconServer;

const MOD: u64 = 5;

struct NoMasters;
impl MasterNames for NoMasters {
    fn is_master(&self, _: &str) -> bool {
        false
    }
    fn is_exempt(&self, _: &str) -> bool {
        false
    }
    fn is_protected(&self, _: &str) -> bool {
        false
    }
    fn exempt<'a>(&'a self, _: &'a str, _: Option<Duration>) -> BoxFuture<'a, ()> {
        Box::pin(async {})
    }
}

async fn live() -> (FakeRconServer, Arc<RconRegistry>) {
    let fake = FakeRconServer::start().await.unwrap();
    let mut options = fake.options();
    options.name = "server1".into();
    options.command_spacing = Duration::ZERO;
    options.read_cache_duration = Duration::ZERO;
    options.max_attempts = 1;
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    (fake, rcon)
}

fn text(name: &str, value: &str) -> InvokedOption {
    InvokedOption::value(name, OptionKind::String, OptionValue::String(value.into()))
}

#[tokio::test(flavor = "multi_thread")]
async fn announce_sends_notify_all_with_the_newline_stripped() {
    let (fake, rcon) = live().await;
    let store = common::commands::store();
    let command = AnnounceCommand {
        rcon,
        access: common::commands::access(&store, &[MOD]),
    };
    let responder = common::RecordingResponder::default();
    let invocation = Invocation::new("announce", Caller::user(MOD))
        .with(text("message", "Restart soon\nBan everyone"));
    command
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();

    let sent = fake.commands();
    assert_eq!(sent.len(), 1, "one line, never two: {sent:?}");
    assert!(sent[0].starts_with("Notify All Restart soon"), "{sent:?}");
    assert!(!sent[0].contains('\n'));
    assert!(responder.last_text().contains("Broadcast sent"));
}

#[tokio::test(flavor = "multi_thread")]
async fn kick_targets_the_name_and_never_bans() {
    let (fake, rcon) = live().await;
    let store = common::commands::store();
    let command = KickCommand {
        bans: Arc::new(BanService::new(
            Arc::clone(&rcon),
            Arc::clone(&store),
            Arc::new(NoMasters),
            Arc::new(SystemClock),
        )),
        rcon,
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
        access: common::commands::access(&store, &[MOD]),
    };
    let responder = common::RecordingResponder::default();
    let invocation = Invocation::new("kick", Caller::user(MOD))
        .with(text("playerid", "Griefer"))
        .with(text("reason", "spawn camping"));
    command
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();

    let sent = fake.commands();
    assert!(sent.iter().any(|c| c == "Kick Griefer"), "{sent:?}");
    assert!(!sent.iter().any(|c| c.starts_with("Ban")), "{sent:?}");
}

#[tokio::test]
async fn announce_is_refused_below_mod() {
    let (_fake, rcon) = live().await;
    let store = common::commands::store();
    let command = AnnounceCommand {
        rcon,
        access: common::commands::access(&store, &[MOD]),
    };
    let responder = common::RecordingResponder::default();
    let invocation = Invocation::new("announce", Caller::user(99)).with(text("message", "hi"));
    command
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    assert!(responder.last_text().contains("Not allowed"));
}

fn service(name: &str, errors: Vec<ServiceFailure>) -> ServiceStatus {
    ServiceStatus {
        name: name.into(),
        state: ServiceState::Running,
        uptime: Duration::ZERO,
        interval: None,
        ticks: 0,
        failures: errors.len() as u64,
        consecutive_failures: 0,
        overruns: 0,
        last_error: None,
        last_tick_at: None,
        last_duration_ms: None,
        errors,
    }
}

#[test]
fn recent_errors_stop_before_the_field_overflows() {
    let errors: Vec<ServiceFailure> = (0..40)
        .map(|i| ServiceFailure {
            at: Utc::now() - TimeDelta::minutes(i),
            message: format!("boom {i}\n{}", "x".repeat(200)),
        })
        .collect();
    let body = recent_errors(&[service("ban-sweep", errors)]);
    assert!(body.chars().count() <= 1024, "{}", body.chars().count());
    assert!(body.contains("boom 0"), "newest first");
    assert!(body.ends_with("more*"));
    assert!(!body.lines().next().unwrap().contains("xxxx\n"));
    assert!(recent_errors(&[service("quiet", Vec::new())]).is_empty());
}

#[test]
fn help_lists_every_command_with_the_callers_level() {
    let catalog = CommandCatalog::new();
    catalog.populate([
        CommandSummary {
            name: "whitelist".into(),
            description: "Faction Leader - roster".into(),
        },
        CommandSummary {
            name: "ban".into(),
            description: "Mod - Ban a player".into(),
        },
    ]);
    let embed = help_embed(&catalog, "Public");
    let body = embed.description.as_deref().unwrap();
    assert!(body.starts_with("Your access: **Public**"));
    assert!(
        body.find("/ban").unwrap() < body.find("/whitelist").unwrap(),
        "sorted by name"
    );
}
