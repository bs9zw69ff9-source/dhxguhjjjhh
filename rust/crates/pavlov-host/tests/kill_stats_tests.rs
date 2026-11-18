//! Port of `KillStatsTests.cs`: counting kills off Pavlov.log, and the card `/stats` builds
//! from them. Most of what can go wrong is arithmetic that looks right: a suicide credited as a
//! kill, a ratio that divides by zero, a buffered count lost because a write failed.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashMap;
use std::io;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::cards::stats::{card, hours};
use pavlov_core::data::{KeyValueBackend, MemoryBackend, SerializedStore};
use pavlov_core::embed::Embed;
use pavlov_core::intelligence::{
    PlayerProfile, ProfileActivity, ProfileCombat, ProfileEconomy, ProfileFaction, ProfileIdentity,
    ProfileModeration, ProfileNetwork, RiskAssessment,
};
use pavlov_core::logs::KillEvent;
use pavlov_host::kill_stats::KillStats;

fn now() -> DateTime<Utc> {
    DateTime::UNIX_EPOCH + TimeDelta::days(56 * 365)
}

fn kill(killer: Option<&str>, killed: &str, by: Option<&str>) -> KillEvent {
    KillEvent {
        killer: killer.map(Into::into),
        killed: Some(killed.into()),
        killed_by: by.map(Into::into),
    }
}

fn stats() -> (Arc<MemoryBackend>, KillStats) {
    let backend = Arc::new(MemoryBackend::new());
    let store = Arc::new(SerializedStore::new(Arc::clone(&backend) as _));
    (backend, KillStats::new(store))
}

#[test]
fn a_kill_is_one_kill_and_one_death() {
    let (_, s) = stats();
    s.record(&kill(Some("Winner"), "Loser", Some("AK47")), now());
    assert_eq!(s.of("Winner").kills, 1);
    assert_eq!(s.of("Winner").deaths, 0);
    assert_eq!(s.of("Loser").deaths, 1);
    assert_eq!(s.of("Loser").kills, 0);
}

#[test]
fn a_suicide_is_a_death_and_never_a_kill() {
    // Counting it as a kill means a ratio anybody can farm with a grenade.
    let (_, s) = stats();
    s.record(&kill(Some("Clumsy"), "Clumsy", Some("grenade")), now());
    let clumsy = s.of("Clumsy");
    assert_eq!(clumsy.kills, 0);
    assert_eq!(clumsy.deaths, 1);
    assert_eq!(clumsy.suicides, 1);
}

#[test]
fn a_fall_is_a_death_with_nobody_credited() {
    let (_, s) = stats();
    s.record(&kill(None, "Clumsy", None), now());
    assert_eq!(s.of("Clumsy").deaths, 1);
    assert_eq!(s.of("Clumsy").kills, 0);
}

#[test]
fn names_are_matched_the_way_every_other_name_lookup_is() {
    let (_, s) = stats();
    s.record(&kill(Some("Winner"), "Loser", Some("AK47")), now());
    assert_eq!(s.of("winner").kills, 1);
}

#[test]
fn a_ratio_with_no_deaths_is_the_kill_count_rather_than_infinity() {
    let (_, s) = stats();
    for victim in ["A", "B", "C"] {
        s.record(&kill(Some("Untouchable"), victim, Some("AK47")), now());
    }
    assert!((s.of("Untouchable").ratio() - 3.0).abs() < f64::EPSILON);
}

#[tokio::test]
async fn the_buffer_is_written_once_rather_than_per_kill() {
    // THE REASON THIS BUFFERS AT ALL: the store rewrites the whole dataset per update.
    let (backend, s) = stats();
    for i in 0..50 {
        s.record(
            &kill(Some("Winner"), &format!("Loser{i}"), Some("AK47")),
            now(),
        );
    }
    assert_eq!(backend.writes(), 0);

    s.flush().await.unwrap();
    assert_eq!(backend.writes(), 1);
    assert_eq!(s.of("Winner").kills, 50);
}

#[tokio::test]
async fn a_flushed_total_survives_and_keeps_accumulating() {
    let (_, s) = stats();
    s.record(&kill(Some("Winner"), "Loser", Some("AK47")), now());
    s.flush().await.unwrap();

    let later = now() + TimeDelta::minutes(1);
    s.record(&kill(Some("Winner"), "Loser", Some("AK47")), later);
    // The unflushed kill is included, so a check straight after a firefight is current.
    assert_eq!(s.of("Winner").kills, 2);

    s.flush().await.unwrap();
    assert_eq!(s.of("Winner").kills, 2);
    assert_eq!(s.of("Winner").last_at, Some(later));
}

#[tokio::test]
async fn a_flush_with_nothing_buffered_writes_nothing() {
    let (backend, s) = stats();
    s.flush().await.unwrap();
    assert_eq!(backend.writes(), 0);
}

#[derive(Default)]
struct ThrowingBackend {
    data: Mutex<HashMap<String, String>>,
    healthy: AtomicBool,
    writes: AtomicUsize,
}

