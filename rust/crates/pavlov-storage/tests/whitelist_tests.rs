//! Port of `WhitelistTests.cs`: one name per line, edited without losing what an operator wrote,
//! and the install discovery that finds the files (through the real disk probe).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::fs;
use std::path::Path;
use std::sync::Arc;

use futures_util::future::join_all;
use pavlov_core::servers::installs::{discover, whitelist_path};
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::installs::DiskProbe;
use pavlov_storage::whitelist::{WhitelistFile, entries, entry};

struct Fixture {
    root: tempfile::TempDir,
    _backups: tempfile::TempDir,
    whitelist: WhitelistFile,
}

impl Fixture {
    fn new() -> Self {
        let root = tempfile::tempdir().unwrap();
        let backups = tempfile::tempdir().unwrap();
        let whitelist = WhitelistFile::new(Arc::new(GameFileGuard::none()), backups.path());
        Self {
            root,
            _backups: backups,
            whitelist,
        }
    }

    fn path(&self) -> String {
        self.root
            .path()
            .join("whitelist.txt")
            .to_string_lossy()
            .into_owned()
    }

    fn seed(&self, text: &str) {
        fs::write(self.path(), text).unwrap();
    }

    fn contents(&self) -> String {
        fs::read_to_string(self.path()).unwrap()
    }
}

/// An empty box: no systemd units, so discovery goes straight to the sibling scan.
fn probe() -> DiskProbe {
    DiskProbe::new("/nonexistent/systemd")
}

// ---- parsing ----

#[test]
fn comments_and_blanks_are_not_entries() {
    let found = entries(&[
        "# testers",
        "",
        "  ",
        "76561198000000001",
        "// old",
        "  0002abc  ",
    ]);
    assert_eq!(found, ["76561198000000001", "0002abc"]);
}

// ---- writing ----

#[tokio::test(flavor = "multi_thread")]
async fn adding_appends_and_leaves_everything_else_exactly_as_it_was() {
    let f = Fixture::new();
    f.seed("# testers\nalice\n\n// retired\nbob\n");

    let result = f.whitelist.add(&f.path(), "carol").await;

    assert!(result.ok);
    assert!(result.changed);
    assert_eq!(f.contents(), "# testers\nalice\n\n// retired\nbob\ncarol\n");
}

#[tokio::test(flavor = "multi_thread")]
async fn removing_drops_only_that_line() {
    let f = Fixture::new();
    f.seed("# testers\nalice\nbob\ncarol\n");

    let result = f.whitelist.remove(&f.path(), "bob").await;

    assert!(result.changed);
    assert_eq!(f.contents(), "# testers\nalice\ncarol\n");
}

#[tokio::test(flavor = "multi_thread")]
async fn an_entry_already_there_is_reported_rather_than_rewritten() {
    let f = Fixture::new();
    f.seed("alice\n");
    let written = fs::metadata(f.path()).unwrap().modified().unwrap();

    let result = f.whitelist.add(&f.path(), "ALICE").await;

    assert!(result.ok);
    assert!(!result.changed);
    assert_eq!(fs::metadata(f.path()).unwrap().modified().unwrap(), written);
}

#[tokio::test(flavor = "multi_thread")]
async fn removing_somebody_who_is_not_there_changes_nothing() {
    let f = Fixture::new();
    f.seed("alice\n");

    let result = f.whitelist.remove(&f.path(), "nobody").await;

    assert!(result.ok);
    assert!(!result.changed);
    assert_eq!(f.contents(), "alice\n");
}

