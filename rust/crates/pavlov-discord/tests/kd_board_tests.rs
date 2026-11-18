//! Port of `KdBoardTests.cs`: who ranks on the K/D board, in what order, and what each row
//! says.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::Utc;
use pavlov_core::factions::FactionRegistry;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_discord::boards::{KD_MINIMUM_KILLS as MIN, KD_ROWS, PlaytimeEntry, build_kd_board};
use pavlov_host::factions::roster::Membership;
use pavlov_host::kill_stats::PlayerKills;

fn k(player: &str, kills: i64, deaths: i64) -> PlayerKills {
    PlayerKills {
        player: player.to_owned(),
        kills,
        deaths,
        suicides: 0,
        last_at: Some(Utc::now()),
    }
}

fn board(
    kills: &[PlayerKills],
    playtime: &IgnoreCaseMap<PlaytimeEntry>,
    factions: &IgnoreCaseMap<Membership>,
) -> String {
    build_kd_board(kills, playtime, factions, Utc::now())
        .description
        .unwrap()
}

fn plain(kills: &[PlayerKills]) -> String {
    board(kills, &IgnoreCaseMap::new(), &IgnoreCaseMap::new())
}

#[test]
fn ranked_by_ratio_with_more_kills_breaking_a_tie() {
    let board = plain(&[
        k("Average", MIN * 2, MIN * 2),
        k("Sharp", MIN * 3, MIN),
        k("AlsoSharp", MIN * 6, MIN * 2),
    ]);
    assert!(board.find("AlsoSharp").unwrap() < board.find("**Sharp**").unwrap());
    assert!(board.find("**Sharp**").unwrap() < board.find("Average").unwrap());
    assert!(board.contains(&format!("**3.00** K/D ({}/{})", MIN * 6, MIN * 2)));
}

#[test]
fn too_few_kills_do_not_rank() {
    let board = plain(&[k("Lucky", MIN - 1, 0), k("Regular", MIN, 20)]);
    assert!(!board.contains("Lucky"));
    assert!(board.contains("Regular"));
}

#[test]
fn each_row_carries_faction_rank_and_playtime() {
    let gwpd = FactionRegistry::default_set()
        .get(Some("GWPD"))
        .unwrap()
        .clone();
    let rank = gwpd.order.last().unwrap().clone();
    let mut playtime = IgnoreCaseMap::new();
    playtime.insert(
        "soldier",
        PlaytimeEntry {
            player: "Soldier".into(),
            minutes: 860,
            last_seen: Some(Utc::now()),
        },
    );
    let mut factions = IgnoreCaseMap::new();
    factions.insert(
        "Soldier",
        Membership {
            faction: gwpd.name.clone(),
            player: "Soldier".into(),
            rank: rank.clone(),
        },
    );

    let board = board(
        &[k("Soldier", MIN + 50, 10), k("Drifter", MIN + 30, 15)],
        &playtime,
        &factions,
    );

    assert!(board.contains(&format!("GWPD {rank} · 14h 20m")), "{board}");
    assert!(board.contains("No faction · no playtime"));
}

#[test]
fn only_the_top_rows_are_shown() {
    let kills: Vec<PlayerKills> = (1..=KD_ROWS + 5)
        .map(|i| k(&format!("P{i:02}"), MIN + i64::try_from(i).unwrap(), 10))
        .collect();
    let board = plain(&kills);
    assert_eq!(
        board.split('\n').filter(|l| l.contains("K/D (")).count(),
        KD_ROWS
    );
}

#[test]
fn nobody_ranked_is_an_empty_board_not_nothing() {
    assert!(plain(&[]).contains("Nobody has"));
}
