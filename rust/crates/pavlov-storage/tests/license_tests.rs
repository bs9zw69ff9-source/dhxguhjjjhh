//! Port of `LicenseTests.cs`, the `LicenseStoreTests` half: licences and money written through
//! the one store that knows when a save may be touched. (The file-shape half is in the core
//! crate; `TheCashBoardCountsKeyedSaves` needs the boards and joins with M7.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::fs;
use std::path::PathBuf;
use std::sync::Arc;

use common::FakeRoster;
use futures_util::future::join_all;
use pavlov_core::economy::{BalanceStore, Ledger};
use pavlov_core::licensing::Licenses;
use pavlov_rcon::OnlineRoster;
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::ledger_store::{LedgerFileStore, LicenseOutcome};

const POLICE_SAVE: &str = "Money\n0\nDriversLiscense\nfalse\nFirearmLiscense\nfalse";

struct Fixture {
    dir: tempfile::TempDir,
}

impl Fixture {
    fn new() -> Self {
        Self {
            dir: tempfile::tempdir().unwrap(),
        }
    }

    fn store_with(&self, roster: FakeRoster) -> LedgerFileStore {
        let roster: Arc<dyn OnlineRoster> = Arc::new(roster);
        LedgerFileStore::new(
            Some(self.dir.path().to_path_buf()),
            Some(roster),
            Arc::new(GameFileGuard::none()),
        )
    }

    fn store(&self, online: &[&str]) -> LedgerFileStore {
        self.store_with(FakeRoster::new(true, online))
    }

    fn path_of(&self, player: &str) -> PathBuf {
        self.dir.path().join(format!("{player}.txt"))
    }

    fn seed(&self, player: &str, content: &str) {
        fs::write(self.path_of(player), content).unwrap();
    }

    fn contents(&self, player: &str) -> String {
        fs::read_to_string(self.path_of(player)).unwrap()
    }
}

#[tokio::test]
async fn a_payout_no_longer_wipes_the_licenses() {
    let f = Fixture::new();
    f.seed(
        "Butter Life",
        "Money\n100\nDriversLiscense\ntrue\nFirearmLiscense\ntrue\n",
    );

    let change = Ledger::new(f.store(&[])).credit("Butter Life", 50).await;

    assert!(change.ok);
    assert_eq!(change.before, 100);
    assert_eq!(
        f.contents("Butter Life"),
        "Money\n150\nDriversLiscense\ntrue\nFirearmLiscense\ntrue\n"
    );
}

#[test]
fn a_keyed_balance_is_read() {
    let f = Fixture::new();
    f.seed(
        "Alice",
        "Money\n1250\nDriversLiscense\nfalse\nFirearmLiscense\nfalse",
    );
    assert_eq!(f.store(&[]).read("Alice"), Some(1250));
}

#[test]
fn a_file_in_neither_shape_is_refused_not_overwritten() {
    let f = Fixture::new();
    f.seed("Alice", "Money\n10\nDriversLiscense");
    let store = f.store(&[]);

    assert!(!store.write("Alice", 60));
    assert!(store.refusal("Alice").unwrap().contains("shape"));
    assert_eq!(f.contents("Alice"), "Money\n10\nDriversLiscense");
}

#[test]
fn the_bare_shape_is_still_written_as_it_was() {
    let f = Fixture::new();
    f.seed("Alice", "500");
    assert!(f.store(&[]).write("Alice", 750));
    assert_eq!(f.contents("Alice"), "750");
}

#[test]
fn one_license_goes_and_everything_else_stays() {
    let f = Fixture::new();
    f.seed(
        "Alice",
        "Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\ntrue\n",
    );

    let result = f.store(&[]).remove_licenses("Alice", &[Licenses::FIREARM]);

    assert_eq!(result.outcome, LicenseOutcome::Removed);
    assert_eq!(result.removed, [Licenses::FIREARM]);
    assert_eq!(
        f.contents("Alice"),
        "Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\nfalse\n"
    );
}

