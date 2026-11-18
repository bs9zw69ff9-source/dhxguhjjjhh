//! Port of the host half of `StatsLogTests.cs` (`StatsKillNameTests`, `StatsKillScoreTests`):
//! a real tailed Stats.log through [`StatsLogService`], into names and into K/D. The reader's
//! parsing half is in `pavlov-core/tests/stats_log_tests.rs`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashMap;
use std::io::Write;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use chrono::{TimeZone, Utc};
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::logs::{KillEvent, StatsKill};
use pavlov_host::kill_stats::KillStats;
use pavlov_host::logs::{LogTailer, NameResolver, StatsLogService, stats_log_service};
use pavlov_host::metrics::MetricsRegistry;

struct Fixture {
    _dir: tempfile::TempDir,
    path: PathBuf,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("Stats.log");
        std::fs::write(&path, "").unwrap();
        Self { _dir: dir, path }
    }

    fn service(&self, resolve: Option<NameResolver>) -> StatsLogService {
        StatsLogService::new(
            vec![self.path.to_str().unwrap().to_owned()],
            Arc::new(LogTailer::default()),
            Arc::new(MetricsRegistry::default()),
            resolve,
        )
    }

    fn append(&self, text: &str) {
        std::fs::OpenOptions::new()
            .append(true)
            .open(&self.path)
            .unwrap()
            .write_all(text.as_bytes())
            .unwrap();
    }

    /// The tailer positions at the END on its first pass, so the content arrives after it -
    /// which is also how a live server writes it.
    async fn run(&self, service: &StatsLogService, contents: &str) {
        service.tick().await;
        self.append(contents);
        service.tick().await;
    }
}

fn block(killer: &str, killed: &str, weapon: &str, headshot: bool) -> String {
    format!(
        "[2026.09.12-03.50.32] StatManagerLog: {{\n\t\"KillData\":\n\t{{\n\t\t\"Killer\": \"{killer}\",\n\t\t\"Killed\": \"{killed}\",\n\t\t\"KilledBy\": \"{weapon}\",\n\t\t\"Headshot\": {headshot}\n\t}}\n}}\n"
    )
}

fn name_block(killer: &str, killed: &str) -> String {
    block(killer, killed, "lightmachinegun", true)
}

async fn read(contents: &str, resolve: Option<NameResolver>) -> Vec<StatsKill> {
    let f = Fixture::new();
    let service = f.service(resolve);
    let seen = Arc::new(Mutex::new(Vec::new()));
    let sink = Arc::clone(&seen);
    service.killed.subscribe(move |kill| {
        sink.lock().unwrap().push(kill);
        Box::pin(async {})
    });
    f.run(&service, contents).await;
    seen.lock().unwrap().clone()
}

const ID_A: &str = "27475864022060194";
const ID_B: &str = "9096592833787397";

#[tokio::test]
async fn a_unique_id_becomes_the_display_name() {
    // THE BUG: the feed read "27475864022060194 -> 9096592833787397".
    let names: HashMap<&str, &str> = [(ID_A, "Holosight1"), (ID_B, "LxPXHam")].into();
    let resolve: NameResolver = Arc::new(move |id| Ok(names.get(id).map(|n| (*n).to_owned())));

    let kills = read(&name_block(ID_A, ID_B), Some(resolve)).await;

    assert_eq!(kills.len(), 1);
    assert_eq!(kills[0].killer, "Holosight1");
    assert_eq!(kills[0].killed, "LxPXHam");
    assert!(kills[0].headshot);
}

#[tokio::test]
async fn an_id_nobody_can_name_is_left_as_the_id() {
    // NOT "unknown": the id is still the only handle anybody has on that player.
    let kills = read(&name_block(ID_A, ID_B), Some(Arc::new(|_| Ok(None)))).await;

    assert_eq!(kills.len(), 1);
    assert_eq!(kills[0].killer, ID_A);
    assert_eq!(kills[0].killed, ID_B);
}

