//! Port of `ModSaveSyncTests.cs`: the sweep across installs and the join-time fast path, on
//! real directories.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use common::{FakeRoster, touch};
use pavlov_core::servers::installs::mod_save_path;
use pavlov_rcon::OnlineRoster;
use pavlov_storage::mod_save_sync::ModSaveSync;

struct Box_ {
    root: tempfile::TempDir,
}

impl Box_ {
    fn new() -> Self {
        Self {
            root: tempfile::tempdir().unwrap(),
        }
    }

    fn install(&self, name: &str) -> String {
        let root = self.root.path().join(name).to_string_lossy().into_owned();
        fs::create_dir_all(mod_save_path(&root)).unwrap();
        root
    }

    /// An install root with Pavlov/Saved/Config but no ModSave yet.
    fn bare_install(&self, name: &str) -> String {
        let root = self.root.path().join(name);
        fs::create_dir_all(root.join("Pavlov/Saved/Config")).unwrap();
        root.to_string_lossy().into_owned()
    }
}

fn ledger_path(install: &str, player: &str) -> PathBuf {
    Path::new(&mod_save_path(install)).join(format!("{player}.txt"))
}

fn write_ledger(install: &str, player: &str, content: &str, minutes: i64) {
    let path = ledger_path(install, player);
    fs::write(&path, content).unwrap();
    touch(&path, minutes);
}

fn read(path: &Path) -> String {
    fs::read_to_string(path).unwrap()
}

fn sync(installs: &[&str], roster: Option<FakeRoster>, enabled: bool) -> ModSaveSync {
    let roster: Arc<dyn OnlineRoster> = Arc::new(roster.unwrap_or(FakeRoster::new(true, &[])));
    ModSaveSync::new(
        installs.iter().map(|s| (*s).to_owned()).collect(),
        Some(roster),
        enabled,
        &[],
        None,
    )
}

#[test]
fn the_newer_balance_lands_in_the_other_install() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Bob", "1250", 0);
    write_ledger(&c, "Bob", "300", -10);

    assert_eq!(sync(&[&a, &c], None, true).sweep(), 1);
    assert_eq!(read(&ledger_path(&c, "Bob")), "1250");
}

#[test]
fn a_second_sweep_converges_and_copies_nothing() {
    // The mtime is carried across, so once propagated the two compare equal.
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Bob", "1250", 0);
    write_ledger(&c, "Bob", "300", -10);
    let s = sync(&[&a, &c], None, true);

    assert_eq!(s.sweep(), 1);
    assert_eq!(s.sweep(), 0);
}

#[test]
fn a_file_only_one_install_has_is_copied_in_and_never_deleted_from_the_source() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Solo", "42", 0);

    let _ = sync(&[&a, &c], None, true).sweep();

    assert!(ledger_path(&a, "Solo").exists());
    assert_eq!(read(&ledger_path(&c, "Solo")), "42");
}

#[test]
fn an_online_players_ledger_is_not_touched_by_the_sweep() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Pkd", "999", 0);
    write_ledger(&c, "Pkd", "500", -10);

    let copied = sync(&[&a, &c], Some(FakeRoster::new(true, &["pkd"])), true).sweep();

    assert_eq!(copied, 0);
    assert_eq!(read(&ledger_path(&c, "Pkd")), "500");
}

#[test]
fn an_empty_or_zero_file_never_wipes_a_positive_balance() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Rich", "0", 0); // fresh zero, newest
    write_ledger(&c, "Rich", "5000", -5);

    assert_eq!(sync(&[&a, &c], None, true).sweep(), 0);
    assert_eq!(read(&ledger_path(&c, "Rich")), "5000");
}

#[test]
fn menu_access_files_are_never_mirrored() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    let rel_a = Path::new(&mod_save_path(&a)).join("RconPlus");
    let rel_c = Path::new(&mod_save_path(&c)).join("RconPlus");
    fs::create_dir_all(&rel_a).unwrap();
    fs::create_dir_all(&rel_c).unwrap();
    fs::write(rel_a.join("MenuAccess.txt"), "granted").unwrap();
    fs::write(rel_c.join("MenuAccess.txt"), "revoked").unwrap();
    touch(&rel_c.join("MenuAccess.txt"), -10);

    let _ = sync(&[&a, &c], None, true).sweep();

    assert_eq!(read(&rel_c.join("MenuAccess.txt")), "revoked");
}

#[test]
fn a_non_ledger_file_is_mirrored_newest_wins() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    let rel_a = Path::new(&mod_save_path(&a)).join("roles.txt");
    let rel_c = Path::new(&mod_save_path(&c)).join("roles.txt");
    fs::write(&rel_a, "Captain=Bob").unwrap();
    fs::write(&rel_c, "old").unwrap();
    touch(&rel_c, -60);

    let _ = sync(&[&a, &c], None, true).sweep();

    assert_eq!(read(&rel_c), "Captain=Bob");
}