#[test]
fn both_go_together() {
    let f = Fixture::new();
    f.seed(
        "Alice",
        "Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\ntrue",
    );

    let result = f.store(&[]).remove_licenses("Alice", &Licenses::ALL);

    assert_eq!(result.removed, Licenses::ALL);
    assert_eq!(
        f.contents("Alice"),
        "Money\n40\nDriversLiscense\nfalse\nFirearmLiscense\nfalse"
    );
}

#[test]
fn only_the_held_ones_are_reported() {
    let f = Fixture::new();
    f.seed(
        "Alice",
        "Money\n40\nDriversLiscense\nfalse\nFirearmLiscense\ntrue",
    );
    assert_eq!(
        f.store(&[])
            .remove_licenses("Alice", &Licenses::ALL)
            .removed,
        [Licenses::FIREARM]
    );
}

#[test]
fn nothing_held_is_nothing_written() {
    let f = Fixture::new();
    f.seed("Alice", POLICE_SAVE);
    let before = fs::metadata(f.path_of("Alice"))
        .unwrap()
        .modified()
        .unwrap();

    let result = f.store(&[]).remove_licenses("Alice", &Licenses::ALL);

    assert_eq!(result.outcome, LicenseOutcome::NoneHeld);
    assert_eq!(f.contents("Alice"), POLICE_SAVE);
    assert_eq!(
        fs::metadata(f.path_of("Alice"))
            .unwrap()
            .modified()
            .unwrap(),
        before
    );
}

#[test]
fn a_bare_save_has_no_licenses_to_take() {
    let f = Fixture::new();
    f.seed("Alice", "500");

    let result = f.store(&[]).remove_licenses("Alice", &Licenses::ALL);

    assert_eq!(result.outcome, LicenseOutcome::NotInFile);
    assert_eq!(f.contents("Alice"), "500");
}

#[test]
fn a_connected_player_is_refused() {
    // The game rewrites the whole file from memory on its next save.
    let f = Fixture::new();
    f.seed(
        "Alice",
        "Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\ntrue",
    );

    let result = f.store(&["alice"]).remove_licenses("Alice", &Licenses::ALL);

    assert_eq!(result.outcome, LicenseOutcome::Refused);
    assert!(result.reason.unwrap().contains("in game"));
    assert!(f.contents("Alice").contains("FirearmLiscense\ntrue"));
}

#[test]
fn an_unknown_roster_is_refused() {
    let f = Fixture::new();
    f.seed(
        "Alice",
        "Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\ntrue",
    );
    let store = f.store_with(FakeRoster::new(false, &[]));

    assert_eq!(
        store.remove_licenses("Alice", &Licenses::ALL).outcome,
        LicenseOutcome::Refused
    );
    assert!(f.contents("Alice").contains("DriversLiscense\ntrue"));
}

#[test]
fn a_save_is_never_created() {
    let f = Fixture::new();
    let result = f.store(&[]).remove_licenses("Nobody", &Licenses::ALL);

    assert_eq!(result.outcome, LicenseOutcome::Refused);
    assert!(!f.path_of("Nobody").exists());
}

#[tokio::test(flavor = "multi_thread")]
async fn payouts_and_revocations_do_not_undo_each_other() {
    // Each edit re-reads under the lock, or a payout that read before a revocation would write
    // the licence back.
    let f = Fixture::new();
    f.seed(
        "Alice",
        "Money\n0\nDriversLiscense\ntrue\nFirearmLiscense\ntrue",
    );
    let store = Arc::new(f.store(&[]));
    let ledger = Ledger::new(Arc::clone(&store));

    let revoke = {
        let store = Arc::clone(&store);
        tokio::task::spawn_blocking(move || store.remove_licenses("Alice", &Licenses::ALL))
    };
    join_all((0..50).map(|_| ledger.credit("Alice", 1))).await;
    revoke.await.unwrap();

    assert_eq!(
        f.contents("Alice"),
        "Money\n50\nDriversLiscense\nfalse\nFirearmLiscense\nfalse"
    );
}
