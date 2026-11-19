//! Port of `ServerBanFileTests.cs`: reading the game's `blacklist.txt`. The bot no longer
//! writes or imports it (owner, 2026-11-19: bans live in the player's save), so the export,
//! import and sync tests are dropped with that behaviour; parsing and the lookup `/checkban`
//! and `/unban` use are kept.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::fs;
use std::sync::Arc;

use pavlov_host::moderation::ban_file::parse;
use pavlov_host::moderation::{BanFileStatus, ResolveName, ServerBanFile};

struct Fixture {
    dir: tempfile::TempDir,
    path: String,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let path = dir
            .path()
            .join("banlist.txt")
            .to_string_lossy()
            .into_owned();
        Self { dir, path }
    }

    fn file(&self, paths: Vec<String>, resolve: Option<ResolveName>) -> ServerBanFile {
        ServerBanFile::new(paths, resolve)
    }

    fn modsave(&self) -> ServerBanFile {
        self.file(vec![self.path.clone()], None)
    }

    fn other(&self, name: &str) -> String {
        self.dir.path().join(name).to_string_lossy().into_owned()
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
async fn an_unconfigured_path_is_a_no_op() {
    let f = Fixture::new();
    let disabled = f.file(vec![String::new()], None);
    assert!(!disabled.enabled());
    assert_eq!(disabled.find("Alice").await.status, BanFileStatus::Disabled);
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
async fn find_locates_a_player_listed_on_any_server() {
    let f = Fixture::new();
    let (a, b, c) = (f.other("a.txt"), f.other("b.txt"), f.other("c.txt"));
    fs::write(&a, "Alice\nReason: grief\nUnban: Permanent\n\n").unwrap();
    fs::write(&b, "Bob\nReason: grief\nUnban: Permanent\n\n").unwrap();
    let every = f.file(vec![a, b.clone(), c], None);

    let found = every.find("Bob").await;

    assert!(found.listed());
    assert_eq!(found.status, BanFileStatus::Read);
    assert_eq!(found.path.as_deref(), Some(b.as_str()));
}
