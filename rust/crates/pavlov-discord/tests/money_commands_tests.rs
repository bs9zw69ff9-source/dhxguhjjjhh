//! `/givemoney` and `/adjustmoney`: give never takes away, and a refused write never reports
//! the balance it meant to set. (The C# has no test file for these.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use pavlov_core::economy::{BalanceStore, Ledger};
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::economy::MoneyCommand;
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::moderation::AuditLog;

const OWNER: u64 = 1;

#[derive(Default)]
struct Balances {
    values: Mutex<HashMap<String, i64>>,
    refuse: bool,
}

impl BalanceStore for Balances {
    fn read(&self, player_id: &str) -> Option<i64> {
        self.values.lock().unwrap().get(player_id).copied()
    }
    fn write(&self, player_id: &str, balance: i64) -> bool {
        if self.refuse {
            return false;
        }
        self.values
            .lock()
            .unwrap()
            .insert(player_id.into(), balance);
        true
    }
}

fn command(give: bool, balances: Arc<Balances>) -> MoneyCommand {
    let store = common::commands::store();
    let ledger = Arc::new(Ledger::new(balances as Arc<dyn BalanceStore>));
    let audit = Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock)));
    let access = common::commands::access(&store, &[OWNER]);
    if give {
        MoneyCommand::give(ledger, audit, access, None)
    } else {
        MoneyCommand::adjust(ledger, audit, access, None)
    }
}

async fn run(command: &MoneyCommand, player: &str, amount: i64) -> String {
    let invocation = Invocation::new(command.name(), Caller::user(OWNER))
        .with(InvokedOption::value(
            "playerid",
            OptionKind::String,
            OptionValue::String(player.into()),
        ))
        .with(InvokedOption::value(
            "amount",
            OptionKind::Integer,
            OptionValue::Integer(amount),
        ));
    let responder = common::RecordingResponder::default();
    command
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    responder.last_text()
}

#[test]
fn the_names_come_from_the_police_skin() {
    assert_eq!(command(true, Arc::default()).name(), "givemoney");
    assert_eq!(command(false, Arc::default()).name(), "adjustmoney");
}

#[tokio::test]
async fn give_adds_and_reports_before_and_after() {
    let balances = Arc::new(Balances::default());
    balances.write("Alice", 100);
    let text = run(&command(true, Arc::clone(&balances)), "Alice", 250).await;
    assert_eq!(balances.read("Alice"), Some(350));
    assert!(text.contains("+$250"), "{text}");
}

#[tokio::test]
async fn give_refuses_a_minus_rather_than_guessing() {
    let balances = Arc::new(Balances::default());
    balances.write("Alice", 100);
    let text = run(&command(true, Arc::clone(&balances)), "Alice", -50).await;
    assert!(text.contains("Negative amount"), "{text}");
    assert!(text.contains("/adjustmoney"));
    assert_eq!(balances.read("Alice"), Some(100));
}

#[tokio::test]
async fn adjust_can_take_away() {
    let balances = Arc::new(Balances::default());
    balances.write("Alice", 100);
    run(&command(false, Arc::clone(&balances)), "Alice", -40).await;
    assert_eq!(balances.read("Alice"), Some(60));
}

#[tokio::test]
async fn a_refused_write_says_unchanged_not_the_intended_balance() {
    let balances = Arc::new(Balances {
        refuse: true,
        ..Balances::default()
    });
    let text = run(&command(true, balances), "Alice", 500).await;
    assert!(text.contains("Not applied"), "{text}");
    assert!(text.contains("balance is unchanged"));
    assert!(!text.contains("$500"));
}