#[test]
fn a_single_install_does_nothing() {
    let b = Box_::new();
    let a = b.install("pavlovserver");
    write_ledger(&a, "Bob", "1250", 0);

    assert_eq!(sync(&[&a], None, true).sweep(), 0);
    assert!(!sync(&[&a], None, true).can_sync());
}

#[test]
fn disabled_syncs_nothing_even_with_many_installs() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Bob", "1250", 0);
    write_ledger(&c, "Bob", "1", -10);

    assert_eq!(sync(&[&a, &c], None, false).sweep(), 0);
    assert_eq!(read(&ledger_path(&c, "Bob")), "1");
}

#[test]
fn the_bot_never_creates_a_mod_save_tree_in_a_misdiscovered_install() {
    let b = Box_::new();
    let a = b.install("pavlovserver");
    let c = b.root.path().join("pavlovserver1");
    fs::create_dir_all(&c).unwrap(); // an install root, but NO Pavlov/Saved/Config
    let c = c.to_string_lossy().into_owned();
    write_ledger(&a, "Bob", "1250", 0);

    assert_eq!(sync(&[&a, &c], None, true).sweep(), 0);
    assert!(!Path::new(&mod_save_path(&c)).exists());
}

#[test]
fn the_join_fast_path_pushes_one_ledger_even_for_an_online_player() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Hopper", "800", 0);
    write_ledger(&c, "Hopper", "100", -10);

    sync(&[&a, &c], Some(FakeRoster::new(true, &["Hopper"])), true).sync_player_ledger("Hopper");

    assert_eq!(read(&ledger_path(&c, "Hopper")), "800");
}

#[test]
fn the_join_fast_path_still_refuses_to_wipe_a_balance() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Hopper", "0", 0);
    write_ledger(&c, "Hopper", "2500", -5);

    sync(&[&a, &c], None, true).sync_player_ledger("Hopper");

    assert_eq!(read(&ledger_path(&c, "Hopper")), "2500");
}

// ---- faction rosters on a freshly provisioned server ----

fn rosters(install: &str) -> PathBuf {
    Path::new(&mod_save_path(install)).join("FactionRoles")
}

fn write_roster(install: &str, file: &str, content: &str, minutes: i64) {
    fs::create_dir_all(rosters(install)).unwrap();
    let path = rosters(install).join(file);
    fs::write(&path, content).unwrap();
    touch(&path, minutes);
}

fn roster_sync(installs: &[&str]) -> ModSaveSync {
    let roster: Arc<dyn OnlineRoster> = Arc::new(FakeRoster::new(true, &[]));
    let folder = rosters(installs[0]).to_string_lossy().into_owned();
    ModSaveSync::new(
        installs.iter().map(|s| (*s).to_owned()).collect(),
        Some(roster),
        true,
        &[],
        Some(&folder),
    )
}

#[test]
fn a_fresh_server_gets_its_faction_roles_folder_and_every_roster() {
    let b = Box_::new();
    let one = b.install("pavlovserver");
    let two = b.bare_install("pavlovserver2");
    let three = b.bare_install("pavlovserver3");
    write_roster(&one, "gambinospawn.txt", "Alice\nBob\n", 0);
    write_roster(&one, "policecadet.txt", "Carol\n", 0);

    let _ = roster_sync(&[&one, &two, &three]).sweep();

    for server in [&two, &three] {
        assert_eq!(
            read(&rosters(server).join("gambinospawn.txt")),
            "Alice\nBob\n"
        );
        assert_eq!(read(&rosters(server).join("policecadet.txt")), "Carol\n");
    }
}

#[test]
fn an_empty_roster_on_a_new_server_never_wipes_the_real_one() {
    let b = Box_::new();
    let (one, two) = (b.install("pavlovserver"), b.install("pavlovserver2"));
    write_roster(&one, "gambinospawn.txt", "Alice\n", -60);
    write_roster(&two, "gambinospawn.txt", "", 0);

    let _ = roster_sync(&[&one, &two]).sweep();

    assert_eq!(read(&rosters(&one).join("gambinospawn.txt")), "Alice\n");
}

#[test]
fn a_wipe_from_the_bots_own_roster_folder_still_goes_out() {
    let b = Box_::new();
    let (one, two) = (b.install("pavlovserver"), b.install("pavlovserver2"));
    write_roster(&two, "gambinospawn.txt", "Alice\n", -60);
    write_roster(&one, "gambinospawn.txt", "", 0);

    let _ = roster_sync(&[&one, &two]).sweep();

    assert_eq!(read(&rosters(&two).join("gambinospawn.txt")), "");
}
