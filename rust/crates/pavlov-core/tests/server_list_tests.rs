//! Port of `ServerListTests.cs`: reading the Pavlov master server's list, for `/server lookup`.
//! Tested against a REAL CAPTURE of the endpoint (`rust/fixtures/pavlov-servers.json`, the C#
//! fixture copied), because the shape a parser is written against goes stale silently.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::servers::listing::{ServerListing, matching, ordered, parse};
use pavlov_core::text::case::{IgnoreCaseSet, contains_ignore_case};

const FIXTURE: &str = include_str!("../../../fixtures/pavlov-servers.json");

fn live() -> Vec<ServerListing> {
    parse(FIXTURE)
}

fn listing(name: &str, ip: &str, port: i32, players: i32) -> ServerListing {
    ServerListing {
        name: name.into(),
        ip: ip.into(),
        port,
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
fn the_real_response_parses() {
    let servers = live();

    assert!(!servers.is_empty());
    for s in &servers {
        assert!(!s.name.trim().is_empty());
        assert!(!s.ip.trim().is_empty());
        assert!(s.port > 0);
        assert!(s.max_players > 0);
        assert!(s.players >= 0);
        assert!(s.updated.is_some());
    }
}

#[test]
fn every_field_on_the_wire_is_carried_through() {
    // The point of the command is "all related info", so a field silently dropped in parsing
    // is a field the card can never show.
    let servers = live();
    let server = servers
        .iter()
        .find(|s| s.ip == "15.235.60.33" && s.port == 7005)
        .unwrap();

    assert_eq!(server.name, "[24/7] BLOOD AND IRON OFFICIAL");
    assert_eq!(server.players, 24);
    assert_eq!(server.max_players, 24);
    assert_eq!(server.map_id, "UGC5062697");
    assert_eq!(server.map_label, "BLOOD AND IRON");
    assert_eq!(server.game_mode, "CUSTOM");
    assert_eq!(server.version, "1.0.27");
    assert!(server.secured);
    assert!(!server.password_protected);
    assert!(server.full());
    assert!(server.is_workshop_map());
    assert_eq!(
        server.updated.unwrap().to_rfc3339(),
        "2026-08-01T23:30:40.509+00:00"
    );
}

#[test]
fn the_key_is_address_and_port_not_the_name() {
    // Names are operator controlled, not unique, and can exceed Discord's 100-character
    // option-value limit.
    let servers = live();
    let server = &servers[0];

    assert_eq!(server.key(), format!("{}:{}", server.ip, server.port));
    assert!(servers.iter().all(|s| s.key().chars().count() <= 100));
}

#[test]
fn malformed_input_is_empty_not_an_exception() {
    // The master server is third-party. A bad response must degrade to an empty browser.
    assert!(parse("").is_empty());
    assert!(parse("not json at all").is_empty());
    assert!(parse(r#"{"result":"Success"}"#).is_empty());
    assert!(parse(r#"{"servers":"not an array"}"#).is_empty());
    assert!(parse("[]").is_empty());
}

#[test]
fn an_unreadable_row_is_skipped_and_the_rest_survive() {
    let servers = parse(
        r#"{"servers":[
          {"name":"Good","ip":"1.2.3.4","port":7777,"slots":3,"maxSlots":24},
          {"name":"","ip":"1.2.3.5","port":7777},
          {"ip":"1.2.3.6","port":7777},
          {"name":"No address","port":7777},
          "not even an object"
        ]}"#,
    );

    assert_eq!(servers.len(), 1);
    assert_eq!(servers[0].name, "Good");
    assert_eq!(servers[0].players, 3);
}

#[test]
fn missing_fields_fall_back_rather_than_throw() {
    let server = parse(r#"{"servers":[{"name":"Bare","ip":"1.2.3.4"}]}"#).remove(0);

    assert_eq!(server.port, 0);
    assert_eq!(server.max_players, 0);
    assert_eq!(server.version, "");
    assert!(!server.password_protected);
    assert!(!server.secured);
    assert!(server.updated.is_none());
    assert!(!server.full()); // 0/0 is not full - it is unknown
}

#[test]
fn a_map_with_no_label_falls_back_to_its_id() {
    let server = parse(r#"{"servers":[{"name":"X","ip":"1.2.3.4","mapId":"UGC123"}]}"#).remove(0);

    assert_eq!(server.map_label, "UGC123");
}

// ---- ordering and filtering ----

#[test]
fn the_busiest_servers_come_first() {
    let ordered = ordered(live());

    assert!(ordered.windows(2).all(|w| w[0].players >= w[1].players));
}

#[test]
fn ordering_is_stable_by_name_within_a_player_count() {
    let ordered = ordered([
        listing("Zulu", "1.1.1.1", 1, 5),
        listing("alpha", "1.1.1.2", 1, 5),
    ]);

    assert_eq!(ordered[0].name, "alpha");
}

#[test]
fn two_rows_sharing_an_address_collapse_to_one() {
    // Discord rejects a whole message when a select carries two options with the same value.
    // The busier row is the one kept.
    let ordered = ordered([
        listing("Quiet copy", "1.1.1.1", 7777, 0),
        listing("Busy copy", "1.1.1.1", 7777, 9),
        listing("Different port", "1.1.1.1", 7778, 0),
    ]);

    assert_eq!(ordered.len(), 2);
    assert_eq!(ordered[0].name, "Busy copy");
    let mut keys = IgnoreCaseSet::new();
    assert!(ordered.iter().all(|s| keys.insert(s.key())));
}

#[test]
fn search_matches_name_map_mode_and_address() {
    let live = live();

    assert!(!matching(&live, Some("BLOOD")).is_empty());
    assert!(!matching(&live, Some("blood")).is_empty()); // case insensitive
    assert!(!matching(&live, Some("15.235.60.33")).is_empty()); // by address
    assert!(!matching(&live, Some("CUSTOM")).is_empty()); // by mode
    assert!(matching(&live, Some("definitely-not-a-server-name")).is_empty());
}

#[test]
fn an_empty_search_matches_everything() {
    let live = live();

    assert_eq!(matching(&live, None).len(), live.len());
    assert_eq!(matching(&live, Some("")).len(), live.len());
    assert_eq!(matching(&live, Some("   ")).len(), live.len());
}

#[test]
fn searching_by_map_finds_the_servers_running_it() {
    let live = live();
    let found = matching(&live, Some("Jailbreak"));

    assert!(
        found
            .iter()
            .all(|s| contains_ignore_case(&s.name, "Jailbreak")
                || contains_ignore_case(&s.map_label, "Jailbreak"))
    );
}
