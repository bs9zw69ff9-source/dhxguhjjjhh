//! Port of `ModSaveSyncTests.cs`: the sweep across installs, on real directories, over the JSON
//! player saves (`<name>.json`). The C#'s online-guard, RCON+ skip and join fast-path tests
//! are dropped with the behaviour (owner, 2026-11-18; see rust/MIGRATION.md).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::fs;
use std::path::{Path, PathBuf};

use common::touch;
use pavlov_core::servers::installs::mod_save_path;
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
    Path::new(&mod_save_path(install)).join(format!("{player}.json"))
}

/// A save holding `money` (or, for "", an empty file).
fn write_ledger(install: &str, player: &str, money: &str, minutes: i64) {
    let path = ledger_path(install, player);
    let content = if money.is_empty() {
        String::new()
    } else {
        format!(
            "{{\n\t\"money\": {money},\n\t\"drivers_license\": false,\n\t\"name\": \"{player}\"\n}}\n"
        )
    };
    fs::write(&path, content).unwrap();
    touch(&path, minutes);
}

/// The balance a save holds.
fn money(path: &Path) -> String {
    pavlov_core::sync::ledger_file::balance(Some(&read(path)))
        .map(|m| m.to_string())
        .unwrap_or_default()
}

fn read(path: &Path) -> String {
    fs::read_to_string(path).unwrap()
}

fn sync(installs: &[&str], _online: Option<()>, enabled: bool) -> ModSaveSync {
    ModSaveSync::new(
        installs.iter().map(|s| (*s).to_owned()).collect(),
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
    assert_eq!(money(&ledger_path(&c, "Bob")), "1250");
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
    assert_eq!(money(&ledger_path(&c, "Solo")), "42");
}

#[test]
fn an_online_players_save_syncs_like_any_other() {
    // The mod writes in-game state to the save live, so the newest file is current: there is
    // no online guard to hold it back any more.
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Pkd", "999", 0);
    write_ledger(&c, "Pkd", "500", -10);

    assert_eq!(sync(&[&a, &c], None, true).sweep(), 1);
    assert_eq!(money(&ledger_path(&c, "Pkd")), "999");
}

#[test]
fn the_menu_tier_lists_mirror_newest_wins() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    let roles_a = Path::new(&mod_save_path(&a)).join("ServerRoles");
    let roles_c = Path::new(&mod_save_path(&c)).join("ServerRoles");
    fs::create_dir_all(&roles_a).unwrap();
    fs::create_dir_all(&roles_c).unwrap();
    fs::write(roles_a.join("staff.txt"), "Alice\nBob\n").unwrap();
    fs::write(roles_c.join("staff.txt"), "Alice\n").unwrap();
    touch(&roles_c.join("staff.txt"), -10);

    let _ = sync(&[&a, &c], None, true).sweep();

    assert_eq!(read(&roles_c.join("staff.txt")), "Alice\nBob\n");
}

