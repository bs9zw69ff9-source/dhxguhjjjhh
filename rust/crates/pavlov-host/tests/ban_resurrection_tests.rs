//! Port of `BanResurrectionTests.cs`: a lifted ban must not come back on its own. The
//! importer treated any name in the game's file that the store did not know as a ban to
//! create; the tombstone carries "this was lifted deliberately" across the two subsystems.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::fs;
use std::sync::Arc;

use chrono::{DateTime, TimeDelta, Utc};
use common::file_store;
use pavlov_core::data::SerializedStore;
use pavlov_core::json::Stamp;
use pavlov_core::moderation::BanRecord;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::SystemClock;
use pavlov_host::moderation::ban_file::TOMBSTONE_LIFE;
use pavlov_host::moderation::{ResolveName, ServerBanFile};
use pavlov_storage::datasets::{TEMP_BANS, UNBAN_TOMBSTONES};
use pavlov_storage::game_files::GameFileGuard;

const NAME: &str = "Pardoned";
const ACCOUNT_ID: &str = "0002abcdef0123456789abcdef012345";

struct Fixture {
    _dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    path: String,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = file_store(dir.path());
        let path = dir
            .path()
            .join("blacklist.txt")
            .to_string_lossy()
            .into_owned();
        Self {
            _dir: dir,
            store,
            path,
        }
    }

    fn file(&self, resolve: Option<ResolveName>) -> ServerBanFile {
        ServerBanFile::new(
            vec![self.path.clone()],
            Arc::clone(&self.store),
            Arc::new(SystemClock),
            resolve,
            Arc::new(GameFileGuard::none()),
        )
    }

    fn modsave(&self) -> ServerBanFile {
        self.file(None)
    }

    /// An importer that knows this id belongs to NAME.
    fn resolving(&self) -> ServerBanFile {
        self.file(Some(Arc::new(|id| {
            (id == ACCOUNT_ID).then(|| NAME.to_owned())
        })))
    }

    fn write_ban_file(&self, name: &str) {
        fs::write(
            &self.path,
            format!("{name}\nReason: griefing\nUnban: Permanent\n\n"),
        )
        .unwrap();
    }

    async fn tombstone(&self, name: &str, at: DateTime<Utc>) {
        self.store
            .update(
                UNBAN_TOMBSTONES,
                IgnoreCaseMap::default,
                |mut t: IgnoreCaseMap<Stamp>| {
                    t.insert(name, Stamp(at));
                    Some(t)
                },
            )
            .await
            .unwrap();
    }

    fn bans(&self) -> Vec<BanRecord> {
        self.store.read(TEMP_BANS, Vec::new()).unwrap()
    }
}

#[tokio::test]
async fn a_deliberately_lifted_ban_is_not_resurrected_by_the_importer() {
    let f = Fixture::new();
    f.write_ban_file(NAME);
    f.tombstone(NAME, Utc::now()).await;

    f.modsave().import().await.unwrap();

    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn an_untombstoned_file_entry_is_still_imported() {
    // The control: an importer that had simply stopped working would pass the test above.
    let f = Fixture::new();
    f.write_ban_file(NAME);

    f.modsave().import().await.unwrap();

    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert_eq!(bans[0].player_id, NAME);
}

#[tokio::test]
async fn an_expired_tombstone_no_longer_blocks_the_import() {
    let f = Fixture::new();
    f.write_ban_file(NAME);
    f.tombstone(NAME, Utc::now() - TOMBSTONE_LIFE - TimeDelta::days(1))
        .await;

    f.modsave().import().await.unwrap();

    assert_eq!(f.bans().len(), 1);
}

#[tokio::test]
async fn a_tombstone_only_covers_the_name_it_names() {
    let f = Fixture::new();
    fs::write(
        &f.path,
        format!("{NAME}\nReason: griefing\nUnban: Permanent\n\nSomebodyElse\nReason: cheating\nUnban: Permanent\n\n"),
    )
    .unwrap();
    f.tombstone(NAME, Utc::now()).await;

    f.modsave().import().await.unwrap();

    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert_eq!(bans[0].player_id, "SomebodyElse");
}

#[tokio::test]
async fn exporting_removes_a_lifted_player_from_the_file() {
    let f = Fixture::new();
    f.write_ban_file(NAME);
    f.tombstone(NAME, Utc::now()).await;

    f.modsave().sync().await.unwrap();

    assert!(!fs::read_to_string(&f.path).unwrap().contains(NAME));
    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn an_in_game_ban_is_not_re_imported_on_every_sync() {
    let f = Fixture::new();
    f.write_ban_file(ACCOUNT_ID);
    let modsave = f.resolving();

    for _ in 0..3 {
        modsave.import().await.unwrap();
    }

    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert_eq!(bans[0].player_id, NAME);
}

#[tokio::test]
async fn a_tombstone_under_the_name_blocks_the_file_entry_under_the_id() {
    let f = Fixture::new();
    f.write_ban_file(ACCOUNT_ID);
    f.tombstone(NAME, Utc::now()).await;

    f.resolving().import().await.unwrap();

    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn a_tombstone_under_the_id_blocks_it_as_well() {
    let f = Fixture::new();
    f.write_ban_file(ACCOUNT_ID);
    f.tombstone(ACCOUNT_ID, Utc::now()).await;

    f.resolving().import().await.unwrap();

    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn the_resolved_record_keeps_the_account_id() {
    let f = Fixture::new();
    f.write_ban_file(ACCOUNT_ID);

    f.resolving().import().await.unwrap();

    assert_eq!(f.bans()[0].unique_id.as_deref(), Some(ACCOUNT_ID));
}

#[tokio::test]
async fn an_unresolvable_id_is_still_imported_under_the_id() {
    let f = Fixture::new();
    f.write_ban_file("0002ffffffffffffffffffffffffffff");

    f.resolving().import().await.unwrap();

    assert_eq!(f.bans()[0].player_id, "0002ffffffffffffffffffffffffffff");
}

#[tokio::test]
async fn a_ban_still_in_force_survives_a_sync() {
    let f = Fixture::new();
    f.store
        .write(
            TEMP_BANS,
            &vec![BanRecord {
                reason: Some("cheating".into()),
                moderator: Some("mod".into()),
                at: Some(Utc::now()),
                permanent: true,
                duration_label: Some("Permanent".into()),
                ..BanRecord::for_player("StillBanned")
            }],
        )
        .await
        .unwrap();

    f.modsave().sync().await.unwrap();

    assert_eq!(f.bans().len(), 1);
    assert!(fs::read_to_string(&f.path).unwrap().contains("StillBanned"));
}
