//! `/stats`'s card and `/feeds`'s report. (The C# has no test file for either.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::Utc;
use indexmap::IndexMap;
use pavlov_core::intelligence::{
    PlayerProfile, ProfileActivity, ProfileCombat, ProfileEconomy, ProfileFaction, ProfileIdentity,
    ProfileModeration, ProfileNetwork, RiskAssessment,
};
use pavlov_discord::commands::feeds::{FEEDS, report};
use pavlov_discord::commands::stats::{card, hours};

fn profile(minutes: i64, combat: Option<ProfileCombat>) -> PlayerProfile {
    PlayerProfile {
        identity: ProfileIdentity::unknown("Alice"),
        activity: ProfileActivity {
            first_seen: None,
            last_seen: Some(Utc::now()),
            playtime_minutes: minutes,
            online: true,
            servers: vec!["server1".into()],
        },
        network: ProfileNetwork::default(),
        moderation: ProfileModeration::default(),
        faction: ProfileFaction {
            name: Some("LSPD".into()),
            rank: Some("Sergeant".into()),
            discord_id: None,
        },
        economy: ProfileEconomy {
            balance: Some(1_250),
            ..ProfileEconomy::default()
        },
        associations: Vec::new(),
        risk: RiskAssessment::clean(),
        redacted: false,
        combat,
    }
}

#[test]
fn playtime_reads_in_hours_and_minutes() {
    assert_eq!(hours(45), "45m");
    assert_eq!(hours(75), "1h 15m");
    assert_eq!(hours(60 * 1500 + 2), "1,500h 2m");
}

#[test]
fn a_stranger_is_told_they_were_never_seen() {
    let mut nobody = profile(0, None);
    nobody.activity.last_seen = None;
    let embed = card(&nobody);
    assert!(embed.description.as_deref().unwrap().contains("Never seen"));
}

#[test]
fn the_card_shows_faction_kd_money_and_where_they_are() {
    let embed = card(&profile(
        75,
        Some(ProfileCombat {
            kills: 30,
            deaths: 12,
            suicides: 2,
            last_at: None,
        }),
    ));
    assert_eq!(embed.description.as_deref(), Some("**LSPD** — Sergeant"));
    assert_eq!(embed.field_named("K/D").unwrap().value, "**2.50**");
    assert_eq!(
        embed.field_named("Kills / deaths").unwrap().value,
        "30 / 12  (2 self)"
    );
    assert_eq!(embed.field_named("Playtime").unwrap().value, "1h 15m");
    assert!(
        embed
            .field_named("Status")
            .unwrap()
            .value
            .contains("online now on server1")
    );
    assert!(embed.fields.iter().any(|f| f.value == "$1,250"));
}

#[test]
fn no_combat_is_nothing_recorded_not_a_zero_ratio() {
    let embed = card(&profile(5, None));
    assert_eq!(
        embed.field_named("K/D").unwrap().value,
        "*nothing recorded*"
    );
}

#[test]
fn every_feed_is_reported_with_its_state() {
    let mut status = IndexMap::new();
    status.insert("join".to_owned(), "delivering (3 sent)".to_owned());
    status.insert("connect".to_owned(), "not configured".to_owned());
    status.insert("kill".to_owned(), "failing: 404".to_owned());
    let embed = report(&status, false);
    let body = embed.description.as_deref().unwrap();
    for (label, variable, _) in FEEDS {
        assert!(
            body.contains(&format!("**{label}** — `{variable}`")),
            "{label}"
        );
    }
    assert!(
        body.contains("not registered"),
        "rcon and staff were never registered"
    );
    assert!(
        embed
            .footer
            .as_deref()
            .is_some_and(|f| f.contains("/feeds test:true"))
    );
    assert!(report(&status, true).field_named("Test sent").is_some());
}