#[tokio::test(flavor = "multi_thread")]
async fn the_bot_never_creates_a_directory_inside_an_install() {
    let f = Fixture::new();
    let missing = f.root.path().join("no-such-install").join("whitelist.txt");

    let result = f.whitelist.add(&missing.to_string_lossy(), "alice").await;

    assert!(!result.ok);
    assert!(!missing.parent().unwrap().exists());
    assert!(result.error.unwrap().contains("does not exist"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_write_that_would_gut_the_whitelist_is_refused() {
    // Every entry is the same name, so one "remove" would take all forty out at once.
    let f = Fixture::new();
    f.seed(&format!("{}\n", vec!["alice"; 40].join("\n")));

    let result = f.whitelist.remove(&f.path(), "alice").await;

    assert!(!result.ok);
    assert_eq!(f.whitelist.read(&f.path()).len(), 40);
}

#[tokio::test(flavor = "multi_thread")]
async fn no_temporary_file_is_left_beside_the_whitelist() {
    let f = Fixture::new();
    f.seed("alice\n");

    f.whitelist.add(&f.path(), "bob").await;

    let files: Vec<String> = fs::read_dir(f.root.path())
        .unwrap()
        .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
        .collect();
    assert_eq!(files, ["whitelist.txt"]);
}

#[tokio::test(flavor = "multi_thread")]
async fn concurrent_adds_to_one_file_do_not_lose_entries() {
    let f = Fixture::new();
    f.seed("alice\n");
    let path = f.path();
    let names: Vec<String> = (0..8).map(|i| format!("player{i}")).collect();

    join_all(names.iter().map(|n| f.whitelist.add(&path, n))).await;

    let found = f.whitelist.read(&path);
    assert_eq!(found.len(), 9);
    assert!(found.iter().any(|e| e == "alice"));
}

// ---- reading: unaffected ----

#[test]
fn reading_still_works() {
    let f = Fixture::new();
    f.seed("# testers\nalice\nBart.simpson.2010kk\n\n// old\n");

    assert_eq!(
        f.whitelist.read(&f.path()),
        ["alice", "Bart.simpson.2010kk"]
    );
}

#[test]
fn reading_a_file_that_is_not_there_is_empty_not_an_error() {
    let f = Fixture::new();
    let missing = f.root.path().join("nope").join("whitelist.txt");
    assert!(f.whitelist.read(&missing.to_string_lossy()).is_empty());
}

// ---- install discovery ----

#[test]
fn explicit_bases_win_outright() {
    let found = discover(Some("/srv/one, /srv/two"), None, &probe());
    assert_eq!(found.roots, ["/srv/one", "/srv/two"]);
}

#[test]
fn sibling_installs_are_found() {
    // A directory without a Pavlov folder is a backup or an unpacked archive, not an install.
    let root = tempfile::tempdir().unwrap();
    for name in ["pavlovserver", "pavlovserver1", "pavlovserver2"] {
        fs::create_dir_all(root.path().join(name).join("Pavlov")).unwrap();
    }
    fs::create_dir_all(root.path().join("pavlovserver.old")).unwrap(); // no Pavlov dir

    let base = root.path().join("pavlovserver");
    let found = discover(None, Some(&base.to_string_lossy()), &probe());

    assert_eq!(found.roots.len(), 3);
    assert!(
        found
            .roots
            .iter()
            .all(|f| Path::new(f).join("Pavlov").is_dir())
    );
}

#[test]
fn nothing_found_still_yields_the_configured_base() {
    let root = tempfile::tempdir().unwrap();
    let base = root.path().join("does-not-exist");
    let found = discover(None, Some(&base.to_string_lossy()), &probe());

    assert_eq!(found.roots.len(), 1);
}

#[test]
fn the_whitelist_path_is_the_one_operators_edit() {
    assert_eq!(
        whitelist_path("/home/steam/pavlovserver"),
        "/home/steam/pavlovserver/Pavlov/Saved/Config/whitelist.txt"
    );
}

// ---- what actually gets written ----

#[test]
fn a_username_is_written_verbatim() {
    assert_eq!(entry(Some("A Player Name")), "A Player Name");
    assert_eq!(entry(Some("Bart.simpson.2010kk")), "Bart.simpson.2010kk");
    assert_eq!(entry(Some("[TAG] Someone!")), "[TAG] Someone!");
    assert_eq!(entry(Some("Ünïcødé")), "Ünïcødé");
}

#[test]
fn surrounding_whitespace_is_trimmed() {
    assert_eq!(entry(Some("  Pkdestroy \t")), "Pkdestroy");
}

#[test]
fn a_name_cannot_forge_a_second_entry() {
    let forged = entry(Some("Innocent\nSomebodyElse"));

    assert!(!forged.contains(['\n', '\r']));
    assert_eq!(forged, "Innocent SomebodyElse");
}

#[test]
fn an_empty_or_control_only_name_yields_nothing() {
    assert_eq!(entry(None), "");
    assert_eq!(entry(Some("   ")), "");
    assert_eq!(entry(Some("\r\n\t")), "");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_name_with_a_space_survives_the_round_trip() {
    let f = Fixture::new();
    f.seed("# testers\n");

    f.whitelist
        .add(&f.path(), &entry(Some("A Player Name")))
        .await;

    assert_eq!(f.whitelist.read(&f.path()), ["A Player Name"]);
}
