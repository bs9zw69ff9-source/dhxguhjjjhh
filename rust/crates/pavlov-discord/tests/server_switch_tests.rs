//! `/serverswitch`: the action is validated, the stop warns and says it stays down, a start
//! warns nobody, and a failure carries systemd's answer and the fix. (The C# tests cover
//! ServiceControl's argv; this pins the command.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::server_switch::{ServerSwitchCommand, action_of};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::notice::PlayerNotice;
use pavlov_host::registry::RconRegistry;
use pavlov_host::service_control::{UnitAction, UnitControl, UnitResult, UnitState};

const OWNER: u64 = 1;

struct Systemd {
    units: Vec<String>,
    ok: bool,
    ran: Mutex<Vec<(String, UnitAction)>>,
}

impl UnitControl for Systemd {
    fn units(&self) -> &[String] {
        &self.units
    }
    fn state<'a>(&'a self, _: &'a str) -> BoxFuture<'a, UnitState> {
        Box::pin(async { UnitState::Active })
    }
    fn run<'a>(&'a self, unit: &'a str, action: UnitAction) -> BoxFuture<'a, UnitResult> {
        self.ran.lock().unwrap().push((unit.to_owned(), action));
        let ok = self.ok;
        Box::pin(async move { UnitResult::new(ok, unit, if ok { "" } else { "Access denied" }) })
    }
    fn advice(&self, results: &[UnitResult]) -> Option<String> {
        results
            .iter()
            .any(|r| !r.ok)
            .then(|| "add a sudoers line".to_owned())
    }
}

fn command(ok: bool) -> (Arc<Systemd>, ServerSwitchCommand) {
    let systemd = Arc::new(Systemd {
        units: vec!["pavlovserver".into(), "pavlovserver1".into()],
        ok,
        ran: Mutex::default(),
    });
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    let store = common::commands::store();
    let command = ServerSwitchCommand {
        services: Arc::clone(&systemd) as Arc<dyn UnitControl>,
        notice: Arc::new(PlayerNotice::new(Arc::clone(&rcon))),
        rcon,
        access: common::commands::access(&store, &[OWNER]),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
        grace: Duration::ZERO,
    };
    (systemd, command)
}

fn switch(server: i64, action: i64) -> Invocation {
    Invocation::new("serverswitch", Caller::user(OWNER))
        .with(InvokedOption::value(
            "server",
            OptionKind::Integer,
            OptionValue::Integer(server),
        ))
        .with(InvokedOption::value(
            "action",
            OptionKind::Integer,
            OptionValue::Integer(action),
        ))
}

async fn run(command: &ServerSwitchCommand, invocation: &Invocation) -> common::RecordingResponder {
    let responder = common::RecordingResponder::default();
    command
        .handle(CommandContext {
            invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    responder
}

#[test]
fn only_the_offered_actions_exist() {
    assert_eq!(action_of(0), Some(UnitAction::Start));
    assert_eq!(action_of(1), Some(UnitAction::Stop));
    assert_eq!(action_of(2), Some(UnitAction::Restart));
    assert_eq!(action_of(3), None);
    assert_eq!(action_of(-1), None);
}

#[tokio::test]
async fn a_stop_runs_on_the_chosen_unit_and_says_it_stays_down() {
    let (systemd, command) = command(true);
    let responder = run(&command, &switch(2, 1)).await;
    assert_eq!(
        *systemd.ran.lock().unwrap(),
        [("pavlovserver1".to_owned(), UnitAction::Stop)]
    );
    let text = responder.last_text();
    assert!(text.contains("is stopped"), "{text}");
    let sent = responder.sent();
    let common::Sent::Edit { reply } = sent.last().unwrap() else {
        panic!()
    };
    let embed = &reply.embeds[0];
    assert!(embed.fields.iter().any(|f| f.name.ends_with("Stays down")));
    assert!(
        embed
            .field_named("Players warned")
            .unwrap()
            .value
            .starts_with("No — "),
        "no RCON configured"
    );
}

#[tokio::test]
async fn a_start_owes_no_warning() {
    let (_systemd, command) = command(true);
    let responder = run(&command, &switch(1, 0)).await;
    assert_eq!(responder.sent().len(), 1, "no warning step");
    let common::Sent::Edit { reply } = &responder.sent()[0] else {
        panic!()
    };
    assert!(reply.embeds[0].field_named("Players warned").is_none());
}

#[tokio::test]
async fn an_unknown_action_or_server_runs_nothing() {
    let (systemd, command) = command(true);
    assert!(
        run(&command, &switch(1, 9))
            .await
            .last_text()
            .contains("Unknown action")
    );
    assert!(
        run(&command, &switch(7, 2))
            .await
            .last_text()
            .contains("Only 2 unit(s) are configured")
    );
    assert!(systemd.ran.lock().unwrap().is_empty());
}

#[tokio::test]
async fn a_failure_carries_systemds_answer_and_the_fix() {
    let (_systemd, command) = command(false);
    let responder = run(&command, &switch(1, 2)).await;
    let common::Sent::Edit { reply } = responder.sent().last().cloned().unwrap() else {
        panic!()
    };
    let embed = &reply.embeds[0];
    assert!(embed.title.as_deref().unwrap().ends_with("failed"));
    assert!(
        embed
            .field_named("systemd said")
            .unwrap()
            .value
            .contains("Access denied")
    );
    assert!(embed.fields.iter().any(|f| f.name.ends_with("Fix")));
}
