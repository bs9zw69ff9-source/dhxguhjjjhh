//! Port of `PayrollTests.cs` (and its `PayrollStateShapeTests`): accrue on duty, bank on
//! leaving, whole periods with the remainder carried, and never against a stale roster.
//! Balances are asserted in the ModSave ledger files the game reads.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use chrono::{DateTime, TimeDelta};
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::economy::{BalanceStore, Ledger};
use pavlov_core::factions::FactionRegistry;
use pavlov_core::time::ManualClock;
use pavlov_host::economy::{Payroll, PayrollParts, PayrollRun, PayrollState};
use pavlov_host::factions::RosterService;
use pavlov_rcon::OnlineRoster;
use pavlov_storage::datasets::PAYROLL_STATE;
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::ledger_store::LedgerFileStore;

/// Who is online, and separately who is CONFIRMED online (on a fresh roster).
struct FakeRoster {
    trustworthy: bool,
    online: Vec<String>,
    confirmed: Vec<String>,
}

impl OnlineRoster for FakeRoster {
    fn online(&self) -> Vec<String> {
        self.online.clone()
    }
    fn confirmed_online(&self) -> Vec<String> {
        self.confirmed.clone()
    }
    fn is_trustworthy(&self) -> bool {
        self.trustworthy
    }
}

fn roster(fresh: bool, online: &[&str]) -> Arc<dyn OnlineRoster> {
    let names: Vec<String> = online.iter().map(|s| (*s).to_owned()).collect();
    Arc::new(FakeRoster {
        trustworthy: fresh,
        online: names.clone(),
        confirmed: names,
    })
}

struct Fixture {
    _root: tempfile::TempDir,
    rosters_dir: PathBuf,
    ledgers: PathBuf,
    clock: Arc<ManualClock>,
    store: Arc<SerializedStore>,
    rosters: Arc<RosterService>,
}

impl Fixture {
    fn new() -> Self {
        let root = tempfile::tempdir().unwrap();
        let rosters_dir = root.path().join("rosters");
        let ledgers = root.path().join("modsave");
        std::fs::create_dir_all(&rosters_dir).unwrap();
        std::fs::create_dir_all(&ledgers).unwrap();
        let rosters = Arc::new(
            RosterService::new(Some(rosters_dir.clone()), root.path().join("_bak"))
                .with_factions(FactionRegistry::police().clone()),
        );
        Self {
            _root: root,
            rosters_dir,
            ledgers,
            clock: Arc::new(ManualClock::new(
                DateTime::UNIX_EPOCH + TimeDelta::days(56 * 365),
            )),
            store: Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new()))),
            rosters,
        }
    }

    fn put_on_roster(&self, players: &[&str]) {
        let faction = FactionRegistry::get(Some(FactionRegistry::POLICE_NAME)).unwrap();
        std::fs::write(
            self.rosters_dir.join(&faction.rank_files[&faction.default]),
            format!("{}\n", players.join("\n")),
        )
        .unwrap();
    }

    fn give_ledger(&self, player: &str, balance: i64) {
        std::fs::write(
            self.ledgers.join(format!("{player}.json")),
            format!("{{\n\t\"money\": {balance},\n\t\"skin\": 3,\n\t\"name\": \"{player}\"\n}}\n"),
        )
        .unwrap();
    }

    fn balance_of(&self, player: &str) -> Option<i64> {
        std::fs::read_to_string(self.ledgers.join(format!("{player}.json")))
            .ok()
            .and_then(|t| pavlov_core::sync::ledger_file::balance(Some(&t)))
    }

    fn build_with(&self, online: Arc<dyn OnlineRoster>, amount: i64) -> Payroll {
        let ledger_store: Arc<dyn BalanceStore> = Arc::new(LedgerFileStore::new(
            Some(self.ledgers.clone()),
            Arc::new(GameFileGuard::none()),
        ));
        Payroll::new(PayrollParts {
            store: Arc::clone(&self.store),
            ledger: Arc::new(Ledger::new(ledger_store)),
            rosters: Arc::clone(&self.rosters),
            online,
            clock: Arc::clone(&self.clock) as _,
            amount,
            period: Duration::from_secs(30 * 60),
            faction: Some("NYPD".into()),
        })
    }

    fn build(&self, online: Arc<dyn OnlineRoster>) -> Payroll {
        self.build_with(online, 500)
    }

    fn idle(&self) -> Payroll {
        self.build(roster(true, &[]))
    }

    async fn tick(&self, online: &[&str]) -> PayrollRun {
        self.build(roster(true, online)).run().await
    }

    fn wait(&self, minutes: i64) {
        self.clock.advance(TimeDelta::minutes(minutes));
    }

    async fn advance(&self, minutes: i64, online: &[&str]) -> PayrollRun {
        self.wait(minutes);
        self.tick(online).await
    }
}

