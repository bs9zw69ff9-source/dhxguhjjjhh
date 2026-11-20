//! `/tempban`, `/permban`, `/unban` and `/checkban` end to end: the record, the player's save
//! (where a ban is enforced since 2026-11-19), the kick on the wire, the hierarchy, and the
//! server's own `blacklist.txt`, which the bot reads but never edits.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::{BanRecord, StaffTier};
use pavlov_core::security::OwnerGuard;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::bans::{BanParts, CheckBanCommand, TempBanCommand, UnbanCommand};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::bans::BanService;
use pavlov_host::moderation::master_names::StoredMasterNames;
use pavlov_host::moderation::{AuditLog, ServerBanFile};
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::ledger_store::LedgerFileStore;

const OWNER: u64 = 3;

struct Rig {
    _dir: tempfile::TempDir,
    fake: FakeRconServer,
    store: Arc<SerializedStore>,
    parts: Arc<BanParts>,
    ban_path: std::path::PathBuf,
    modsave: std::path::PathBuf,
}

impl Rig {
    /// A save as the game creates it on a player's first join.
    fn joined(&self, player: &str) {
        std::fs::write(
            self.modsave.join(format!("{player}.json")),
            format!("{{\n\t\"name\": \"{player}\",\n\t\"money\": 100,\n\t\"ban\": {{ \"banned\": false, \"reason\": \"\", \"unban_date\": \"\" }}\n}}\n"),
        )
        .unwrap();
    }

    fn banned_in_save(&self, player: &str) -> bool {
        let text = std::fs::read_to_string(self.modsave.join(format!("{player}.json"))).unwrap();
        let save: serde_json::Value = serde_json::from_str(&text).unwrap();
        save["ban"]["banned"].as_bool().unwrap()
    }
}

async fn rig() -> Rig {
    let dir = tempfile::tempdir().unwrap();
    let ban_path = dir.path().join("blacklist.txt");
    let modsave = dir.path().join("ModSave");
    std::fs::create_dir_all(&modsave).unwrap();
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
    // Wired as the composition root wires it: bans go into the saves, the file is only read.
    let ban_file = Arc::new(ServerBanFile::new(
        vec![ban_path.to_string_lossy().into_owned()],
        None,
    ));
    let saves = Arc::new(LedgerFileStore::new(
        Some(modsave.clone()),
        Arc::new(GameFileGuard::none()),
    ));
    let bans = BanService::new(
        Arc::clone(&rcon),
        Arc::clone(&store),
        masters.clone(),
        clock.clone(),
    )
    .with_saves(saves);
    let parts = Arc::new(BanParts {
        bans: Arc::new(bans),
        tracking: Arc::new(IpTrackingService::new(
            Arc::clone(&store),
            metrics,
            clock.clone(),
        )),
        store: Arc::clone(&store),
        access: common::commands::access(&store, &[OWNER]),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), clock.clone())),
        masters,
        ban_file,
        clock,
    });
    Rig {
        _dir: dir,
        fake,
        store,
        parts,
        ban_path,
        modsave,
    }
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

