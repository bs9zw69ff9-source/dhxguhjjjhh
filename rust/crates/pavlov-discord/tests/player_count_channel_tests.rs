//! Port of `PlayerCountChannelTests.cs`: the exact names (a channel name is the kind of thing a
//! later edit quietly reformats) and the decision NOT to write an unchanged or untrue one,
//! which is what keeps the feature inside two renames per ten minutes.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_config::Secret;
use pavlov_core::servers::listing::ServerListing;
use pavlov_core::servers::units::default_units;
use pavlov_core::time::SystemClock;
use pavlov_discord::player_count::{
    ChannelRenamer, PlayerCountChannels, RenameOutcome, Targets, missing_half, resolve_capacity,
    server_name, total_name, total_players,
};
use pavlov_host::http::{HttpClient, HttpError, HttpRequest, HttpResponse};
use pavlov_host::master_list::MasterServerList;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;
use pavlov_host::service_control::{UnitAction, UnitControl, UnitResult, UnitState};
use pavlov_rcon::RconOptions;
use pavlov_rcon::reply::capacity;

/// Records renames, and reports whatever name was last written.
#[derive(Default)]
struct FakeChannels {
    names: Mutex<HashMap<u64, String>>,
    renames: Mutex<Vec<(u64, String)>>,
    name_reads: Mutex<usize>,
    outcome: Option<RenameOutcome>,
}

impl FakeChannels {
    fn with(existing: &[(u64, &str)]) -> Self {
        let channels = Self::default();
        for (id, name) in existing {
            channels
                .names
                .lock()
                .unwrap()
                .insert(*id, (*name).to_owned());
        }
        channels
    }

    fn outcome(mut self, outcome: RenameOutcome) -> Self {
        self.outcome = Some(outcome);
        self
    }

    fn renames(&self) -> Vec<(u64, String)> {
        self.renames.lock().unwrap().clone()
    }
}

impl ChannelRenamer for FakeChannels {
    fn name_of(&self, channel_id: u64) -> BoxFuture<'_, Option<String>> {
        *self.name_reads.lock().unwrap() += 1;
        let name = self.names.lock().unwrap().get(&channel_id).cloned();
        async move { name }.boxed()
    }

    fn rename<'a>(&'a self, channel_id: u64, name: &'a str) -> BoxFuture<'a, RenameOutcome> {
        self.renames
            .lock()
            .unwrap()
            .push((channel_id, name.to_owned()));
        let outcome = self.outcome.unwrap_or(RenameOutcome::Renamed);
        // Only a successful rename changes the name.
        if outcome == RenameOutcome::Renamed {
            self.names
                .lock()
                .unwrap()
                .insert(channel_id, name.to_owned());
        }
        async move { outcome }.boxed()
    }
}

/// A unit list the shape of the channel list, and a state that does not depend on whether
/// this machine runs systemd (the reason the C# stopped using a real `ServiceControl` here).
struct StubUnits {
    units: Vec<String>,
    state: UnitState,
}

impl UnitControl for StubUnits {
    fn units(&self) -> &[String] {
        &self.units
    }
    fn state<'a>(&'a self, _: &'a str) -> BoxFuture<'a, UnitState> {
        let state = self.state;
        async move { state }.boxed()
    }
    fn run<'a>(&'a self, unit: &'a str, _: UnitAction) -> BoxFuture<'a, UnitResult> {
        async move { UnitResult::new(false, unit, "not in a test") }.boxed()
    }
}

/// The master list is unreachable here.
struct NoNetwork;

impl HttpClient for NoNetwork {
    fn send(&self, _: HttpRequest) -> BoxFuture<'_, Result<HttpResponse, HttpError>> {
        async { Err(HttpError::Transport("no network in tests".into())) }.boxed()
    }
}

fn build(channels: Arc<FakeChannels>, state: UnitState) -> PlayerCountChannels {
    let mut options = RconOptions::new("server1", "127.0.0.1", 9, Secret::new("x"));
    options.max_attempts = 1;
    options.command_timeout = Duration::from_millis(500);
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    let master = Arc::new(MasterServerList::new(
        Arc::new(NoNetwork),
        None,
        Arc::new(SystemClock),
    ));
    let services = Arc::new(StubUnits {
        units: default_units(),
        state,
    });
    PlayerCountChannels::new(channels, rcon, master, services, 24)
}

