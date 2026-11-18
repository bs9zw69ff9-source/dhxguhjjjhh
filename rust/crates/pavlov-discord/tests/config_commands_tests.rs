//! `/donator`, `/suspendrank` and `/setrconroles` against real roster files. (The C# has no
//! test file for these three.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;

use pavlov_config::{Config, FeatureOptions};
use pavlov_core::factions::FactionRegistry;
use pavlov_core::moderation::MenuRoleMap;
use pavlov_core::servers::DetectedServer;
use pavlov_core::servers::HostProbe;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::config::{
    DONATOR_ROSTER, DonatorCommand, SuspendRankCommand, mapping,
};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::paged::Paged;
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::factions::rank_restorer::RankSuspension;

const OWNER: u64 = 1;

/// A box with no Pavlov installs, as the test machine is.
struct EmptyBox;
impl HostProbe for EmptyBox {
    fn systemd_servers(&self) -> Vec<DetectedServer> {
        Vec::new()
    }
    fn installs_beside(&self, _: &str, _: &str) -> std::io::Result<Vec<String>> {
        Ok(Vec::new())
    }
    fn directory_exists(&self, _: &str) -> bool {
        false
    }
}

fn features(pairs: &[(&str, &str)]) -> FeatureOptions {
    FeatureOptions::bind(&Config::from_pairs(pairs.iter().copied()), &EmptyBox)
}

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
async fn adding_a_donator_writes_the_roster_and_the_record() {
    let dir = tempfile::tempdir().unwrap();
    std::fs::write(dir.path().join(DONATOR_ROSTER), "Existing\n").unwrap();
    let store = common::commands::store();
    let command = DonatorCommand {
        store: Arc::clone(&store),
        rosters: common::commands::rosters(Some(dir.path())),
        access: common::commands::access(&store, &[OWNER]),
        paged: Arc::new(Paged::new()),
        clock: Arc::new(SystemClock),
    };
    let add = Invocation::new("donator", Caller::user(OWNER))
        .with(InvokedOption::sub("add", vec![text("playerid", "Giver")]));
    assert!(run(&command, &add).await.contains("Donator added"));
    let file = std::fs::read_to_string(dir.path().join(DONATOR_ROSTER)).unwrap();
    assert!(
        file.contains("Existing") && file.contains("Giver"),
        "{file}"
    );
    let raw = store.read_raw("donator_suspend").unwrap().unwrap();
    assert!(raw.contains("\"addedBy\""), "{raw}");
}

#[tokio::test]
async fn a_suspension_is_recorded_and_the_rank_pulled() {
    let dir = tempfile::tempdir().unwrap();
    let faction = FactionRegistry::police()
        .all()
        .find(|f| f.has_ranks())
        .cloned()
        .unwrap();
    let rank = faction.order[0].clone();
    std::fs::write(dir.path().join(&faction.rank_files[&rank]), "Officer1\n").unwrap();
    std::fs::write(dir.path().join(&faction.spawn_file), "Officer1\n").unwrap();
    let store = common::commands::store();
    let command = SuspendRankCommand {
        store: Arc::clone(&store),
        rosters: common::commands::rosters(Some(dir.path())),
        access: common::commands::access(&store, &[OWNER]),
        clock: Arc::new(SystemClock),
    };
    let reply = run(
        &command,
        &Invocation::new("suspendrank", Caller::user(OWNER))
            .with(text("playerid", "Officer1"))
            .with(text("time", "2h")),
    )
    .await;
    assert!(reply.contains("Rank suspended"), "{reply}");
    let suspensions: IgnoreCaseMap<RankSuspension> = store
        .read("rank_suspensions", IgnoreCaseMap::new())
        .unwrap();
    assert_eq!(suspensions.get("Officer1").unwrap().restore_to, rank);
    let file = std::fs::read_to_string(dir.path().join(&faction.rank_files[&rank])).unwrap();
    assert!(!file.contains("Officer1"), "{file}");
}

#[tokio::test]
async fn a_bad_duration_or_unknown_member_changes_nothing() {
    let dir = tempfile::tempdir().unwrap();
    let store = common::commands::store();
    let command = SuspendRankCommand {
        store: Arc::clone(&store),
        rosters: common::commands::rosters(Some(dir.path())),
        access: common::commands::access(&store, &[OWNER]),
        clock: Arc::new(SystemClock),
    };
    let bad = Invocation::new("suspendrank", Caller::user(OWNER))
        .with(text("playerid", "X"))
        .with(text("time", "soon"));
    assert!(
        run(&command, &bad)
            .await
            .contains("That duration makes no sense")
    );
    let unknown = Invocation::new("suspendrank", Caller::user(OWNER))
        .with(text("playerid", "Nobody"))
        .with(text("time", "1h"));
    assert!(run(&command, &unknown).await.contains("Not whitelisted"));
    assert!(store.read_raw("rank_suspensions").unwrap().is_none());
}

#[test]
fn the_mapping_shows_what_is_in_force_env_included() {
    let with_env = features(&[("MENU_ROLE_STAFF", "222")]);
    let embed = mapping(
        MenuRoleMap {
            high_staff: Some(111),
            ..MenuRoleMap::default()
        },
        &with_env,
        false,
    );
    assert_eq!(embed.field_named("High staff").unwrap().value, "<@&111>");
    assert_eq!(
        embed.field_named("Staff").unwrap().value,
        "<@&222> *(from .env)*"
    );
    assert_eq!(embed.field_named("Blacklist").unwrap().value, "*not set*");
    let nobody = mapping(MenuRoleMap::default(), &features(&[]), false);
    assert!(
        nobody
            .fields
            .iter()
            .any(|f| f.name.ends_with("Nobody qualifies"))
    );
}
