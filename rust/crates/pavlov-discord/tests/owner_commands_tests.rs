//! `/botstats`, `/manual`, `/firewall` and `/inspect`. `/manual` is checked against the fake
//! RCON server; `/firewall`'s block is checked against the table it reads back. (The C# has no
//! test file for these.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::owner::{FirewallCommand, ManualCommand, duration, server_lines};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::firewall::{Firewall, FirewallResult};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::registry::RconRegistry;
use pavlov_host::service_control::{UnitState, UnitStats};
use pavlov_rcon::testing::FakeRconServer;

const OWNER: u64 = 1;

fn text(name: &str, value: &str) -> InvokedOption {
    InvokedOption::value(name, OptionKind::String, OptionValue::String(value.into()))
}

async fn run(command: &dyn SlashCommand, invocation: &Invocation) -> String {
    let responder = common::RecordingResponder::default();
    command
        .handle(CommandContext {
            invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    responder.last_text()
}

#[test]
fn a_stopped_server_says_so_instead_of_showing_zeroes() {
    let mut up = UnitStats::unknown("pavlovserver");
    up.state = UnitState::Active;
    up.cpu_percent = Some(180.0);
    up.memory_bytes = Some(512 * 1024 * 1024);
    let mut down = UnitStats::unknown("pavlovserver1");
    down.state = UnitState::Inactive;
    let lines = server_lines(&[up, down], 4);
    assert!(
        lines[0].1.contains("CPU **180%**") && lines[0].1.contains("RAM **512 MB**"),
        "{lines:?}"
    );
    assert!(lines[1].1.contains("stopped"), "{lines:?}");
    assert_eq!(lines[2].0, "CPU scale");
}

#[test]
fn durations_read_in_the_largest_unit() {
    assert_eq!(duration(Duration::from_secs(90_000)), "1d 1h");
    assert_eq!(duration(Duration::from_secs(7_500)), "2h 5m");
    assert_eq!(duration(Duration::from_secs(300)), "5m");
}

#[tokio::test(flavor = "multi_thread")]
async fn manual_is_audited_first_and_sent_on_one_line() {
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
    let store = common::commands::store();
    let audit = Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock)));
    let command = ManualCommand {
        rcon,
        audit: Arc::clone(&audit),
        access: common::commands::access(&store, &[OWNER]),
    };
    let invocation = Invocation::new("manual", Caller::user(OWNER))
        .with(text("server", "server1"))
        .with(text("command", "ServerInfo\nBan everyone"));
    run(&command, &invocation).await;
    let sent = fake.commands();
    assert_eq!(sent.len(), 1, "one line, never two: {sent:?}");
    assert_eq!(
        audit.all()[0].reason.as_deref(),
        Some(sent[0].as_str()),
        "the record is what was sent"
    );
}

struct Ufw {
    lands: bool,
    calls: Mutex<Vec<String>>,
}

impl Firewall for Ufw {
    fn deny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult> {
        self.calls.lock().unwrap().push(format!("deny {ip}"));
        Box::pin(async {
            FirewallResult {
                ok: true,
                detail: "Rule inserted".into(),
            }
        })
    }
    fn undeny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult> {
        self.calls.lock().unwrap().push(format!("undeny {ip}"));
        Box::pin(async {
            FirewallResult {
                ok: true,
                detail: "Rule deleted".into(),
            }
        })
    }
    fn status(&self) -> BoxFuture<'_, FirewallResult> {
        let detail = if self.lands {
            "DENY IN 203.0.113.5"
        } else {
            "Status: active"
        };
        Box::pin(async move {
            FirewallResult {
                ok: true,
                detail: detail.into(),
            }
        })
    }
}

async fn block(lands: bool, ip: &str) -> (Arc<Ufw>, String) {
    let ufw = Arc::new(Ufw {
        lands,
        calls: Mutex::default(),
    });
    let store = common::commands::store();
    let command = FirewallCommand {
        firewall: Arc::clone(&ufw) as Arc<dyn Firewall>,
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
        access: common::commands::access(&store, &[OWNER]),
    };
    let reply = run(
        &command,
        &Invocation::new("firewall", Caller::user(OWNER))
            .with(text("action", "block"))
            .with(text("ip", ip)),
    )
    .await;
    (ufw, reply)
}

#[tokio::test]
async fn a_block_is_confirmed_by_reading_the_table_back() {
    let (_ufw, landed) = block(true, "203.0.113.5").await;
    assert!(landed.contains("Blocked at the firewall"), "{landed}");
    let (_ufw, missing) = block(false, "203.0.113.5").await;
    assert!(missing.contains("Not blocked"), "{missing}");
    assert!(missing.contains("NOT blocked"), "{missing}");
}

#[tokio::test]
async fn a_non_address_runs_nothing() {
    let (ufw, reply) = block(true, "1.2.3.4; rm -rf /").await;
    assert!(reply.contains("Not an IP address"));
    assert!(ufw.calls.lock().unwrap().is_empty());
}