fn accrued(run: &PayrollRun, player: &str) -> Option<i64> {
    run.accrued.get(player).copied()
}

fn paid(run: &PayrollRun, player: &str) -> Option<i64> {
    run.paid.get(player).copied()
}

fn minutes(m: u64) -> Duration {
    Duration::from_secs(m * 60)
}

#[tokio::test]
async fn an_officer_on_a_crashed_server_accrues_nothing() {
    // Server 2 crashed with Bob on it: still in `online`, not in `confirmed_online`.
    let f = Fixture::new();
    f.put_on_roster(&["Alice", "Bob"]);
    f.give_ledger("Alice", 0);
    f.give_ledger("Bob", 0);
    let split: Arc<dyn OnlineRoster> = Arc::new(FakeRoster {
        trustworthy: true,
        online: vec!["Alice".into(), "Bob".into()],
        confirmed: vec!["Alice".into()],
    });

    f.build(Arc::clone(&split)).run().await;
    f.wait(30);
    let run = f.build(split).run().await;

    assert_eq!(accrued(&run, "Alice"), Some(500));
    assert_eq!(accrued(&run, "Bob"), None);
}

#[tokio::test]
async fn an_online_officer_accrues_but_is_not_paid_yet() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 1000);

    f.tick(&["Alice"]).await; // first sighting
    let run = f.advance(30, &["Alice"]).await; // a whole period on duty

    assert!(run.paid.is_empty());
    assert_eq!(accrued(&run, "Alice"), Some(500));
    assert_eq!(f.balance_of("Alice"), Some(1000));
}

#[tokio::test]
async fn the_wage_is_banked_once_they_log_off() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 1000);

    f.tick(&["Alice"]).await;
    f.advance(30, &["Alice"]).await;
    let run = f.advance(1, &[]).await; // logged off: banks

    assert_eq!(paid(&run, "Alice"), Some(500));
    assert_eq!(f.balance_of("Alice"), Some(1500));
    assert_eq!(f.idle().owed_to("Alice"), 0);
}

#[tokio::test]
async fn a_whole_shift_is_banked_in_one_payment() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 0);

    f.tick(&["Alice"]).await;
    for _ in 0..4 {
        f.advance(30, &["Alice"]).await;
    }
    assert_eq!(f.build(roster(true, &["Alice"])).owed_to("Alice"), 2000);
    assert_eq!(f.balance_of("Alice"), Some(0));

    let run = f.advance(1, &[]).await;

    assert_eq!(paid(&run, "Alice"), Some(2000));
    assert_eq!(f.balance_of("Alice"), Some(2000));
}

#[tokio::test]
async fn a_partial_period_is_carried_and_paid_once_it_completes() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 0);

    f.tick(&["Alice"]).await;
    let at_thirty = f.advance(30, &["Alice"]).await;
    assert_eq!(accrued(&at_thirty, "Alice"), Some(500));

    let at_forty_two = f.advance(12, &["Alice"]).await; // 12 carried
    assert!(at_forty_two.accrued.is_empty());
    assert_eq!(f.idle().owed_to("Alice"), 500);
    assert_eq!(f.idle().earned_toward_next_period("Alice"), minutes(12));

    let at_sixty = f.advance(18, &["Alice"]).await; // the remaining 18 completes it
    assert_eq!(accrued(&at_sixty, "Alice"), Some(500));
    assert_eq!(f.idle().owed_to("Alice"), 1000);
    assert_eq!(f.idle().earned_toward_next_period("Alice"), Duration::ZERO);
}

#[tokio::test]
async fn carried_time_survives_logging_off_and_back_on() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 0);

    f.tick(&["Alice"]).await;
    f.advance(20, &["Alice"]).await; // 20 minutes, then leaves
    f.advance(5, &[]).await; // offline
    assert_eq!(f.idle().earned_toward_next_period("Alice"), minutes(20));

    f.tick(&["Alice"]).await; // back on: a new first sighting
    let completes = f.advance(10, &["Alice"]).await; // 20 carried + 10

    assert_eq!(accrued(&completes, "Alice"), Some(500));
}

#[tokio::test]
async fn each_member_is_paid_for_their_own_time_not_the_factions_clock() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice", "Bob"]);
    f.give_ledger("Alice", 0);
    f.give_ledger("Bob", 0);

    f.tick(&["Alice"]).await;
    f.advance(20, &["Alice", "Bob"]).await; // Bob joins 20 minutes in
    let run = f.advance(10, &["Alice", "Bob"]).await; // Alice: 30. Bob: 10.

    assert_eq!(accrued(&run, "Alice"), Some(500));
    assert_eq!(accrued(&run, "Bob"), None);
    assert_eq!(f.idle().earned_toward_next_period("Bob"), minutes(10));
}

