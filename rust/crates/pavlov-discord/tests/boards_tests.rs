//! Port of `BoardsTests.cs`: the cash, most-wanted, warrant and staff boards, and playtime.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use pavlov_config::Secret;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::police::{Arrest, Warrant};
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::SystemClock;
use pavlov_discord::boards::Boards;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::ModAction;
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::RconOptions;
use pavlov_storage::datasets::{ARRESTS, MOD_LOG, WARRANTS};

struct Rig {
    _dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    ledgers: PathBuf,
    rcon: Arc<RconRegistry>,
    boards: Boards,
}

fn registry() -> Arc<RconRegistry> {
    let options = RconOptions::new("server1", "127.0.0.1", 9100, Secret::new("x"));
    Arc::new(RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ))
}

fn rig() -> Rig {
    let dir = tempfile::tempdir().unwrap();
    let store = Arc::new(SerializedStore::new(Arc::new(
        pavlov_storage::FileKeyValueBackend::new(dir.path()).unwrap(),
    )));
    let ledgers = dir.path().join("modsave");
    std::fs::create_dir_all(&ledgers).unwrap();
    let rcon = registry();
    let boards = Boards::new(
        Arc::clone(&store),
        Arc::clone(&rcon),
        Some(ledgers.clone()),
        None,
        Arc::new(SystemClock),
    );
    Rig {
        _dir: dir,
        store,
        ledgers,
        rcon,
        boards,
    }
}

impl Rig {
    /// A JSON save holding `money`; anything that is not a number is written as-is (a save
    /// mid-write, say).
    fn ledger(&self, player: &str, money: &str) {
        let content = if money.parse::<i64>().is_ok() {
            format!("{{\n\t\"money\": {money},\n\t\"name\": \"{player}\"\n}}\n")
        } else {
            money.to_owned()
        };
        std::fs::write(self.ledgers.join(format!("{player}.json")), content).unwrap();
    }
}

fn description(embed: &Embed) -> &str {
    embed.description.as_deref().unwrap_or("")
}

fn footer(embed: &Embed) -> &str {
    embed.footer.as_deref().unwrap_or("")
}

fn title(embed: &Embed) -> &str {
    embed.title.as_deref().unwrap_or("")
}

fn before(text: &str, a: &str, b: &str) -> bool {
    text.find(a).unwrap() < text.find(b).unwrap()
}

#[test]
fn a_board_with_no_data_yet_still_posts_something() {
    // The arrest and cash boards replaced Node boards under the same keys; the staff board
    // has never shared its channel, so it alone may skip.
    let r = rig();
    let _ = r.boards.build_arrest_board();
    let _ = r.boards.build_cash_board();
    assert!(r.boards.build_staff_board().is_none());
}

#[test]
fn an_empty_ledger_directory_still_posts_a_board() {
    let board = rig().boards.build_cash_board();
    assert!(title(&board).contains("Richest players"));
    assert!(!title(&board).contains("Playtime"));
}

#[test]
fn the_cash_board_ranks_by_balance() {
    let r = rig();
    r.ledger("Alice", "1250");
    r.ledger("Bob", "40");
    let board = r.boards.build_cash_board();

    assert!(description(&board).contains("1,250"));
    assert!(!description(&board).contains("$1,250"));
    assert!(title(&board).contains("Richest players"));
    assert!(!title(&board).contains("Playtime"));
    assert!(before(description(&board), "Alice", "Bob"));
}

#[test]
fn the_combined_total_covers_every_ledger_not_just_the_rows_shown() {
    let r = rig();
    for i in 0..20 {
        r.ledger(&format!("p{i:02}"), "100");
    }
    let board = r.boards.build_cash_board();
    assert!(footer(&board).contains("$2,000"), "{}", footer(&board));
    assert!(footer(&board).contains("20 ledger(s)"));
}

#[test]
fn the_banlist_is_not_a_ledger() {
    let r = rig();
    // The ban list, and any other file beside the saves, is not a save.
    std::fs::write(r.ledgers.join("banlist.txt"), "0").unwrap();
    std::fs::write(r.ledgers.join("othermod.json"), "{\"enabled\": true}").unwrap();
    r.ledger("Alice", "10");
    assert!(
        !description(&r.boards.build_cash_board())
            .to_lowercase()
            .contains("banlist")
    );
}

#[test]
fn a_half_written_ledger_is_skipped_rather_than_read_as_zero() {
    let r = rig();
    r.ledger("Alice", "1000");
    r.ledger("Bob", "");
    let board = r.boards.build_cash_board();
    assert!(!description(&board).contains("Bob"));
    assert!(footer(&board).contains("1 ledger(s)"));
}

