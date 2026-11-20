//! `/warrant`, `/backgroundcheck` and `/bail`. (The C# has no test file for these; the stored
//! shapes are pinned by `ArrestInteropTests` and `StoredShapeTests`.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;

use chrono::{TimeDelta, Utc};
use pavlov_core::police::{Arrest, PoliceConfig, Warrant};
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::police::{BailCommand, WarrantCommand, record, settings};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::paged::Paged;
use pavlov_discord::slash::{CommandContext, SlashCommand};

const OWNER: u64 = 2;

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

#[tokio::test]
async fn anyone_may_check_a_warrant_but_only_police_issue_one() {
    let store = common::commands::store();
    let command = WarrantCommand {
        store: Arc::clone(&store),
        access: common::commands::access(&store, &[OWNER]),
        paged: Arc::new(Paged::new()),
        clock: Arc::new(SystemClock),
    };
    let issue = |who: u64| {
        Invocation::new("warrant", Caller::user(who)).with(InvokedOption::sub(
            "give",
            vec![text("playerid", "Robber"), text("reason", "bank job")],
        ))
    };
    assert!(run(&command, &issue(99)).await.contains("Not allowed"));
    run(&command, &issue(OWNER)).await;

    let check = Invocation::new("warrant", Caller::user(99)).with(InvokedOption::sub(
        "check",
        vec![text("playerid", "robber")],
    ));
    let reply = run(&command, &check).await;
    assert!(
        reply.contains("bank job"),
        "a stranger can see it, case-insensitively: {reply}"
    );
}

#[test]
fn a_record_lists_the_latest_arrests_first() {
    let arrest = |hours: i64, code: &str| Arrest {
        player: "Robber".into(),
        codes: vec![code.into()],
        jail_minutes: 5,
        bail: 100,
        arrested_by: "Officer".into(),
        at: Utc::now() - TimeDelta::hours(hours),
    };
    let warrants = [Warrant {
        player: "Robber".into(),
        reason: "escape".into(),
        issued_by: "x".into(),
        at: Utc::now(),
    }];
    let embed = record(
        "Robber",
        &[arrest(48, "PC 1"), arrest(1, "PC 2")],
        &warrants,
    );
    let recent = embed.field_named("Recent arrests").unwrap();
    assert!(recent.value.find("PC 2").unwrap() < recent.value.find("PC 1").unwrap());
    assert_eq!(
        embed.field_named("Total jail time").unwrap().value,
        "10 min"
    );
    assert!(
        record("Nobody", &[], &[])
            .description
            .as_deref()
            .unwrap()
            .contains("no arrests or warrants")
    );
}

#[tokio::test]
async fn changing_the_bail_rate_keeps_the_jail_cap() {
    let store = common::commands::store();
    store
        .write(
            "police_config",
            &PoliceConfig {
                bail_rate: 1.0,
                max_jail_minutes: 20,
            },
        )
        .await
        .unwrap();
    let command = BailCommand {
        store: Arc::clone(&store),
        access: common::commands::access(&store, &[OWNER]),
    };
    let set = Invocation::new("bail", Caller::user(OWNER))
        .with(text("action", "set"))
        .with(InvokedOption::value(
            "percent",
            OptionKind::Integer,
            OptionValue::Integer(150),
        ));
    let reply = run(&command, &set).await;
    assert!(reply.contains("Now **150%**"), "{reply}");
    let config: PoliceConfig = store
        .read("police_config", PoliceConfig::default())
        .unwrap();
    assert_eq!(config.max_jail_minutes, 20);
    assert!((config.bail_rate - 1.5).abs() < f64::EPSILON);
}

#[tokio::test]
async fn showing_is_public_and_changing_is_not() {
    let store = common::commands::store();
    let command = BailCommand {
        store: Arc::clone(&store),
        access: common::commands::access(&store, &[OWNER]),
    };
    let show = Invocation::new("bail", Caller::user(99)).with(text("action", "show"));
    assert!(run(&command, &show).await.contains("Police settings"));
    let reset = Invocation::new("bail", Caller::user(99)).with(text("action", "reset"));
    assert!(run(&command, &reset).await.contains("Not allowed"));
    assert!(
        settings(&PoliceConfig::default())
            .field_named("Jail cap")
            .unwrap()
            .value
            .contains("Uncapped")
    );
}