#[tokio::test]
async fn a_field_that_already_holds_a_name_is_passed_through() {
    let kills = read(
        &name_block("Holosight1", "LxPXHam"),
        Some(Arc::new(|_| Ok(None))),
    )
    .await;

    assert_eq!(kills[0].killer, "Holosight1");
    assert_eq!(kills[0].killed, "LxPXHam");
}

#[tokio::test]
async fn a_resolver_that_throws_does_not_cost_the_kill() {
    let kills = read(
        &name_block(ID_A, ID_B),
        Some(Arc::new(|_| Err("rcon down".to_owned()))),
    )
    .await;

    assert_eq!(kills.len(), 1);
    assert_eq!(kills[0].killer, ID_A);
}

#[tokio::test]
async fn with_no_resolver_the_raw_fields_survive() {
    let kills = read(&name_block(ID_A, ID_B), None).await;
    assert_eq!(kills[0].killer, ID_A);
}

/// Exactly what `FeedBridge::on_stats_kill` records: a `KillEvent` stamped with the GAME'S time.
async fn score(blocks: &[String]) -> KillStats {
    let f = Fixture::new();
    let service = f.service(None);
    let stats = Arc::new(KillStats::new(Arc::new(SerializedStore::new(Arc::new(
        MemoryBackend::new(),
    )))));
    let sink = Arc::clone(&stats);
    service.killed.subscribe(move |kill: StatsKill| {
        sink.record(
            &KillEvent {
                killer: Some(kill.killer.clone()),
                killed: Some(kill.killed.clone()),
                killed_by: kill.weapon.clone(),
            },
            kill.at,
        );
        Box::pin(async {})
    });
    f.run(&service, &blocks.concat()).await;
    drop(service);
    Arc::try_unwrap(stats).unwrap()
}

#[tokio::test]
async fn a_stats_kill_is_counted_for_both_the_killer_and_the_victim() {
    let score = score(&[block("Winner", "Loser", "ak47", false)]).await;

    assert_eq!(score.of("Winner").kills, 1);
    assert_eq!(score.of("Winner").deaths, 0);
    assert_eq!(score.of("Loser").deaths, 1);
    assert_eq!(score.of("Loser").kills, 0);
}

#[tokio::test]
async fn the_kill_carries_the_games_own_timestamp() {
    // Not the clock at read time: the whole point of Stats.log over the Pavlov.log scrape.
    let score = score(&[block("Winner", "Loser", "ak47", false)]).await;

    assert_eq!(
        score.of("Winner").last_at,
        Some(Utc.with_ymd_and_hms(2026, 9, 12, 3, 50, 32).unwrap())
    );
}

#[tokio::test]
async fn many_kills_accumulate() {
    let score = score(&[
        block("Winner", "A", "ak47", false),
        block("Winner", "B", "ak47", false),
        block("Winner", "C", "ak47", false),
        block("Rival", "Winner", "ak47", false),
    ])
    .await;

    assert_eq!(score.of("Winner").kills, 3);
    assert_eq!(score.of("Winner").deaths, 1);
    assert!((score.of("Winner").ratio() - 3.0).abs() < f64::EPSILON);
}

#[test]
fn the_stats_log_is_found_beside_each_pavlov_log() {
    // Rust-only: Discover derives Stats.log from the install root, and drops what is missing.
    let dir = tempfile::tempdir().unwrap();
    let root = dir.path().join("pavlovserver");
    let stats = root.join("Pavlov/Saved/Stats/Stats.log");
    std::fs::create_dir_all(stats.parent().unwrap()).unwrap();
    std::fs::write(&stats, "").unwrap();
    let logs = [
        root.join("Pavlov/Saved/Logs/Pavlov.log")
            .to_str()
            .unwrap()
            .to_owned(),
        dir.path()
            .join("other/Pavlov/Saved/Logs/Pavlov.log")
            .to_str()
            .unwrap()
            .to_owned(),
    ];

    assert_eq!(
        stats_log_service::discover(None, &logs),
        [stats.to_str().unwrap()]
    );
    assert!(stats_log_service::discover(Some("/no/such/Stats.log"), &logs).is_empty());
}