fn targets(servers: &[u64], total: Option<u64>) -> Targets {
    Targets {
        server_channels: servers.to_vec(),
        total_channel: total,
    }
}

// ---- the names ----

#[test]
fn a_server_channel_is_named_count_over_capacity() {
    assert_eq!(server_name(1, 3, 24), "Server 1: 3/24");
    assert_eq!(server_name(2, 0, 24), "Server 2: 0/24");
    assert_eq!(server_name(3, 24, 24), "Server 3: 24/24");
}

#[test]
fn a_denominator_is_always_shown() {
    assert_eq!(server_name(1, 0, 24), "Server 1: 0/24");
    assert_eq!(server_name(2, 3, 32), "Server 2: 3/32");
}

#[test]
fn an_empty_server_still_shows_its_capacity() {
    assert_eq!(server_name(1, 0, 24), "Server 1: 0/24");
}

#[test]
fn a_non_standard_capacity_is_shown_as_itself() {
    assert_eq!(server_name(2, 5, 32), "Server 2: 5/32");
    assert_eq!(server_name(3, 0, 100), "Server 3: 0/100");
}

// ---- which capacity is shown ----

#[test]
fn a_live_capacity_always_wins() {
    assert_eq!(resolve_capacity(Some(32), Some(24), 24), 32);
    assert_eq!(resolve_capacity(Some(16), Some(100), 24), 16);
}

#[test]
fn the_last_known_capacity_covers_a_single_failed_read() {
    assert_eq!(resolve_capacity(None, Some(24), 99), 24);
    assert_eq!(resolve_capacity(None, Some(32), 24), 32);
}

#[test]
fn only_a_server_that_has_never_answered_falls_back() {
    assert_eq!(resolve_capacity(None, None, 24), 24);
    assert_eq!(resolve_capacity(None, None, 64), 64);
}

#[test]
fn zero_is_not_reported_it_is_a_failed_parse() {
    assert_eq!(resolve_capacity(Some(0), None, 24), 24);
    assert_eq!(resolve_capacity(Some(0), Some(0), 24), 24);
    assert_eq!(resolve_capacity(Some(-1), None, 24), 24);
}

#[test]
fn a_misconfigured_fallback_still_yields_a_usable_name() {
    assert_eq!(resolve_capacity(None, None, 0), 24);
}

#[test]
fn the_channel_name_can_no_longer_lose_its_denominator() {
    for live in [None, Some(0), Some(-1), Some(24), Some(32)] {
        for known in [None, Some(0), Some(24)] {
            let name = server_name(1, 0, resolve_capacity(live, known, 24));
            assert!(name.contains('/'));
            assert!(!name.contains("/0"));
        }
    }
}

#[test]
fn the_total_is_a_count_with_no_denominator() {
    assert_eq!(total_name(287), "Pavlov Shack: 287");
    assert_eq!(total_name(0), "Pavlov Shack: 0");
}

#[test]
fn no_name_carries_an_emoji() {
    for name in [
        server_name(1, 3, 24),
        server_name(9, 0, 24),
        total_name(287),
    ] {
        assert!(name.is_ascii(), "\"{name}\" contains a non-ASCII character");
    }
}

#[test]
fn the_capacity_comes_from_pavlovs_player_count_string() {
    assert_eq!(capacity(Some("2/24")), Some(24));
    assert_eq!(capacity(Some("0/10")), Some(10));
    assert_eq!(capacity(Some(" 2 / 24 ")), Some(24));
}

#[test]
fn an_unreadable_capacity_is_null_rather_than_invented() {
    for raw in [None, Some("2"), Some("2/"), Some("2/abc"), Some("2/0")] {
        assert_eq!(capacity(raw), None, "{raw:?}");
    }
}

fn listing(name: &str, players: i32) -> ServerListing {
    ServerListing {
        name: name.into(),
        ip: "1.1.1.1".into(),
        port: 1,
        players,
        max_players: 24,
        map_id: "m".into(),
        map_label: "m".into(),
        game_mode: "TDM".into(),
        game_mode_label: "TDM".into(),
        version: "1".into(),
        password_protected: false,
        secured: true,
        updated: None,
    }
}

#[test]
fn the_total_sums_every_listed_server() {
    assert_eq!(
        total_players(&[listing("a", 5), listing("b", 0), listing("c", 12)]),
        17
    );
    assert_eq!(total_players(&[]), 0);
}