#[test]
fn an_unreadable_ledger_path_reports_the_problem_rather_than_skipping() {
    let r = rig();
    let boards = Boards::new(
        Arc::clone(&r.store),
        registry(),
        None,
        None,
        Arc::new(SystemClock),
    );
    assert!(description(&boards.build_cash_board()).contains("MODSAVE_PATH"));
}

fn arrest(player: &str, minutes: i32, bail: i32) -> Arrest {
    Arrest {
        player: player.into(),
        codes: vec!["PC 100".into()],
        jail_minutes: minutes,
        bail,
        arrested_by: "Dana".into(),
        at: Utc::now(),
    }
}

#[tokio::test]
async fn the_most_wanted_board_ranks_by_jail_time_not_arrest_count() {
    let r = rig();
    let mut arrests: IgnoreCaseMap<Vec<Arrest>> = IgnoreCaseMap::new();
    arrests.insert("Alice", vec![arrest("Alice", 120, 0)]);
    arrests.insert(
        "Bob",
        vec![
            arrest("Bob", 2, 25),
            arrest("Bob", 2, 25),
            arrest("Bob", 2, 25),
        ],
    );
    r.store.write(ARRESTS, &arrests).await.unwrap();

    assert!(before(
        description(&r.boards.build_arrest_board()),
        "Alice",
        "Bob"
    ));
}

fn action(kind: &str, moderator: &str, player: &str) -> ModAction {
    ModAction {
        action: kind.into(),
        moderator: moderator.into(),
        player: player.into(),
        reason: None,
        at: Utc::now(),
    }
}

#[tokio::test]
async fn the_staff_board_counts_actions_per_moderator() {
    let r = rig();
    let log = vec![
        action("ban", "Dana", "Alice"),
        action("kick", "Dana", "Bob"),
        action("ban", "Sam", "Carol"),
    ];
    r.store.write(MOD_LOG, &log).await.unwrap();

    let board = r.boards.build_staff_board().unwrap();
    assert!(description(&board).contains("Dana"));
    assert!(before(description(&board), "Dana", "Sam"));
}

#[test]
fn a_player_name_cannot_inject_formatting_into_a_board() {
    let r = rig();
    r.ledger("ev`il", "10");
    assert!(!description(&r.boards.build_cash_board()).contains("ev`il"));
}

#[tokio::test]
async fn playtime_ticking_with_nobody_online_writes_nothing() {
    let r = rig();
    assert!(r.rcon.confirmed_online_players().is_empty());
    r.boards.tick_playtime(TimeDelta::minutes(1)).await.unwrap();
    assert!(r.boards.playtime().is_empty());
}

fn warrant(player: &str, reason: &str, at: chrono::DateTime<Utc>) -> Warrant {
    Warrant {
        player: player.into(),
        reason: reason.into(),
        issued_by: "Officer".into(),
        at,
    }
}

#[tokio::test]
async fn the_warrant_board_lists_the_oldest_warrant_first() {
    let r = rig();
    let mut warrants: IgnoreCaseMap<Vec<Warrant>> = IgnoreCaseMap::new();
    warrants.insert(
        "fresh",
        vec![warrant(
            "Fresh",
            "speeding",
            Utc::now() - TimeDelta::minutes(5),
        )],
    );
    warrants.insert(
        "stale",
        vec![warrant(
            "Stale",
            "armed robbery",
            Utc::now() - TimeDelta::days(3),
        )],
    );
    r.store.write(WARRANTS, &warrants).await.unwrap();

    assert!(before(
        description(&r.boards.build_warrant_board()),
        "Stale",
        "Fresh"
    ));
}

#[test]
fn an_empty_warrant_board_is_an_embed_rather_than_null() {
    assert!(title(&rig().boards.build_warrant_board()).contains("No active warrants"));
}

#[tokio::test]
async fn a_player_with_several_warrants_is_listed_once() {
    let r = rig();
    let mut warrants: IgnoreCaseMap<Vec<Warrant>> = IgnoreCaseMap::new();
    warrants.insert(
        "repeat",
        vec![
            warrant("Repeat", "first", Utc::now() - TimeDelta::hours(2)),
            warrant("Repeat", "second", Utc::now() - TimeDelta::hours(1)),
        ],
    );
    r.store.write(WARRANTS, &warrants).await.unwrap();

    let board = r.boards.build_warrant_board();
    assert!(description(&board).contains("\u{d7}2"));
    assert!(description(&board).contains("second"));
}

#[tokio::test]
async fn a_player_whose_warrants_were_cleared_is_not_on_the_board() {
    let r = rig();
    let mut warrants: IgnoreCaseMap<Vec<Warrant>> = IgnoreCaseMap::new();
    warrants.insert("cleared", Vec::new());
    r.store.write(WARRANTS, &warrants).await.unwrap();

    assert!(title(&r.boards.build_warrant_board()).contains("No active warrants"));
}
