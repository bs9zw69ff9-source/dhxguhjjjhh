//! Port of `PlayerBoardTests.cs`: a board that says "nobody online" when it has no data, and
//! one silently cut short by the 1024-character field limit on a busy server. Both read as
//! the truth to anyone looking.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::{Embed, palette};
use pavlov_core::factions::FactionRegistry;
use pavlov_core::time::SystemClock;
use pavlov_discord::boards::Boards;
use pavlov_discord::player_board::{BoardPlayer, BoardRoster, build};
use pavlov_host::factions::roster::RosterService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;

fn server(
    name: &str,
    age: Option<TimeDelta>,
    stale: bool,
    problem: Option<&str>,
    players: &[BoardPlayer],
) -> BoardRoster {
    BoardRoster {
        server: name.to_owned(),
        players: players.to_vec(),
        age,
        stale,
        problem: problem.map(str::to_owned),
    }
}

fn live(name: &str, players: &[BoardPlayer]) -> BoardRoster {
    server(name, Some(TimeDelta::seconds(5)), false, None, players)
}

fn values(embed: &Embed) -> String {
    embed
        .fields
        .iter()
        .map(|f| format!("{}\n{}", f.name, f.value))
        .collect::<Vec<_>>()
        .join("\n")
}

fn p(name: &str, faction: Option<&str>) -> BoardPlayer {
    BoardPlayer::new(name, faction)
}

#[test]
fn every_name_carries_its_faction_and_civilians_carry_none() {
    let board = build(
        &[server(
            "server1",
            Some(TimeDelta::seconds(10)),
            false,
            None,
            &[p("Alice", Some("NYPD")), p("Bob", None)],
        )],
        Utc::now(),
    )
    .unwrap();
    let text = values(&board);

    assert!(text.contains("• Alice — NYPD"));
    assert!(text.contains("• Bob"));
    assert!(!text.contains("• Bob —"));
    assert_eq!(board.title.as_deref(), Some("Live Player List"));
    assert!(board.description.as_deref().unwrap().contains("**2**"));
    assert!(text.contains("Server 1 (2)"));
}

#[test]
fn a_server_is_named_the_way_the_player_count_channels_name_it() {
    let board = build(
        &[
            live("server2", &[p("Alice", None)]),
            live("Downtown", &[p("Bob", None)]),
        ],
        Utc::now(),
    )
    .unwrap();
    let text = values(&board);
    assert!(text.contains("Server 2 (1)"));
    assert!(text.contains("Downtown (1)"));
}

#[test]
fn the_servers_sit_side_by_side() {
    let board = build(
        &[
            live("server1", &[p("Alice", Some("NYPD"))]),
            live("server2", &[p("Bob", Some("NCR"))]),
            live("server3", &[p("Carol", None)]),
        ],
        Utc::now(),
    )
    .unwrap();
    assert_eq!(board.fields.len(), 3);
    assert!(board.fields.iter().all(|f| f.inline));
}

#[test]
fn a_name_cannot_format_the_names_below_it() {
    let board = build(
        &[live(
            "server1",
            &[p("*Ghost", Some("NCR")), p("Butter_Life", None)],
        )],
        Utc::now(),
    )
    .unwrap();
    let text = values(&board);
    assert!(text.contains(r"\*Ghost"));
    assert!(text.contains(r"Butter\_Life"));
}

#[test]
fn a_full_server_says_how_many_names_it_could_not_show() {
    let crowd: Vec<BoardPlayer> = (1..=400)
        .map(|i| p(&format!("Player{i:03}"), Some("Brotherhood of Steel")))
        .collect();
    let board = build(&[live("server1", &crowd)], Utc::now()).unwrap();
    let text = values(&board);

    for field in &board.fields {
        assert!(
            field.value.chars().count() <= 1024,
            "field was {}",
            field.value.chars().count()
        );
    }
    assert!(text.contains("Server 1 (400)"));
    assert!(board.length() < 6000, "embed was {}", board.length());
    assert!(
        board.fields.len() > 1,
        "the list did not continue into another column"
    );
    assert!(text.contains("more"));
    assert_eq!(text.matches("• Player001").count(), 1);
}

#[test]
fn a_stale_roster_is_marked_and_still_lists_the_names() {
    let board = build(
        &[server(
            "server1",
            Some(TimeDelta::minutes(42)),
            true,
            None,
            &[p("Alice", Some("NYPD"))],
        )],
        Utc::now(),
    )
    .unwrap();
    let text = values(&board);
    assert!(text.contains("42 minutes ago"));
    assert!(text.contains("• Alice"));
    assert_eq!(board.color, Some(palette::AMBER));
}

