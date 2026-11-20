//! Port of `LicenseTests.cs`, the `LicenseStoreTests` half, over the JSON saves
//! (`<name>.json`, owner change 2026-11-18): licences and money written through the one store
//! that knows when a save may be touched. The C#'s text-shape, bare-save and online-gate tests
//! are dropped with that behaviour (see rust/MIGRATION.md); the tests after them are new.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::fs;
use std::path::PathBuf;
use std::sync::Arc;

use futures_util::future::join_all;
use pavlov_core::economy::{BalanceStore, Ledger};
use pavlov_core::licensing::Licenses;
use pavlov_core::sync::PlayerSave;
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::ledger_store::{LedgerFileStore, LicenseOutcome};

/// The owner's sample save, byte for byte.
const SAMPLE: &str = "{
\t\"money\": 153,
\t\"drivers_license\": false,
\t\"firearm_license\": true,
\t\"look\":
\t{
\t\t\"on\": true,
\t\t\"shirt\": 1,
\t\t\"pants\": 2,
\t\t\"hair\": 2,
\t\t\"hair_color\": 2,
\t\t\"beard\": 0,
\t\t\"body\": 1
\t},
\t\"face\":
\t{
\t\t\"face\": 3,
\t\t\"eyes\": 1,
\t\t\"brows\": 0,
\t\t\"detail\": 0
\t},
\t\"skin\": 3,
\t\"name\": \"Rickythegamer1001\",
\t\"pf_version\": 1,
\t\"updated\": \"2026-11-18 15:44\"
}";

const STAMP: &str = "2026-11-19 09:30";

fn save(money: i64, drivers: bool, firearm: bool) -> String {
    format!(
        "{{\n\t\"money\": {money},\n\t\"drivers_license\": {drivers},\n\t\"firearm_license\": {firearm},\n\t\"skin\": 3,\n\t\"updated\": \"2026-11-18 15:44\"\n}}\n"
    )
}

struct Fixture {
    dir: tempfile::TempDir,
}

impl Fixture {
    fn new() -> Self {
        Self {
            dir: tempfile::tempdir().unwrap(),
        }
    }

    fn store(&self) -> LedgerFileStore {
        LedgerFileStore::new(
            Some(self.dir.path().to_path_buf()),
            Arc::new(GameFileGuard::none()),
        )
        .with_stamp(Arc::new(|| STAMP.to_owned()))
    }

    fn path_of(&self, player: &str) -> PathBuf {
        self.dir.path().join(format!("{player}.json"))
    }

    fn seed(&self, player: &str, content: &str) {
        fs::write(self.path_of(player), content).unwrap();
    }

    fn contents(&self, player: &str) -> String {
        fs::read_to_string(self.path_of(player)).unwrap()
    }

    fn parsed(&self, player: &str) -> PlayerSave {
        PlayerSave::parse(Some(&self.contents(player))).unwrap()
    }
}

#[tokio::test]
async fn a_payout_no_longer_wipes_the_licenses() {
    let f = Fixture::new();
    f.seed("Butter Life", &save(100, true, true));

    let change = Ledger::new(f.store()).credit("Butter Life", 50).await;

    assert!(change.ok);
    assert_eq!(change.before, 100);
    let after = f.parsed("Butter Life");
    assert_eq!(after.money(), Some(150));
    assert_eq!(after.license("drivers_license"), Some(true));
    assert_eq!(after.license("firearm_license"), Some(true));
}

#[test]
fn a_keyed_balance_is_read() {
    let f = Fixture::new();
    f.seed("Alice", &save(1250, false, false));
    assert_eq!(f.store().read("Alice"), Some(1250));
}

#[test]
fn a_file_in_neither_shape_is_refused_not_overwritten() {
    let f = Fixture::new();
    let other_mod = "{\n\t\"setting\": \"value\"\n}";
    f.seed("Alice", other_mod);
    let store = f.store();

    assert!(!store.write("Alice", 60));
    assert!(
        store
            .refusal("Alice")
            .unwrap()
            .contains("recognised format")
    );
    assert_eq!(f.contents("Alice"), other_mod);
}

#[test]
fn one_license_goes_and_everything_else_stays() {
    let f = Fixture::new();
    f.seed("Alice", &save(40, true, true));

    let result = f.store().remove_licenses("Alice", &[Licenses::FIREARM]);

    assert_eq!(result.outcome, LicenseOutcome::Changed);
    assert_eq!(result.changed, [Licenses::FIREARM]);
    let after = f.parsed("Alice");
    assert_eq!(after.license("firearm_license"), Some(false));
    assert_eq!(after.license("drivers_license"), Some(true));
    assert_eq!(after.money(), Some(40));
}

#[test]
fn both_go_together() {
    let f = Fixture::new();
    f.seed("Alice", &save(40, true, true));

    let result = f.store().remove_licenses("Alice", &Licenses::ALL);

    assert_eq!(result.changed, Licenses::ALL);
    let after = f.parsed("Alice");
    assert_eq!(after.license("drivers_license"), Some(false));
    assert_eq!(after.license("firearm_license"), Some(false));
}

