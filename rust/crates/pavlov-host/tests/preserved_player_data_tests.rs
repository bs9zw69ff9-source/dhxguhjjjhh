//! Port of `PreservedPlayerDataTests.cs`: `/deleteserver` keeps ModSave (rosters, ledgers) and
//! the player lists; the next `/provisionserver` into that slot puts them back.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::fs;
use std::path::{Path, PathBuf};

use chrono::{DateTime, TimeZone, Utc};
use pavlov_core::servers::installs::discover;
use pavlov_host::preserved::{preserve, preserved_dir, restore};
use pavlov_storage::installs::DiskProbe;

fn now() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 9, 27, 14, 31, 0).unwrap()
}

struct Box_ {
    root: tempfile::TempDir,
    install: String,
}

impl Box_ {
    fn new() -> Self {
        let root = tempfile::tempdir().unwrap();
        let install = root
            .path()
            .join("pavlovserver")
            .to_string_lossy()
            .into_owned();
        Self { root, install }
    }

    fn config(&self) -> PathBuf {
        Path::new(&self.install).join("Pavlov/Saved/Config")
    }

    fn roster(&self) -> PathBuf {
        self.config().join("ModSave/FactionRoles/officer.txt")
    }

    fn seed_live_install(&self) {
        fs::create_dir_all(self.roster().parent().unwrap()).unwrap();
        fs::write(self.roster(), "PlayerOne\nPlayerTwo\n").unwrap();
        fs::write(self.config().join("mods.txt"), "fki6\n").unwrap();
        fs::write(self.config().join("blacklist.txt"), "Cheater\n").unwrap();
        fs::create_dir_all(self.config().join("LinuxServer")).unwrap();
        fs::write(self.config().join("LinuxServer/Game.ini"), "old settings").unwrap();
    }

    /// What /provisionserver leaves: the directories, the three lists touched empty, no ModSave.
    fn fresh_provision(&self) {
        fs::create_dir_all(self.config().join("LinuxServer")).unwrap();
        for file in ["mods.txt", "whitelist.txt", "blacklist.txt"] {
            fs::write(self.config().join(file), "").unwrap();
        }
    }
}

#[test]
fn delete_then_provision_keeps_the_rosters_and_lists() {
    let b = Box_::new();
    b.seed_live_install();

    let kept = preserve(&b.install, now()).unwrap();
    fs::remove_dir_all(&b.install).unwrap(); // the rm -rf
    b.fresh_provision();
    let restored = restore(&b.install).unwrap();

    assert_eq!(kept.moved, ["ModSave", "mods.txt", "blacklist.txt"]);
    assert_eq!(
        fs::read_to_string(b.roster()).unwrap(),
        "PlayerOne\nPlayerTwo\n"
    );
    assert_eq!(
        fs::read_to_string(b.config().join("mods.txt")).unwrap(),
        "fki6\n"
    );
    assert_eq!(
        fs::read_to_string(b.config().join("blacklist.txt")).unwrap(),
        "Cheater\n"
    );
    assert!(restored.skipped.is_empty());
    // Emptied and cleaned up, so the next delete starts clean.
    assert!(!preserved_dir(&b.install).exists());
}

#[test]
fn game_settings_are_not_kept() {
    // The new slot's Game.ini carries its own ports; the old one must not come back over it.
    let b = Box_::new();
    b.seed_live_install();

    preserve(&b.install, now()).unwrap();

    assert!(
        !preserved_dir(&b.install)
            .join("LinuxServer/Game.ini")
            .exists()
    );
    assert!(b.config().join("LinuxServer/Game.ini").exists());
}

#[test]
fn the_preserved_folder_is_not_mistaken_for_an_install() {
    let b = Box_::new();
    b.seed_live_install();
    preserve(&b.install, now()).unwrap();

    let preserved = preserved_dir(&b.install).to_string_lossy().into_owned();
    assert!(
        preserved.starts_with(&format!("{}/.", b.root.path().display())),
        "{preserved}"
    );
    let probe = DiskProbe::new(b.root.path().join("no-systemd-here"));
    let found = discover(None, Some(&b.install), &probe);
    assert!(!found.roots.contains(&preserved), "{:?}", found.roots);
}

#[test]
fn restore_never_overwrites_real_data() {
    let b = Box_::new();
    b.seed_live_install();
    preserve(&b.install, now()).unwrap();

    // A provision that copied another install already has a populated ModSave.
    fs::create_dir_all(b.roster().parent().unwrap()).unwrap();
    fs::write(b.roster(), "Copied\n").unwrap();
    fs::write(b.config().join("mods.txt"), "").unwrap();

    let restored = restore(&b.install).unwrap();

    assert_eq!(fs::read_to_string(b.roster()).unwrap(), "Copied\n");
    assert!(restored.moved.contains(&"mods.txt".to_owned()));
    assert_eq!(
        restored
            .skipped
            .iter()
            .filter(|s| s.starts_with("ModSave"))
            .count(),
        1
    );
    assert_eq!(restored.skipped.len(), 1);
    assert!(preserved_dir(&b.install).join("ModSave").is_dir());
}

#[test]
fn a_second_delete_does_not_destroy_the_first_ones_data() {
    let b = Box_::new();
    b.seed_live_install();
    preserve(&b.install, now()).unwrap();

    // Re-provisioned by hand (no restore), then deleted again with different data.
    fs::create_dir_all(b.roster().parent().unwrap()).unwrap();
    fs::write(b.roster(), "Newer\n").unwrap();
    preserve(&b.install, now()).unwrap();

    let preserved = preserved_dir(&b.install);
    assert_eq!(
        fs::read_to_string(preserved.join("ModSave/FactionRoles/officer.txt")).unwrap(),
        "Newer\n"
    );
    let aside = PathBuf::from(format!("{}-20260927-143100", preserved.display()));
    assert_eq!(
        fs::read_to_string(aside.join("ModSave/FactionRoles/officer.txt")).unwrap(),
        "PlayerOne\nPlayerTwo\n"
    );
}

#[test]
fn an_install_with_nothing_to_keep_leaves_no_folder_behind() {
    let b = Box_::new();
    fs::create_dir_all(b.config()).unwrap();

    assert!(preserve(&b.install, now()).unwrap().moved.is_empty());
    assert!(!preserved_dir(&b.install).exists());
    assert!(restore(&b.install).unwrap().moved.is_empty());
}
