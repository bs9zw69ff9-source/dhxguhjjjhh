//! Port of `ServerBanFileTests.cs`: the game's ban-list file, the message a banned player
//! sees, and the only way bans from the in-game admin menu reach the bot.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::fs;
use std::sync::Arc;

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use common::file_store;
use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::BanRecord;
use pavlov_core::time::ManualClock;
use pavlov_host::moderation::ban_file::parse;
use pavlov_host::moderation::{BanFileStatus, ResolveName, ServerBanFile};
use pavlov_storage::datasets::TEMP_BANS;
use pavlov_storage::game_files::GameFileGuard;

/// FIXED: time_left truncates, so the real clock turns "3d 4h" into "3d 3h" now and then.
fn now() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 7, 15, 12, 0, 0).unwrap()
}

struct Fixture {
    dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    path: String,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = file_store(dir.path());
        let path = dir
            .path()
            .join("banlist.txt")
            .to_string_lossy()
            .into_owned();
        Self { dir, store, path }
    }

    fn file(&self, paths: Vec<String>, resolve: Option<ResolveName>) -> ServerBanFile {
        ServerBanFile::new(
            paths,
            Arc::clone(&self.store),
            Arc::new(ManualClock::new(now())),
            resolve,
            Arc::new(GameFileGuard::none()),
        )
    }

    fn modsave(&self) -> ServerBanFile {
        self.file(vec![self.path.clone()], None)
    }

    async fn seed(&self, bans: Vec<BanRecord>) {
        self.store.write(TEMP_BANS, &bans).await.unwrap();
    }

    fn bans(&self) -> Vec<BanRecord> {
        self.store.read(TEMP_BANS, Vec::new()).unwrap()
    }

    fn other(&self, name: &str) -> String {
        self.dir.path().join(name).to_string_lossy().into_owned()
    }
}

fn ban(player: &str, reason: &str) -> BanRecord {
    BanRecord {
        reason: Some(reason.into()),
        ..BanRecord::for_player(player)
    }
}

fn permanent(player: &str, reason: &str) -> BanRecord {
    BanRecord {
        permanent: true,
        ..ban(player, reason)
    }
}

#[test]
fn blocks_are_parsed_into_name_reason_and_unban() {
    let entries = parse(Some(
        "Alice\nReason: Cheating\nUnban: 3d 4h\n\nBob\nReason: Griefing\nUnban: Permanent\n",
    ));
    assert_eq!(entries.len(), 2);
    assert_eq!(entries[0].name, "Alice");
    assert_eq!(entries[0].reason, "Cheating");
    assert_eq!(entries[0].unban, "3d 4h");
    assert_eq!(entries[1].unban, "Permanent");
}

#[test]
fn a_block_whose_first_line_is_a_field_is_skipped() {
    let entries = parse(Some(
        "Reason: orphaned\nUnban: Permanent\n\nAlice\nReason: Cheating\nUnban: Permanent\n",
    ));
    assert_eq!(entries.len(), 1);
    assert_eq!(entries[0].name, "Alice");
}

#[test]
fn a_reason_containing_a_colon_survives_intact() {
    let entries = parse(Some("Alice\nReason: Cheating: aimbot\nUnban: Permanent\n"));
    assert_eq!(entries[0].reason, "Cheating: aimbot");
}

#[tokio::test]
async fn export_writes_the_time_left_as_a_span_not_a_date() {
    let f = Fixture::new();
    f.seed(vec![
        BanRecord {
            expires: Some(now() + TimeDelta::days(3) + TimeDelta::hours(4)),
            ..ban("Alice", "Cheating")
        },
        permanent("Bob", "Griefing"),
    ])
    .await;

    assert_eq!(f.modsave().export().await.unwrap(), 2);

    let written = fs::read_to_string(&f.path).unwrap();
    assert!(written.contains("Unban: 3d 4h"), "{written}");
    assert!(written.contains("Unban: Permanent"), "{written}");
    assert!(!written.contains("2026-"), "{written}");
}

#[tokio::test]
async fn expired_bans_are_not_exported() {
    let f = Fixture::new();
    f.seed(vec![BanRecord {
        expires: Some(now() - TimeDelta::hours(1)),
        ..ban("Served", "x")
    }])
    .await;
    assert_eq!(f.modsave().export().await.unwrap(), 0);
}

