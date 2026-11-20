//! Player autocomplete's ordering and the `/staff` leaderboard. (The C# has no test file for
//! either.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::TimeDelta;
use pavlov_discord::autocomplete::{MAX_CHOICES, rank_names};
use pavlov_discord::command::ChoiceValue;
use pavlov_discord::commands::analytics::{leaderboard, window_of};

fn s(names: &[&str]) -> Vec<String> {
    names.iter().map(|n| (*n).to_owned()).collect()
}

fn values(choices: &[pavlov_discord::command::Choice]) -> Vec<String> {
    choices.iter().map(|c| c.value.to_string()).collect()
}

#[test]
fn online_comes_before_recent_before_known() {
    let choices = rank_names(&s(&["Zed"]), &s(&["Amy"]), &s(&["Bob", "zed"]), "");
    assert_eq!(
        values(&choices),
        ["Zed", "Amy", "Bob"],
        "duplicates collapse ignoring case"
    );
    assert_eq!(choices[0].name, "Zed (online)");
    assert_eq!(choices[1].name, "Amy (recent)");
    assert_eq!(choices[2].name, "Bob");
}

#[test]
fn a_prefix_match_beats_a_substring_match_even_from_a_worse_source() {
    let choices = rank_names(&s(&["Marshal"]), &[], &s(&["Alice"]), "al");
    assert_eq!(values(&choices), ["Alice", "Marshal"]);
}

#[test]
fn nothing_matched_offers_what_was_typed() {
    let choices = rank_names(&s(&["Alice"]), &[], &[], "Xavier");
    assert_eq!(choices.len(), 1);
    assert_eq!(choices[0].name, "Xavier (manual entry)");
    assert_eq!(choices[0].value, ChoiceValue::String("Xavier".into()));
}

#[test]
fn never_more_than_discord_allows() {
    let many: Vec<String> = (0..60).map(|i| format!("p{i}")).collect();
    assert_eq!(rank_names(&many, &[], &[], "").len(), MAX_CHOICES);
}

#[test]
fn an_unknown_window_reads_as_seven_days() {
    assert_eq!(window_of(Some("30D")), ("30d", TimeDelta::days(30)));
    assert_eq!(window_of(Some("forever")), ("7d", TimeDelta::days(7)));
    assert_eq!(window_of(None), ("7d", TimeDelta::days(7)));
}

#[test]
fn the_leaderboard_numbers_its_rows_and_says_what_it_is_not() {
    let embed = leaderboard("7d", &[("mod1".into(), 1_200), ("mod2".into(), 3)]);
    let body = embed.description.as_deref().unwrap();
    assert!(body.contains("` 1.` **mod1** — 1,200 action(s)"), "{body}");
    assert!(
        embed
            .footer
            .as_deref()
            .is_some_and(|f| f.contains("Automated actions excluded"))
    );
    assert!(
        leaderboard("24h", &[])
            .description
            .as_deref()
            .unwrap()
            .contains("No staff actions")
    );
}