#[test]
fn a_server_that_has_not_answered_is_not_reported_as_empty() {
    let board = build(
        &[
            server(
                "server1",
                None,
                false,
                Some("the reply could not be parsed"),
                &[],
            ),
            live("server2", &[p("Alice", None)]),
        ],
        Utc::now(),
    )
    .unwrap();
    let text = values(&board);
    assert!(text.contains("no roster yet"));
    assert!(text.contains("the reply could not be parsed"));
    assert!(
        board
            .description
            .as_deref()
            .unwrap()
            .contains("**1** *player ")
    );
    assert!(!text.contains("nobody online"));
}

#[test]
fn an_empty_server_says_nobody_rather_than_nothing() {
    let board = build(&[live("server1", &[])], Utc::now()).unwrap();
    assert!(values(&board).contains("nobody online"));
    assert_eq!(board.color, Some(palette::GREEN));
}

#[test]
fn the_restart_window_posts_nothing_at_all() {
    assert!(
        build(
            &[
                server("server1", None, false, None, &[]),
                server("server2", None, false, None, &[])
            ],
            Utc::now()
        )
        .is_none()
    );
}

#[test]
fn a_sweep_that_is_failing_posts_anyway_and_says_why() {
    let board = build(
        &[server(
            "server1",
            None,
            false,
            Some("RCON refused the command"),
            &[],
        )],
        Utc::now(),
    )
    .unwrap();
    assert!(values(&board).contains("RCON refused the command"));
}

// ---- end to end, from the wire and the roster files ----

struct Live {
    dir: tempfile::TempDir,
    _server: FakeRconServer,
    rcon: Arc<RconRegistry>,
}

async fn live_registry(reply: &str) -> Live {
    let dir = tempfile::tempdir().unwrap();
    let fake = FakeRconServer::start().await.unwrap();
    fake.configure(|b| b.exact_reply = Some(reply.to_owned()));
    let mut options = fake.options();
    options.name = "server1".into();
    options.command_spacing = Duration::ZERO;
    options.read_cache_duration = Duration::ZERO;
    options.command_timeout = Duration::from_secs(5);
    options.max_attempts = 1;
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    rcon.refresh_rosters().await;
    Live {
        dir,
        _server: fake,
        rcon,
    }
}

fn store(live: &Live) -> Arc<SerializedStore> {
    Arc::new(SerializedStore::new(Arc::new(
        pavlov_storage::FileKeyValueBackend::new(live.dir.path().join("data")).unwrap(),
    )))
}

fn rosters(dir: &std::path::Path) -> Arc<RosterService> {
    Arc::new(
        RosterService::new(Some(dir.to_path_buf()), dir.join("_bak"))
            .with_factions(FactionRegistry::police().clone()),
    )
}

#[tokio::test]
async fn the_tag_comes_off_the_roster_files_by_way_of_the_rcon_sweep() {
    let live = live_registry(
        "{\"Command\":\"RefreshList\",\"Successful\":true,\"PlayerList\":[\
         {\"Username\":\"Alice\",\"UniqueId\":\"1\"},\
         {\"Username\":\"Bob\",\"UniqueId\":\"2\"}]}\r\n",
    )
    .await;
    let nypd = FactionRegistry::police().get(Some("NYPD")).unwrap().clone();
    std::fs::write(
        live.dir.path().join(&nypd.rank_files[nypd.lowest()]),
        "Alice\n",
    )
    .unwrap();

    let boards = Boards::new(
        store(&live),
        Arc::clone(&live.rcon),
        None,
        Some(rosters(live.dir.path())),
        Arc::new(SystemClock),
    );
    let text = values(&boards.build_player_board().unwrap());

    assert!(text.contains(&format!("• Alice — {}", nypd.name)), "{text}");
    assert!(text.contains("• Bob"));
    assert!(!text.contains("• Bob —"));
}

#[tokio::test]
async fn no_roster_directory_leaves_the_names_untagged_rather_than_off_the_board() {
    let live = live_registry(
        "{\"Command\":\"RefreshList\",\"Successful\":true,\"PlayerList\":[\
         {\"Username\":\"Alice\",\"UniqueId\":\"1\"}]}\r\n",
    )
    .await;
    let boards = Boards::new(
        store(&live),
        Arc::clone(&live.rcon),
        None,
        None,
        Arc::new(SystemClock),
    );
    assert!(values(&boards.build_player_board().unwrap()).contains("• Alice"));
}

#[tokio::test]
async fn affiliations_agree_with_the_one_at_a_time_lookup() {
    let dir = tempfile::tempdir().unwrap();
    let rosters = rosters(dir.path());
    let nypd = FactionRegistry::police().get(Some("NYPD")).unwrap().clone();
    rosters.join(&nypd, "Alice").await;
    rosters.change_rank(&nypd, "Alice", 1).await;

    let found = rosters.find("Alice").unwrap();
    let all = rosters.affiliations();

    assert_eq!(found.faction, all.get("Alice").unwrap().faction);
    assert_eq!(found.rank, all.get("Alice").unwrap().rank);
    assert!(all.contains_key("alice"));
    assert!(!all.contains_key("Bob"));
}