#[test]
fn only_the_held_ones_are_reported() {
    let f = Fixture::new();
    f.seed("Alice", &save(40, false, true));
    assert_eq!(
        f.store().remove_licenses("Alice", &Licenses::ALL).changed,
        [Licenses::FIREARM]
    );
}

#[test]
fn nothing_held_is_nothing_written() {
    let f = Fixture::new();
    let original = save(0, false, false);
    f.seed("Alice", &original);
    let before = fs::metadata(f.path_of("Alice"))
        .unwrap()
        .modified()
        .unwrap();

    let result = f.store().remove_licenses("Alice", &Licenses::ALL);

    assert_eq!(result.outcome, LicenseOutcome::AlreadySo);
    assert_eq!(f.contents("Alice"), original, "not even `updated` moves");
    assert_eq!(
        fs::metadata(f.path_of("Alice"))
            .unwrap()
            .modified()
            .unwrap(),
        before
    );
}

#[test]
fn a_save_is_never_created() {
    let f = Fixture::new();
    let result = f.store().remove_licenses("Nobody", &Licenses::ALL);

    assert_eq!(result.outcome, LicenseOutcome::Refused);
    assert!(!f.path_of("Nobody").exists());
    assert!(!f.store().write("Nobody", 10));
    assert!(!f.path_of("Nobody").exists());
}

#[tokio::test(flavor = "multi_thread")]
async fn payouts_and_revocations_do_not_undo_each_other() {
    // Each edit re-reads under the lock, or a payout that read before a revocation would write
    // the licence back.
    let f = Fixture::new();
    f.seed("Alice", &save(0, true, true));
    let store = Arc::new(f.store());
    let ledger = Ledger::new(Arc::clone(&store));

    let revoke = {
        let store = Arc::clone(&store);
        tokio::task::spawn_blocking(move || store.remove_licenses("Alice", &Licenses::ALL))
    };
    join_all((0..50).map(|_| ledger.credit("Alice", 1))).await;
    revoke.await.unwrap();

    let after = f.parsed("Alice");
    assert_eq!(after.money(), Some(50));
    assert_eq!(after.license("drivers_license"), Some(false));
    assert_eq!(after.license("firearm_license"), Some(false));
}

// ---- new with the JSON saves ----

#[test]
fn an_edit_changes_only_its_field_and_stamps_updated() {
    let f = Fixture::new();
    f.seed("Rickythegamer1001", SAMPLE);

    assert!(f.store().write("Rickythegamer1001", 500));

    let expected = format!(
        "{}\n",
        SAMPLE
            .replace("\"money\": 153", "\"money\": 500")
            .replace("2026-11-18 15:44", STAMP)
            // The game writes a nested object's brace on its own line; serde puts it after the
            // key. Same JSON, the only layout difference.
            .replace("\"look\":\n\t{", "\"look\": {")
            .replace("\"face\":\n\t{", "\"face\": {")
    );
    assert_eq!(f.contents("Rickythegamer1001"), expected);
}

#[test]
fn a_license_is_given_and_a_missing_one_is_added() {
    let f = Fixture::new();
    f.seed(
        "Alice",
        "{\n\t\"money\": 5,\n\t\"firearm_license\": false\n}",
    );

    let result = f.store().give_licenses("Alice", &Licenses::ALL);

    assert_eq!(result.outcome, LicenseOutcome::Changed);
    assert_eq!(result.changed, Licenses::ALL);
    let after = f.parsed("Alice");
    assert_eq!(after.license("drivers_license"), Some(true));
    assert_eq!(after.license("firearm_license"), Some(true));
    assert_eq!(
        f.store().give_licenses("Alice", &Licenses::ALL).outcome,
        LicenseOutcome::AlreadySo
    );
}

#[test]
fn a_player_in_game_is_edited_because_the_mod_syncs_live() {
    // No online gate: the store does not even know who is online.
    let f = Fixture::new();
    f.seed("Alice", &save(10, true, false));
    assert!(f.store().write("Alice", 20));
    assert_eq!(f.parsed("Alice").money(), Some(20));
}

#[test]
fn the_save_is_found_whatever_the_case_of_the_name() {
    let f = Fixture::new();
    f.seed("RickyTheGamer", &save(7, false, false));
    let store = f.store();

    assert_eq!(store.read("rickythegamer"), Some(7));
    assert!(store.write("RICKYTHEGAMER", 8));
    assert_eq!(f.parsed("RickyTheGamer").money(), Some(8));
    assert!(
        !f.path_of("RICKYTHEGAMER").exists(),
        "no second file appears"
    );
}

#[test]
fn a_name_cannot_reach_outside_the_directory() {
    let f = Fixture::new();
    assert!(f.store().path_for("..").is_none());
    let path = f.store().path_for("../../etc/passwd").unwrap();
    assert_eq!(path.parent().unwrap(), f.dir.path());
}