// ---- the rate limit ----

#[tokio::test]
async fn a_stale_roster_does_not_overwrite_the_name() {
    // Never fetched, the state right after a restart, and the unit's state unknown.
    let channels = Arc::new(FakeChannels::with(&[(100, "Server 1: 5/24")]));
    build(Arc::clone(&channels), UnitState::Unknown)
        .tick(&targets(&[100], None))
        .await;
    assert!(channels.renames().is_empty());
}

#[tokio::test]
async fn a_channel_the_bot_cannot_see_is_reported_rather_than_retried_blindly() {
    let channels = Arc::new(FakeChannels::default());
    build(Arc::clone(&channels), UnitState::Unknown)
        .tick(&targets(&[], Some(999)))
        .await;
    assert!(channels.renames().is_empty());
}

#[tokio::test]
async fn more_channels_than_servers_is_not_an_error() {
    let channels = Arc::new(FakeChannels::with(&[
        (100, "Server 1: 0/24"),
        (101, "Server 2: 0/24"),
        (102, "Server 3: 0/24"),
    ]));
    build(Arc::clone(&channels), UnitState::Unknown)
        .tick(&targets(&[100, 101, 102], None))
        .await;
    assert!(channels.renames().is_empty());
}

#[tokio::test]
async fn no_channels_configured_does_nothing() {
    let channels = Arc::new(FakeChannels::default());
    build(Arc::clone(&channels), UnitState::Unknown)
        .tick(&targets(&[], None))
        .await;
    assert!(channels.renames().is_empty());
    assert_eq!(*channels.name_reads.lock().unwrap(), 0);
}

#[tokio::test]
async fn the_run_reports_one_line_per_channel() {
    let channels = Arc::new(FakeChannels::with(&[(100, "Server 1: 0/24")]));
    let report = build(channels, UnitState::Unknown)
        .run(&targets(&[100], Some(999)))
        .await;
    assert_eq!(report.len(), 2);
    assert!(report.iter().all(|l| !l.trim().is_empty()));
}

#[tokio::test]
async fn a_channel_the_bot_cannot_see_says_so_in_the_report() {
    // The master list is unreachable here, so the total is skipped before the channel is read;
    // a per-server channel the bot cannot see reaches the same words.
    let report = build(Arc::new(FakeChannels::default()), UnitState::Inactive)
        .run(&targets(&[100], Some(999)))
        .await;
    assert!(report.iter().any(|l| l.contains("FAILED")), "{report:?}");
    assert!(
        report.iter().any(|l| l.contains("not visible")),
        "{report:?}"
    );
}

#[tokio::test]
async fn a_rate_limited_rename_is_reported_rather_than_hidden() {
    let channels = Arc::new(
        FakeChannels::with(&[(100, "Server 1: 99/24")]).outcome(RenameOutcome::RateLimited),
    );
    let report = build(channels, UnitState::Inactive)
        .run(&targets(&[100], None))
        .await;
    assert!(report.iter().any(|l| l.contains("RATE LIMITED")));
    // NOT "FAILED": /counts keys its permission hint off that word.
    assert!(!report.iter().any(|l| l.contains("FAILED")));
}

#[tokio::test]
async fn a_missing_permission_still_reads_as_a_failure() {
    let channels =
        Arc::new(FakeChannels::with(&[(100, "Server 1: 99/24")]).outcome(RenameOutcome::Forbidden));
    let report = build(channels, UnitState::Inactive)
        .run(&targets(&[100], None))
        .await;
    assert!(report.iter().any(|l| l.contains("Manage Channels")));
    assert!(!report.iter().any(|l| l.contains("RATE LIMITED")));
}

// ---- half-configured is its own state ----

#[test]
fn no_per_server_channels_is_called_out_by_name() {
    let (_, body) = missing_half(&targets(&[], Some(999))).unwrap();
    assert!(body.contains("PLAYER_COUNT_CHANNELS"));
    assert!(body.contains("in server order"), "{body}");
}

#[test]
fn no_total_channel_is_called_out_too() {
    let (_, body) = missing_half(&targets(&[100], None)).unwrap();
    assert!(body.contains("SHACK_TOTAL_CHANNEL"));
}

#[test]
fn fully_configured_says_nothing() {
    assert!(missing_half(&targets(&[100], Some(999))).is_none());
}
