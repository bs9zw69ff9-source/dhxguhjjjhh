//! Port of `CashTableTests.cs`: the richest board's fixed-width table.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_discord::boards::{TABLE_BUDGET, cash_table};

fn table(rows: &[(&str, i64)]) -> (String, usize) {
    let rows: Vec<(String, i64)> = rows.iter().map(|(p, b)| ((*p).to_owned(), *b)).collect();
    cash_table(&rows, TABLE_BUDGET)
}

fn lines(block: &str) -> Vec<String> {
    block
        .trim_matches(|c| c == '`' || c == '\n')
        .split('\n')
        .filter(|l| !l.is_empty())
        .map(str::to_owned)
        .collect()
}

fn len(s: &str) -> usize {
    s.chars().count()
}

#[test]
fn the_columns_line_up_across_every_row() {
    let (block, _) = table(&[
        ("notgg1229mm", 7_747_661),
        ("TheonlyCrazyGrape9689", 1_442_892),
        ("ardacc1337", 1_000_207),
    ]);
    let lines = lines(&block);

    assert_eq!(lines[0], "#    Username                  Balance");
    assert_eq!(lines[1], "1    notgg1229mm             7,747,661");
    assert_eq!(lines[2], "2    TheonlyCrazyGrape9689   1,442,892");
    assert_eq!(lines[3], "3    ardacc1337              1,000,207");
    for line in &lines {
        assert_eq!(len(line.trim_end()), len(&lines[1]));
    }
    assert!(lines[0].find("Balance").unwrap() > 0);
}

#[test]
fn money_is_right_aligned_so_digits_sit_under_digits() {
    let (block, _) = table(&[("Rich", 7_747_661), ("Poor", 12)]);
    let lines = lines(&block);
    assert!(lines[1].ends_with("7,747,661"));
    assert!(lines[2].ends_with("       12"));
    assert_eq!(len(&lines[1]), len(&lines[2]));
}

#[test]
fn the_rank_column_stays_aligned_into_three_digits() {
    let rows: Vec<(String, i64)> = (1..=100)
        .map(|i| (format!("Player{i}"), 1000 - i))
        .collect();
    let lines = lines(&cash_table(&rows, TABLE_BUDGET).0);

    assert_eq!(lines.len(), 101); // header + 100
    assert!(lines[100].starts_with("100  Player100"));
    assert_eq!(len(&lines[1]), len(&lines[100]));
}

#[test]
fn a_backtick_in_a_name_cannot_break_out_of_the_block() {
    let (block, _) = table(&[("ev`il```", 10)]);
    let inner = &block[4..block.len() - 3];
    assert!(!inner.contains('`'));
}

#[test]
fn an_absurdly_long_name_is_trimmed_rather_than_setting_the_width_for_everybody() {
    let long = "x".repeat(200);
    let (block, _) = table(&[(&long, 10), ("Short", 5)]);
    for line in lines(&block) {
        assert!(len(&line) < 60, "too wide: {}", len(&line));
    }
    assert!(block.contains('…'));
}

#[test]
fn rows_are_dropped_until_the_block_fits_its_budget() {
    let rows: Vec<(String, i64)> = (1..=100).map(|i| ("n".repeat(32), i)).collect();
    let (block, shown) = cash_table(&rows, 600);
    assert!(len(&block) <= 600, "block was {}", len(&block));
    assert!(shown > 0 && shown < 100, "showed {shown}");
    assert!(block.ends_with("```"));
}

#[test]
fn a_hundred_ordinary_names_fit_in_one_block() {
    let rows: Vec<(String, i64)> = (1..=100)
        .map(|i| ("TheonlyCrazyGrape9689".to_owned(), 9_999_999 - i))
        .collect();
    let (block, shown) = cash_table(&rows, TABLE_BUDGET);
    assert_eq!(shown, 100);
    assert!(len(&block) <= TABLE_BUDGET);
}

#[test]
fn an_empty_table_is_still_a_well_formed_block() {
    let (block, shown) = cash_table(&[], TABLE_BUDGET);
    assert_eq!(shown, 0);
    assert_eq!(block, "```\n#    Username   Balance\n```");
}