#[tokio::test(flavor = "multi_thread")]
async fn tempban_records_then_enforces_with_a_length() {
    let rig = rig().await;
    rig.joined("Griefer");
    let command = TempBanCommand {
        parts: Arc::clone(&rig.parts),
    };
    let invocation = Invocation::new("tempban", Caller::user(OWNER))
        .with(text("playerid", "Griefer"))
        .with(text("reason", "rdm"))
        .with(text("duration", "1d"));
    let reply = run(&command, &invocation).await;
    assert!(reply.contains("Griefer"), "{reply}");
    let bans: Vec<BanRecord> = rig.store.read("tempbans", Vec::new()).unwrap();
    assert_eq!(bans.len(), 1);
    assert!(!bans[0].permanent && bans[0].expires.is_some());
    assert_eq!(bans[0].tier, Some(StaffTier::Owner));
    assert!(rig.banned_in_save("Griefer"));
    let commands = rig.fake.commands();
    assert!(commands.iter().any(|c| c == "Kick Griefer"), "{commands:?}");
    assert!(
        !commands.iter().any(|c| c.starts_with("Ban ")),
        "no RCON ban any more: {commands:?}"
    );
    assert!(!reply.contains("No save yet"), "{reply}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_ban_on_a_player_with_no_save_says_it_waits_and_kicks() {
    let rig = rig().await;
    let command = TempBanCommand {
        parts: Arc::clone(&rig.parts),
    };
    let invocation = Invocation::new("tempban", Caller::user(OWNER))
        .with(text("playerid", "NeverJoined"))
        .with(text("reason", "alt"))
        .with(text("duration", "1d"));

    let reply = run(&command, &invocation).await;

    assert!(reply.contains("No save yet"), "{reply}");
    assert!(rig.fake.commands().iter().any(|c| c == "Kick NeverJoined"));
}

#[tokio::test(flavor = "multi_thread")]
async fn unban_clears_a_ban_held_only_in_the_save() {
    let rig = rig().await;
    rig.joined("Banned");
    let saves = LedgerFileStore::new(Some(rig.modsave.clone()), Arc::new(GameFileGuard::none()));
    let in_game = pavlov_core::sync::SaveBan {
        reason: "set in game".into(),
        unban_date: String::new(),
    };
    assert!(saves.set_ban("Banned", Some(&in_game)).holds());

    let reply = run(
        &UnbanCommand {
            parts: Arc::clone(&rig.parts),
        },
        &Invocation::new("unban", Caller::user(OWNER)).with(text("playerid", "Banned")),
    )
    .await;

    assert!(reply.contains("Ban cleared from their save"), "{reply}");
    assert!(!rig.banned_in_save("Banned"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_date_is_not_a_duration() {
    let rig = rig().await;
    let command = TempBanCommand {
        parts: Arc::clone(&rig.parts),
    };
    let invocation = Invocation::new("tempban", Caller::user(OWNER))
        .with(text("playerid", "Griefer"))
        .with(text("reason", "rdm"))
        .with(text("duration", "2026-08-01"));
    assert!(
        run(&command, &invocation)
            .await
            .contains("Invalid duration")
    );
    assert!(rig.fake.commands().is_empty());
}

#[tokio::test(flavor = "multi_thread")]
async fn a_ban_above_your_tier_cannot_be_lifted() {
    let rig = rig().await;
    let owner_ban = BanRecord {
        reason: Some("owner said so".into()),
        moderator: Some("boss".into()),
        permanent: true,
        tier: Some(StaffTier::MasterOwner),
        ..BanRecord::for_player("Victim")
    };
    rig.store.write("tempbans", &vec![owner_ban]).await.unwrap();
    let command = UnbanCommand {
        parts: Arc::clone(&rig.parts),
    };
    let invocation = Invocation::new("unban", Caller::user(OWNER)).with(text("playerid", "Victim"));
    let reply = run(&command, &invocation).await;
    assert!(reply.contains("Above your authority"), "{reply}");
    let bans: Vec<BanRecord> = rig.store.read("tempbans", Vec::new()).unwrap();
    assert_eq!(bans.len(), 1, "still banned");
}

#[tokio::test(flavor = "multi_thread")]
async fn unban_never_edits_blacklist_txt_and_says_to_do_it_by_hand() {
    let rig = rig().await;
    std::fs::write(
        &rig.ban_path,
        "Ghost\nReason: old in-game ban\nUnban: Permanent\n",
    )
    .unwrap();
    let check = CheckBanCommand {
        parts: Arc::clone(&rig.parts),
    };
    let ask = Invocation::new("checkban", Caller::user(OWNER)).with(text("playerid", "Ghost"));
    let before = run(&check, &ask).await;
    assert!(before.contains("Banned by the server"), "{before}");
    assert!(before.contains("by hand"), "{before}");

    let unban = UnbanCommand {
        parts: Arc::clone(&rig.parts),
    };
    let reply = run(
        &unban,
        &Invocation::new("unban", Caller::user(OWNER)).with(text("playerid", "Ghost")),
    )
    .await;
    assert!(reply.contains("Listed in blacklist.txt only"), "{reply}");
    assert!(reply.contains("by hand"), "{reply}");
    assert!(
        std::fs::read_to_string(&rig.ban_path)
            .unwrap()
            .contains("Ghost"),
        "the file is left alone"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn checkban_says_not_banned_plainly_when_the_file_agrees() {
    let rig = rig().await;
    std::fs::write(&rig.ban_path, "").unwrap();
    let check = CheckBanCommand {
        parts: Arc::clone(&rig.parts),
    };
    let reply = run(
        &check,
        &Invocation::new("checkban", Caller::user(1)).with(text("playerid", "Clean")),
    )
    .await;
    assert!(reply.contains("No active ban"), "{reply}");
    assert!(
        !reply.contains("ban file"),
        "terse on the clean case: {reply}"
    );
}
