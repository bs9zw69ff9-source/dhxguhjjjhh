//! `/players`, `/errors`, `/testalert` and `/counts`: their definitions register, and the
//! replies say what the C# says. (The C# has no test file for these four.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use chrono::TimeDelta;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::problems;
use pavlov_discord::commands::counts;
use pavlov_discord::commands::errors::{ErrorsCommand, innermost};
use pavlov_discord::commands::players::{PlayersCommand, describe};
use pavlov_discord::commands::test_alert::{self, TestAlertCommand};
use pavlov_discord::interaction::Invocation;
use pavlov_discord::paged::Paged;
use pavlov_discord::player_count::Targets;
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::alerts::{AlertDelivery, SecurityAlerts};
use pavlov_host::recent_errors::RecentErrors;
use pavlov_host::registry::RconRegistry;

const OWNER: u64 = 1;

fn rcon() -> Arc<RconRegistry> {
    Arc::new(RconRegistry::with_system_clock(
        &[],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ))
}

#[derive(Debug)]
struct Oops;
impl std::fmt::Display for Oops {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("it broke")
    }
}
impl std::error::Error for Oops {}

#[test]
fn every_definition_is_within_discords_limits() {
    let store = common::commands::store();
    let access = common::commands::access(&store, &[OWNER]);
    let alerts = Arc::new(SecurityAlerts::new());
    let specs = [
        PlayersCommand {
            rcon: rcon(),
            paged: Arc::new(Paged::new()),
            clock: Arc::new(SystemClock),
        }
        .build(),
        ErrorsCommand {
            errors: Arc::new(RecentErrors::new()),
            access: Arc::clone(&access),
        }
        .build(),
        TestAlertCommand {
            alerts,
            recipients: Vec::new(),
            access,
            clock: Arc::new(SystemClock),
        }
        .build(),
    ];
    for spec in &specs {
        assert!(
            problems(spec).is_empty(),
            "{}: {:?}",
            spec.name,
            problems(spec)
        );
    }
}

#[test]
fn ages_read_in_the_unit_a_person_would_use() {
    assert_eq!(describe(TimeDelta::seconds(45)), "45 seconds");
    assert_eq!(describe(TimeDelta::minutes(4)), "4 minutes");
    assert_eq!(describe(TimeDelta::hours(2)), "2 hours");
    assert_eq!(describe(TimeDelta::days(3)), "3 days");
}

#[test]
fn no_servers_says_so_rather_than_showing_nobody_online() {
    let command = PlayersCommand {
        rcon: rcon(),
        paged: Arc::new(Paged::new()),
        clock: Arc::new(SystemClock),
    };
    let (_, sections) = command.sections();
    assert_eq!(sections, ["*No servers are configured.*"]);
}

#[test]
fn a_trace_keeps_its_innermost_frames() {
    let trace: Vec<String> = (1..=30).map(|i| format!("frame {i}")).collect();
    let kept = innermost(&trace.join("\n"));
    assert!(kept.starts_with("frame 17"), "{kept}");
    assert!(kept.ends_with("frame 30"));
    assert!(innermost(&"x".repeat(5000)).chars().count() <= 1004);
}

#[tokio::test]
async fn errors_lists_and_finds_by_the_id_shown() {
    let store = common::commands::store();
    let errors = Arc::new(RecentErrors::new());
    errors.record("ada985e1", "whitelist", 42, &Oops);
    let command = ErrorsCommand {
        errors,
        access: common::commands::access(&store, &[OWNER]),
    };
    assert!(
        command
            .list()
            .title
            .as_deref()
            .unwrap()
            .contains("1 recent failure")
    );
    let detail = command.detail("ADA985E1");
    assert!(detail.description.as_deref().unwrap().contains("it broke"));
    assert!(
        command
            .detail("nope")
            .title
            .as_deref()
            .unwrap()
            .ends_with("No such error")
    );
}

#[tokio::test]
async fn errors_refuses_anyone_but_an_owner() {
    let store = common::commands::store();
    let command = ErrorsCommand {
        errors: Arc::new(RecentErrors::new()),
        access: common::commands::access(&store, &[OWNER]),
    };
    let responder = common::RecordingResponder::default();
    let invocation = Invocation::new("errors", Caller::user(99));
    command
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    assert!(responder.last_text().contains("Not allowed"));
}

#[tokio::test]
async fn testalert_with_nobody_to_receive_it_says_how_to_fix_that() {
    let store = common::commands::store();
    let command = TestAlertCommand {
        alerts: Arc::new(SecurityAlerts::new()),
        recipients: Vec::new(),
        access: common::commands::access(&store, &[OWNER]),
        clock: Arc::new(SystemClock),
    };
    let responder = common::RecordingResponder::default();
    let invocation = Invocation::new("testalert", Caller::user(OWNER));
    command
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    assert!(responder.last_text().contains("SECURITY_DM_IDS"));
}

#[test]
fn a_partial_delivery_explains_the_two_causes() {
    let results = [
        AlertDelivery {
            user: 1,
            delivered: true,
            problem: None,
        },
        AlertDelivery {
            user: 2,
            delivered: false,
            problem: Some("closed DMs".into()),
        },
    ];
    let embed = test_alert::report(&results);
    assert!(
        embed
            .title
            .as_deref()
            .unwrap()
            .ends_with("Test alert reached 1 of 2")
    );
    assert!(embed.field_named("Why a DM does not arrive").is_some());
}

#[test]
fn a_half_configured_count_setup_names_the_missing_half() {
    let targets = Targets {
        server_channels: Vec::new(),
        total_channel: Some(4),
    };
    let embed = counts::report(&targets, &["total: renamed".to_owned()]);
    assert!(
        embed
            .title
            .as_deref()
            .unwrap()
            .ends_with("Player-count channels updated")
    );
    assert!(
        embed
            .field_named("Per-server channels not configured")
            .is_some()
    );
    let failed = counts::report(
        &targets,
        &["total: FAILED (needs Manage Channels)".to_owned()],
    );
    assert!(failed.field_named("Most likely").is_some());
}
