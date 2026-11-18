//! Port of `CheckBanHistoryTests.cs`: which bans `/checkban` lists, in what order, and that the
//! history fits in one field.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{TimeDelta, Utc};
use pavlov_discord::commands::check_ban::ban_history;
use pavlov_host::moderation::ModAction;

fn act(action: &str, player: &str, reason: Option<&str>, days_ago: i64, by: &str) -> ModAction {
    ModAction {
        action: action.into(),
        moderator: by.into(),
        player: player.into(),
        reason: reason.map(str::to_owned),
        at: Utc::now() - TimeDelta::days(days_ago),
    }
}

fn evader() -> Vec<String> {
    vec!["Evader".into()]
}

#[test]
fn current_and_expired_bans_are_listed_newest_first() {
    let log = [
        act("tempban", "Evader", Some("griefing"), 30, "mod"),
        act("permban", "Evader", Some("cheating"), 2, "mod"),
    ];
    let history = ban_history(&log, &evader(), "Evader").unwrap();
    assert!(history.starts_with("2 ban(s) on record."));
    assert!(history.find("cheating").unwrap() < history.find("griefing").unwrap());
}

#[test]
fn a_ban_staff_lifted_early_is_not_counted() {
    let log = [
        act("tempban", "Evader", Some("griefing"), 30, "mod"),
        act("permban", "Evader", Some("mistaken ban"), 10, "mod"),
        act("unban", "Evader", Some("mistaken ban"), 9, "mod"),
    ];
    let history = ban_history(&log, &evader(), "Evader").unwrap();
    assert!(history.starts_with("1 ban(s) on record."));
    assert!(!history.contains("mistaken"));
    assert!(!history.contains("Unbanned"));
}

#[test]
fn unbanning_an_auto_ban_does_not_discount_an_earlier_staff_ban() {
    let log = [
        act("tempban", "Evader", Some("griefing"), 30, "mod"),
        act("autoban-released", "Evader", None, 28, "auto"),
        act("autoban", "Evader", Some("Ban evasion"), 5, "auto"),
        act("unban", "Evader", Some("Ban evasion"), 4, "mod"),
    ];
    let history = ban_history(&log, &evader(), "Evader").unwrap();
    assert!(history.starts_with("1 ban(s) on record."), "{history}");
    assert!(history.contains("griefing"));
}

#[test]
fn only_lifted_bans_means_no_history() {
    let log = [
        act("permban", "Evader", Some("oops"), 3, "mod"),
        act("unban", "Evader", Some("oops"), 2, "mod"),
    ];
    assert!(ban_history(&log, &evader(), "Evader").is_none());
}

#[test]
fn automated_bans_and_releases_are_left_out() {
    let log = [
        act("autoban", "Evader", Some("Ban evasion"), 3, "auto"),
        act("auto-ipban", "Evader", None, 90, "auto"),
        act("vpnban", "Evader", Some("VPN"), 4, "auto"),
        act("autoban-released", "Evader", None, 2, "auto"),
        act("unban", "Evader", Some("Ban evasion"), 1, "mod"),
    ];
    assert!(ban_history(&log, &evader(), "Evader").is_none());
}

#[test]
fn bans_under_another_name_of_the_same_account_are_shown_and_labelled() {
    let history = ban_history(
        &[act("permban", "OldName", Some("cheating"), 5, "mod")],
        &["NewName".into(), "OldName".into()],
        "NewName",
    )
    .unwrap();
    assert!(history.contains("as **OldName**"));
}

#[test]
fn other_players_and_other_actions_are_left_out() {
    let log = [
        act("permban", "Somebody", Some("x"), 1, "mod"),
        act("kick", "Evader", Some("afk"), 1, "mod"),
        act("warn", "Evader", Some("rude"), 1, "mod"),
    ];
    assert!(ban_history(&log, &evader(), "Evader").is_none());
}

#[test]
fn an_auto_ban_reason_does_not_leak_the_address() {
    let history = ban_history(
        &[act(
            "permban",
            "Evader",
            Some("alt of banned player at 203.0.113.9"),
            1,
            "mod",
        )],
        &evader(),
        "Evader",
    )
    .unwrap();
    assert!(!history.contains("203.0.113.9"));
}

#[test]
fn a_long_record_fits_in_one_discord_field() {
    let log: Vec<ModAction> = (1..=40)
        .map(|i| {
            act(
                "tempban",
                "Evader",
                Some(&"x".repeat(300)),
                i,
                &"m".repeat(32),
            )
        })
        .collect();
    let history = ban_history(&log, &evader(), "Evader").unwrap();
    assert!(history.chars().count() <= 1024);
    assert!(history.starts_with("40 ban(s) on record."));
    assert!(history.contains("older."));
}