#[tokio::test]
async fn a_newline_in_a_reason_is_flattened() {
    let f = Fixture::new();
    f.seed(vec![permanent("Alice", "line one\nUnban: Permanent")])
        .await;

    f.modsave().export().await.unwrap();
    let entries = parse(Some(&fs::read_to_string(&f.path).unwrap()));

    assert_eq!(entries.len(), 1);
    assert_eq!(entries[0].reason, "line one Unban: Permanent");
}

#[tokio::test]
async fn import_picks_up_in_game_bans_the_bot_never_issued() {
    let f = Fixture::new();
    fs::write(&f.path, "Alice\nReason: Cheating\nUnban: 3d\n\n").unwrap();

    assert_eq!(f.modsave().import().await.unwrap(), 1);

    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert_eq!(bans[0].player_id, "Alice");
    assert_eq!(bans[0].moderator.as_deref(), Some("in-game"));
    assert!(!bans[0].permanent);
    assert_eq!(bans[0].expires, Some(now() + TimeDelta::days(3)));
}

#[tokio::test]
async fn import_is_idempotent() {
    let f = Fixture::new();
    fs::write(&f.path, "Alice\nReason: Cheating\nUnban: 3d\n\n").unwrap();
    let modsave = f.modsave();

    assert_eq!(modsave.import().await.unwrap(), 1);
    assert_eq!(modsave.import().await.unwrap(), 0);
    assert_eq!(f.bans().len(), 1);
}

#[tokio::test]
async fn an_unreadable_unban_value_becomes_permanent_with_the_value_recorded() {
    let f = Fixture::new();
    fs::write(&f.path, "Alice\nReason: Cheating\nUnban: next tuesday\n\n").unwrap();
    f.modsave().import().await.unwrap();

    let ban = &f.bans()[0];
    assert!(ban.permanent);
    assert!(
        ban.reason
            .as_deref()
            .unwrap()
            .contains("unreadable unban value: next tuesday")
    );
}

#[tokio::test]
async fn import_never_removesa_ban() {
    // The store is the source of truth; a truncated file must not lift every ban.
    let f = Fixture::new();
    f.seed(vec![permanent("Existing", "x")]).await;
    fs::write(&f.path, "").unwrap();

    f.modsave().import().await.unwrap();

    assert_eq!(f.bans().len(), 1);
}

#[tokio::test]
async fn sync_imports_before_exporting() {
    // Exporting first destroys an admin-menu ban before it is read.
    let f = Fixture::new();
    fs::write(
        &f.path,
        "FromTheMenu\nReason: Cheating\nUnban: Permanent\n\n",
    )
    .unwrap();

    f.modsave().sync().await.unwrap();

    assert!(f.bans().iter().any(|b| b.player_id == "FromTheMenu"));
    assert!(fs::read_to_string(&f.path).unwrap().contains("FromTheMenu"));
}

#[tokio::test]
async fn an_unconfigured_path_is_a_no_op() {
    let f = Fixture::new();
    let disabled = f.file(vec![String::new()], None);
    assert!(!disabled.enabled());
    assert_eq!(disabled.export().await.unwrap(), 0);
    assert_eq!(disabled.import().await.unwrap(), 0);
}

#[tokio::test]
async fn the_file_is_searched_by_name_so_checkban_can_answer_for_the_server() {
    let f = Fixture::new();
    fs::write(&f.path, "Alice\nReason: Cheating\nUnban: Permanent\n\n").unwrap();

    let found = f.modsave().find("Alice").await;

    assert!(found.listed());
    assert!(found.conclusive());
    assert_eq!(found.entry.unwrap().reason, "Cheating");
    assert_eq!(found.path.as_deref(), Some(f.path.as_str()));
}

#[tokio::test]
async fn the_file_is_searched_by_the_resolved_name_too() {
    let f = Fixture::new();
    fs::write(
        &f.path,
        "76561198000000042\nReason: Cheating\nUnban: Permanent\n\n",
    )
    .unwrap();
    let resolve: ResolveName =
        Arc::new(|id| (id == "76561198000000042").then(|| "Alice".to_owned()));
    let by_id = f.file(vec![f.path.clone()], Some(resolve));

    assert!(by_id.find("Alice").await.listed());
    assert!(by_id.find("76561198000000042").await.listed());
}