#[test]
fn an_empty_or_zero_file_never_wipes_a_positive_balance() {
    let b = Box_::new();
    let (a, c) = (b.install("pavlovserver"), b.install("pavlovserver1"));
    write_ledger(&a, "Rich", "0", 0); // fresh zero, newest
    write_ledger(&c, "Rich", "5000", -5);

    assert_eq!(sync(&[&a, &c], None, true).sweep(), 0);
    assert_eq!(money(&ledger_path(&c, "Rich")), "5000");
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
    assert_eq!(money(&ledger_path(&c, "Bob")), "1");
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
    let folder = rosters(installs[0]).to_string_lossy().into_owned();
    ModSaveSync::new(
        installs.iter().map(|s| (*s).to_owned()).collect(),
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

// ---- Deletes propagate (owner, 2026-11-19: "I cleared the modsave files and it reverted") ----

use std::sync::Arc;

use pavlov_core::data::{KeyValueBackend, MemoryBackend};

fn remembering(installs: &[&str], state: &Arc<MemoryBackend>) -> ModSaveSync {
    sync(installs, None, true).with_state(Arc::clone(state) as Arc<dyn KeyValueBackend>)
}

#[test]
fn a_save_deleted_in_one_install_is_deleted_in_the_others_and_stays_gone() {
    let b = Box_::new();
    let (one, two, three) = (b.install("a"), b.install("b"), b.install("c"));
    for install in [&one, &two, &three] {
        write_ledger(install, "Bob", "500", -60);
    }
    write_ledger(&one, "Alice", "10", -60);
    let state = Arc::new(MemoryBackend::new());
    let sync = remembering(&[&one, &two, &three], &state);
    let _ = sync.sweep(); // converges Alice and records what each install holds

    fs::remove_file(ledger_path(&one, "Bob")).unwrap();
    let _ = sync.sweep();

    for install in [&one, &two, &three] {
        assert!(
            !ledger_path(install, "Bob").exists(),
            "{install} still has Bob"
        );
        assert_eq!(money(&ledger_path(install, "Alice")), "10");
    }
    let _ = sync.sweep();
    assert!(!ledger_path(&one, "Bob").exists(), "the delete was undone");
}

#[test]
fn clearing_every_save_in_one_install_clears_them_everywhere() {
    let b = Box_::new();
    let (one, two) = (b.install("a"), b.install("b"));
    for player in ["Alice", "Bob", "Carol"] {
        write_ledger(&one, player, "100", -60);
        write_ledger(&two, player, "100", -60);
    }
    let state = Arc::new(MemoryBackend::new());
    let sync = remembering(&[&one, &two], &state);
    let _ = sync.sweep();

    for player in ["Alice", "Bob", "Carol"] {
        fs::remove_file(ledger_path(&one, player)).unwrap();
    }
    let _ = sync.sweep();

    assert_eq!(fs::read_dir(mod_save_path(&two)).unwrap().count(), 0);
}

#[test]
fn the_record_survives_a_restart() {
    let b = Box_::new();
    let (one, two) = (b.install("a"), b.install("b"));
    write_ledger(&one, "Bob", "500", -60);
    write_ledger(&two, "Bob", "500", -60);
    let state = Arc::new(MemoryBackend::new());
    let _ = remembering(&[&one, &two], &state).sweep();

    // The bot is down while the save is deleted; a new process reads the stored record.
    fs::remove_file(ledger_path(&two, "Bob")).unwrap();
    let _ = remembering(&[&one, &two], &state).sweep();

    assert!(!ledger_path(&one, "Bob").exists());
}

#[test]
fn without_a_record_a_deleted_save_is_still_copied_back() {
    let b = Box_::new();
    let (one, two) = (b.install("a"), b.install("b"));
    write_ledger(&one, "Bob", "500", -60);
    write_ledger(&two, "Bob", "500", -60);
    let sync = sync(&[&one, &two], None, true);
    let _ = sync.sweep();

    fs::remove_file(ledger_path(&one, "Bob")).unwrap();
    let _ = sync.sweep();

    assert_eq!(money(&ledger_path(&one, "Bob")), "500");
}

#[test]
fn a_save_the_game_wrote_after_the_delete_comes_back() {
    let b = Box_::new();
    let (one, two) = (b.install("a"), b.install("b"));
    write_ledger(&one, "Bob", "500", -60);
    write_ledger(&two, "Bob", "500", -60);
    let state = Arc::new(MemoryBackend::new());
    let sync = remembering(&[&one, &two], &state);
    let _ = sync.sweep();

    fs::remove_file(ledger_path(&one, "Bob")).unwrap();
    write_ledger(&two, "Bob", "750", 0); // Bob rejoined server two and the game saved him
    let _ = sync.sweep();

    assert_eq!(money(&ledger_path(&one, "Bob")), "750");
    assert_eq!(money(&ledger_path(&two, "Bob")), "750");
}

#[test]
fn a_missing_modsave_folder_deletes_nothing_and_returns_as_a_new_install() {
    let b = Box_::new();
    let (one, two) = (b.install("a"), b.install("b"));
    write_ledger(&one, "Bob", "500", -60);
    write_ledger(&two, "Bob", "500", -60);
    let state = Arc::new(MemoryBackend::new());
    let sync = remembering(&[&one, &two], &state);
    let _ = sync.sweep();

    // Server two is reprovisioned: its ModSave goes away, then comes back empty.
    fs::remove_dir_all(mod_save_path(&two)).unwrap();
    let _ = sync.sweep();
    assert_eq!(money(&ledger_path(&one, "Bob")), "500");

    fs::create_dir_all(mod_save_path(&two)).unwrap();
    let _ = sync.sweep();

    assert_eq!(money(&ledger_path(&one, "Bob")), "500");
    assert_eq!(money(&ledger_path(&two, "Bob")), "500");
}

#[test]
fn a_tampered_record_cannot_delete_outside_modsave() {
    let b = Box_::new();
    let (one, two) = (b.install("a"), b.install("b"));
    let outside = b.root.path().join("outside.json");
    fs::write(&outside, "keep").unwrap();
    let state = Arc::new(MemoryBackend::new());
    state.seed(
        pavlov_storage::datasets::MODSAVE_SYNC_STATE,
        &format!(
            r#"{{"{one}":{{"../../../outside.json":1}},"{two}":{{}}}}"#,
            one = one.replace('\\', "\\\\"),
            two = two.replace('\\', "\\\\"),
        ),
    );

    let _ = remembering(&[&one, &two], &state).sweep();

    assert_eq!(read(&outside), "keep");
}