#[tokio::test]
async fn somebody_online_but_not_on_the_roster_accrues_nothing() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Stranger", 100);

    f.tick(&["Alice", "Stranger"]).await;
    f.advance(30, &["Alice", "Stranger"]).await;
    f.advance(1, &[]).await;

    assert_eq!(f.balance_of("Stranger"), Some(100));
}

#[tokio::test]
async fn an_offline_officer_who_never_served_is_not_paid() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice", "Dormant"]);
    f.give_ledger("Dormant", 50);

    f.tick(&["Alice"]).await;
    f.advance(30, &["Alice"]).await;
    f.advance(1, &[]).await;

    assert_eq!(f.balance_of("Dormant"), Some(50));
}

#[tokio::test]
async fn a_stale_roster_accrues_nothing_and_banks_nothing() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 1000);

    f.tick(&["Alice"]).await;
    f.advance(30, &["Alice"]).await; // 500 owed
    f.wait(1);

    let stale = f.build(roster(false, &[])).run().await;

    assert!(stale.paid.is_empty());
    assert!(stale.accrued.is_empty());
    assert!(
        stale
            .skipped
            .as_deref()
            .unwrap()
            .to_lowercase()
            .contains("fresh")
    );
    assert_eq!(f.balance_of("Alice"), Some(1000));

    // The period was not consumed either, so nothing was lost by refusing.
    let recovered = f.tick(&[]).await;
    assert_eq!(paid(&recovered, "Alice"), Some(500));
}

#[tokio::test]
async fn running_twice_inside_one_period_accrues_once() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 0);

    f.tick(&["Alice"]).await;
    f.advance(30, &["Alice"]).await;
    f.clock.advance(TimeDelta::seconds(1));
    let second = f.tick(&["Alice"]).await;

    assert!(second.accrued.is_empty());
    assert_eq!(f.idle().owed_to("Alice"), 500);
}

#[tokio::test]
async fn a_long_outage_does_not_accrue_a_backlog() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 0);

    f.tick(&["Alice"]).await;
    f.clock.advance(TimeDelta::hours(6));
    f.tick(&["Alice"]).await;

    assert_eq!(f.idle().owed_to("Alice"), 500); // one period, not twelve
}

#[tokio::test]
async fn nobody_on_duty_still_consumes_the_period() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 0);

    f.tick(&[]).await; // nobody online; the period is stamped
    f.advance(1, &["Alice"]).await; // first sighting
    let soon_after = f.advance(1, &["Alice"]).await; // one observed minute

    assert!(soon_after.accrued.is_empty());
    assert_eq!(f.idle().earned_toward_next_period("Alice"), minutes(1));
}

#[tokio::test]
async fn an_officer_with_no_ledger_keeps_their_wage_owed() {
    // The bot never CREATES a ledger; the wage waits for the game to make one.
    let f = Fixture::new();
    f.put_on_roster(&["Newbie"]);

    f.tick(&["Newbie"]).await;
    f.advance(30, &["Newbie"]).await;
    let no_ledger = f.advance(1, &[]).await;

    assert!(no_ledger.paid.is_empty());
    assert_eq!(f.idle().owed_to("Newbie"), 500);
    assert_eq!(f.balance_of("Newbie"), None);

    f.give_ledger("Newbie", 25);
    let after_first_bank = f.tick(&[]).await;

    assert_eq!(paid(&after_first_bank, "Newbie"), Some(500));
    assert_eq!(f.balance_of("Newbie"), Some(525));
}

#[tokio::test]
async fn a_wage_accrued_while_settling_is_not_swallowed() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 0);

    f.tick(&["Alice"]).await;
    f.advance(30, &["Alice"]).await;
    f.advance(1, &[]).await;
    assert_eq!(f.balance_of("Alice"), Some(500));

    f.tick(&["Alice"]).await;
    f.advance(30, &["Alice"]).await;
    f.advance(1, &[]).await;

    assert_eq!(f.balance_of("Alice"), Some(1000));
}

#[tokio::test]
async fn the_wage_lands_in_the_json_save_and_keeps_its_other_fields() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 1234);

    f.build_with(roster(true, &["Alice"]), 1000).run().await;
    f.wait(30);
    f.build_with(roster(true, &["Alice"]), 1000).run().await;
    f.wait(1);
    f.build_with(roster(true, &[]), 1000).run().await;

    let save = std::fs::read_to_string(f.ledgers.join("Alice.json")).unwrap();
    let parsed = pavlov_core::sync::PlayerSave::parse(Some(&save)).unwrap();
    assert_eq!(parsed.money(), Some(2234));
    assert_eq!(parsed.name(), Some("Alice"));
    assert!(
        save.contains("\t\"skin\": 3"),
        "the fields the bot does not own are kept: {save}"
    );
}

