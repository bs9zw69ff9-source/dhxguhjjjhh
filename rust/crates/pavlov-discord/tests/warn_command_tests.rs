//! `/warn`: the escalation records a ban first and enforces it over RCON, a master owner's
//! pardon stands against it, and clearing needs Admin. (The C# has no test file.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::BanRecord;
use pavlov_core::security::OwnerGuard;
use pavlov_core::time::SystemClock;
use pavlov_discord::access::RequiredAccess;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::warn::{WarnCommand, required_for};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::moderation::bans::BanService;
use pavlov_host::moderation::master_names::StoredMasterNames;
use pavlov_host::moderation::warnings::WarningService;
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;

const OWNER: u64 = 3;

struct Rig {
    fake: FakeRconServer,
    store: Arc<SerializedStore>,
    masters: Arc<StoredMasterNames>,
    command: WarnCommand,
}

async fn rig() -> Rig {
    let fake = FakeRconServer::start().await.unwrap();
    let mut options = fake.options();
    options.name = "server1".into();
    options.command_spacing = Duration::ZERO;
    options.read_cache_duration = Duration::ZERO;
    options.max_attempts = 1;
    let metrics = Arc::new(MetricsRegistry::default());
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::clone(&metrics),
    ));
    let store = common::commands::store();
    let clock = Arc::new(SystemClock);
    let masters = Arc::new(
        StoredMasterNames::new([OwnerGuard::MASTER_NAME], Arc::clone(&store), clock.clone())
            .unwrap(),
    );
    let audit = Arc::new(AuditLog::new(Arc::clone(&store), clock.clone()));
    let command = WarnCommand {
        warnings: Arc::new(WarningService::new(
            Arc::clone(&store),
            Arc::clone(&audit),
            clock.clone(),
        )),
        bans: Arc::new(BanService::new(
            Arc::clone(&rcon),
            Arc::clone(&store),
            masters.clone(),
            clock.clone(),
        )),
        tracking: Arc::new(IpTrackingService::new(
            Arc::clone(&store),
            metrics,
            clock.clone(),
        )),
        store: Arc::clone(&store),
        access: common::commands::access(&store, &[OWNER]),
        audit,
        masters: Arc::clone(&masters),
        clock,
    };
    Rig {
        fake,
        store,
        masters,
        command,
    }
}

fn give(player: &str) -> Invocation {
    Invocation::new("warn", Caller::user(OWNER)).with(InvokedOption::sub(
        "give",
        vec![
            InvokedOption::value(
                "playerid",
                OptionKind::String,
                OptionValue::String(player.into()),
            ),
            InvokedOption::value(
                "reason",
                OptionKind::String,
                OptionValue::String("rdm".into()),
            ),
        ],
    ))
}

async fn run(command: &WarnCommand, invocation: &Invocation) -> String {
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
fn clearing_needs_admin_and_everything_else_mod() {
    assert_eq!(required_for(Some("clear")), RequiredAccess::Admin);
    for sub in ["give", "list", "remove", "standings"] {
        assert_eq!(required_for(Some(sub)), RequiredAccess::Mod, "{sub}");
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn the_third_warning_records_a_ban_and_enforces_it() {
    let rig = rig().await;
    let first = run(&rig.command, &give("Griefer")).await;
    assert!(first.contains("Warned"), "{first}");
    run(&rig.command, &give("Griefer")).await;
    let third = run(&rig.command, &give("Griefer")).await;
    assert!(third.contains("Escalated"), "{third}");

    let bans: Vec<BanRecord> = rig.store.read("tempbans", Vec::new()).unwrap();
    assert_eq!(bans.len(), 1);
    assert_eq!(bans[0].player_id, "Griefer");
    assert!(!bans[0].permanent && bans[0].expires.is_some());
    // No ModSave is configured here, so the ban is recorded and Griefer kicked, and the reply
    // says it could not be written rather than claiming it landed.
    let commands = rig.fake.commands();
    assert!(commands.iter().any(|c| c == "Kick Griefer"), "{commands:?}");
    assert!(
        !commands.iter().any(|c| c.starts_with("Ban ")),
        "{commands:?}"
    );
    assert!(third.contains("MODSAVE_PATH"), "{third}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_master_pardon_stands_against_the_escalation() {
    let rig = rig().await;
    rig.masters.pardon("Pardoned").await.unwrap();
    for _ in 0..3 {
        run(&rig.command, &give("Pardoned")).await;
    }
    let bans: Vec<BanRecord> = rig.store.read("tempbans", Vec::new()).unwrap();
    assert!(bans.is_empty());
    assert!(!rig.fake.commands().iter().any(|c| c.starts_with("Ban")));
}