#[tokio::test]
async fn not_listed_and_could_not_read_are_different_answers() {
    let f = Fixture::new();
    fs::write(&f.path, "Bob\nReason: Griefing\nUnban: Permanent\n\n").unwrap();
    let absent = f.modsave().find("Alice").await;
    assert!(!absent.listed());
    assert!(absent.conclusive());

    fs::remove_file(&f.path).unwrap();
    let missing = f.modsave().find("Alice").await;
    assert!(!missing.listed());
    assert!(!missing.conclusive());
    assert_eq!(missing.status, BanFileStatus::Missing);

    let off = f.file(Vec::new(), None).find("Alice").await;
    assert_eq!(off.status, BanFileStatus::Disabled);
}

#[tokio::test]
async fn exporting_with_no_stored_bans_removes_a_file_only_entry() {
    let f = Fixture::new();
    fs::write(&f.path, "Alice\nReason: Cheating\nUnban: Permanent\n\n").unwrap();

    assert_eq!(f.modsave().export().await.unwrap(), 0);
    assert!(!fs::read_to_string(&f.path).unwrap().contains("Alice"));
}

// ---- multiple servers, each with its own blacklist.txt ----

fn multi(f: &Fixture) -> (ServerBanFile, [String; 3]) {
    let files = [f.other("s1.txt"), f.other("s2.txt"), f.other("s3.txt")];
    (f.file(files.to_vec(), None), files)
}

#[tokio::test]
async fn export_writes_every_servers_file() {
    let f = Fixture::new();
    let (sync, files) = multi(&f);
    f.seed(vec![permanent("Alice", "Cheating")]).await;

    assert_eq!(sync.export().await.unwrap(), 1);
    for file in &files {
        assert!(
            fs::read_to_string(file).unwrap().contains("Alice"),
            "{file}"
        );
    }
}

#[tokio::test]
async fn import_reads_bans_from_every_servers_file() {
    let f = Fixture::new();
    let (sync, [a, b, c]) = multi(&f);
    fs::write(&a, "Alice\nReason: aim\nUnban: Permanent\n\n").unwrap();
    fs::write(&b, "Bob\nReason: grief\nUnban: Permanent\n\n").unwrap();
    fs::write(&c, "Alice\nReason: aim\nUnban: Permanent\n\n").unwrap(); // dup of A

    assert_eq!(sync.import().await.unwrap(), 2); // Alice + Bob, not 3

    let stored: Vec<String> = f.bans().into_iter().map(|b| b.player_id).collect();
    assert!(stored.contains(&"Alice".to_owned()));
    assert!(stored.contains(&"Bob".to_owned()));
    assert_eq!(stored.len(), 2);
}

#[tokio::test]
async fn find_locates_a_player_listed_on_any_server() {
    let f = Fixture::new();
    let (sync, [_, b, _]) = multi(&f);
    fs::write(&b, "Bob\nReason: grief\nUnban: Permanent\n\n").unwrap();

    let found = sync.find("Bob").await;

    assert!(found.listed());
    assert_eq!(found.status, BanFileStatus::Read);
    assert_eq!(found.path.as_deref(), Some(b.as_str()));
}

#[tokio::test]
async fn sync_preserves_an_in_game_ban_that_only_exists_on_server2() {
    let f = Fixture::new();
    let (sync, files) = multi(&f);
    f.seed(vec![permanent("Alice", "known")]).await;
    fs::write(&files[1], "Bob\nReason: in-game only\nUnban: Permanent\n\n").unwrap();

    sync.sync().await.unwrap();

    assert!(f.bans().iter().any(|b| b.player_id == "Bob"));
    for file in &files {
        let text = fs::read_to_string(file).unwrap();
        assert!(text.contains("Alice"), "{file}");
        assert!(text.contains("Bob"), "{file}: NOT erased from server 2");
    }
}

#[tokio::test]
async fn an_unreadable_store_writes_no_file_at_all() {
    // Rust-only: export from a store that cannot be read must not empty every server's list.
    let f = Fixture::new();
    fs::write(&f.path, "Alice\nReason: Cheating\nUnban: Permanent\n\n").unwrap();
    fs::write(f.dir.path().join("tempbans.json"), "{not json").unwrap();

    let refused = f.modsave().export().await.unwrap_err();

    assert!(refused.contains("cannot be read"), "{refused}");
    // The C# read the unreadable list as empty and wrote that to every file, erasing the
    // in-game bans that exist nowhere else.
    assert!(fs::read_to_string(&f.path).unwrap().contains("Alice"));
}