#[tokio::test]
async fn no_temporary_files_are_left_in_the_ledger_directory() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 0);

    f.tick(&["Alice"]).await;
    f.advance(30, &["Alice"]).await;
    let banked = f.advance(1, &[]).await;

    assert_eq!(paid(&banked, "Alice"), Some(500));
    let files: Vec<_> = std::fs::read_dir(&f.ledgers).unwrap().collect();
    assert_eq!(files.len(), 1);
}

#[tokio::test]
async fn zero_amount_is_off() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 100);
    let payroll = f.build_with(roster(true, &["Alice"]), 0);

    assert!(!payroll.enabled());
    let run = payroll.run().await;

    assert!(!run.did_something());
    assert_eq!(f.balance_of("Alice"), Some(100));
}

#[tokio::test]
async fn history_records_money_that_moved_rather_than_money_promised() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice"]);
    f.give_ledger("Alice", 0);

    f.tick(&["Alice"]).await;
    f.advance(30, &["Alice"]).await;
    assert!(f.idle().history(10).is_empty()); // accrual alone is not a payment

    f.advance(1, &[]).await;

    let history = f.idle().history(10);
    assert_eq!(history.len(), 1);
    assert_eq!(history[0].total(), 500);
    assert_eq!(f.idle().total_paid_to("Alice"), 500);
}

#[tokio::test]
async fn outstanding_lists_who_is_carrying_an_unbanked_wage() {
    let f = Fixture::new();
    f.put_on_roster(&["Alice", "Bob"]);
    f.give_ledger("Alice", 0);
    f.give_ledger("Bob", 0);

    f.tick(&["Alice", "Bob"]).await;
    f.advance(30, &["Alice", "Bob"]).await;
    f.advance(30, &["Alice", "Bob"]).await;

    let outstanding = f.idle().outstanding();
    assert_eq!(outstanding.len(), 2);
    assert!(outstanding.iter().all(|(_, owed)| *owed == 1000));
}

// ---- PayrollStateShapeTests ----

fn read_state(json: &str) -> PayrollState {
    let backend = Arc::new(MemoryBackend::new());
    backend.seed(PAYROLL_STATE, json);
    SerializedStore::new(backend)
        .read(PAYROLL_STATE, PayrollState::default())
        .unwrap()
}

#[test]
fn a_row_with_no_owed_property_does_not_throw_on_read() {
    // Exactly the live row: written before "owed" existed.
    let state = read_state(r#"{"lastPaid":{"NCR":"2026-09-01T00:00:00+00:00"}}"#);

    assert!(state.owed.is_empty());
    assert_eq!(state.owed.get("Courier6"), None);
    assert_eq!(state.last_paid.len(), 1);
}

#[test]
fn an_explicit_null_is_treated_as_empty_rather_than_as_a_crash() {
    let state =
        read_state(r#"{"lastPaid":null,"owed":null,"onDutySeconds":null,"lastOnDuty":null}"#);

    assert!(state.last_paid.is_empty());
    assert!(state.owed.is_empty());
    assert!(state.earned.is_empty());
    assert!(state.previous.is_empty());
}

#[test]
fn an_empty_document_is_a_whole_usable_state() {
    let state = read_state("{}");

    assert_eq!(state.owed.get("Nobody"), None);
    assert_eq!(state.earned.get("Nobody"), None);
    assert!(state.previous.is_empty());
    assert!(state.last_paid.is_empty());
}

#[test]
fn names_still_match_case_insensitively_after_a_round_trip() {
    let state =
        read_state(r#"{"lastPaid":{"ncr":"2026-09-01T00:00:00+00:00"},"owed":{"alice":250}}"#);

    assert_eq!(state.owed.get("Alice"), Some(&250));
    assert!(state.last_paid.contains_key("NCR"));
}

#[test]
fn a_with_expression_cannot_reintroduce_the_null() {
    // Rust has no `with` that can set a map to null: a replaced map is still a map.
    let state = PayrollState {
        owed: Default::default(),
        ..PayrollState::default()
    };
    assert_eq!(state.owed.get("Courier6"), None);
}

#[test]
fn two_keys_differing_only_by_case_do_not_throw() {
    // A hand-edited file: the later entry wins.
    let state = read_state(r#"{"owed":{"alice":10,"Alice":25}}"#);
    assert_eq!(state.owed.get("ALICE"), Some(&25));
}