impl KeyValueBackend for ThrowingBackend {
    fn read(&self, key: &str) -> io::Result<Option<String>> {
        Ok(self.data.lock().unwrap().get(key).cloned())
    }

    fn write(&self, key: &str, json: &str) -> io::Result<()> {
        if !self.healthy.load(Ordering::SeqCst) {
            return Err(io::Error::other("the disk is full"));
        }
        self.writes.fetch_add(1, Ordering::SeqCst);
        self.data.lock().unwrap().insert(key.into(), json.into());
        Ok(())
    }
}

#[tokio::test]
async fn a_failed_write_holds_the_kills_for_the_next_flush() {
    // Dropping the buffer on failure loses those kills permanently and silently.
    let backend = Arc::new(ThrowingBackend::default());
    let s = KillStats::new(Arc::new(SerializedStore::new(Arc::clone(&backend) as _)));

    s.record(&kill(Some("Winner"), "Loser", Some("AK47")), now());
    assert!(s.flush().await.is_err());
    assert_eq!(s.of("Winner").kills, 1);

    backend.healthy.store(true, Ordering::SeqCst);
    s.flush().await.unwrap();
    assert_eq!(s.of("Winner").kills, 1);
    assert_eq!(backend.writes.load(Ordering::SeqCst), 1);
}

// ---- the card ----

struct P {
    combat: Option<ProfileCombat>,
    faction: Option<&'static str>,
    rank: Option<&'static str>,
}

fn profile(p: P) -> PlayerProfile {
    PlayerProfile {
        identity: ProfileIdentity {
            current: "Courier6".into(),
            account_id: Some("0002abc".into()),
            previous_names: Vec::new(),
        },
        activity: ProfileActivity {
            first_seen: Some(DateTime::UNIX_EPOCH + TimeDelta::days(55 * 365)),
            last_seen: Some(now()),
            playtime_minutes: 125,
            online: false,
            servers: Vec::new(),
        },
        network: ProfileNetwork::default(),
        moderation: ProfileModeration::default(),
        faction: ProfileFaction {
            name: p.faction.map(Into::into),
            rank: p.rank.map(Into::into),
            discord_id: None,
        },
        economy: ProfileEconomy {
            balance: Some(1250),
            owed_wages: 0,
            lifetime_wages: 0,
        },
        associations: Vec::new(),
        risk: RiskAssessment::clean(),
        redacted: false,
        combat: p.combat,
    }
}

fn text(embed: &Embed) -> String {
    let fields: Vec<String> = embed
        .fields
        .iter()
        .map(|f| format!("{}: {}", f.name, f.value))
        .collect();
    format!(
        "{}\n{}",
        embed.description.clone().unwrap_or_default(),
        fields.join("\n")
    )
}

#[test]
fn the_card_shows_the_faction_rank_playtime_and_ratio() {
    let combat = ProfileCombat {
        kills: 10,
        deaths: 5,
        suicides: 1,
        last_at: Some(now()),
    };
    let t = text(&card(&profile(P {
        combat: Some(combat),
        faction: Some("GWPD"),
        rank: Some("Sergeant"),
    })));
    for expected in [
        "GWPD", "Sergeant", "2h 5m", "2.00", "10 / 5", "(1 self)", "$1,250",
    ] {
        assert!(t.contains(expected), "missing {expected} in {t}");
    }
}

#[test]
fn a_player_with_no_kills_is_not_shown_a_zero_ratio() {
    // "0.00" reads as a judgement on somebody who simply has not been counted yet.
    let embed = card(&profile(P {
        combat: Some(ProfileCombat::default()),
        faction: Some("GWPD"),
        rank: None,
    }));
    let kd: Vec<_> = embed.fields.iter().filter(|f| f.name == "K/D").collect();
    assert_eq!(kd.len(), 1);
    assert!(kd[0].value.contains("nothing recorded"));
}

#[test]
fn an_unaffiliated_player_says_so_rather_than_showing_nothing() {
    let embed = card(&profile(P {
        combat: None,
        faction: None,
        rank: None,
    }));
    assert!(embed.description.unwrap().contains("unaffiliated"));
}

#[test]
fn a_player_the_bot_has_never_seen_is_told_so() {
    // A wall of zeroes reads as "terrible", not "never heard of them".
    let stranger = PlayerProfile {
        identity: ProfileIdentity::unknown("WhoIsThis"),
        activity: ProfileActivity {
            first_seen: None,
            last_seen: None,
            playtime_minutes: 0,
            online: false,
            servers: Vec::new(),
        },
        economy: ProfileEconomy::default(),
        faction: ProfileFaction::default(),
        ..profile(P {
            combat: None,
            faction: None,
            rank: None,
        })
    };
    let embed = card(&stranger);
    assert!(embed.description.unwrap().contains("Never seen"));
    assert!(embed.fields.is_empty());
}

#[test]
fn playtime_reads_as_hours_and_minutes() {
    assert_eq!(hours(45), "45m");
    assert_eq!(hours(125), "2h 5m");
    assert_eq!(hours(60_000), "1,000h 0m");
}
