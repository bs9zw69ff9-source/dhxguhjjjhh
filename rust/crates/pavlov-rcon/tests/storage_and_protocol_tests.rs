//! Port of `StorageAndProtocolTests.cs`, the `RconProtocolTests` half: Pavlov's replies are
//! inconsistent about property names and are not always JSON. Every case was observed against a
//! real server. The `FileKeyValueBackendTests` half joins a file of this name in the storage
//! crate (M4).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_rcon::reply::{capacity, names, players, server_info, successful, try_parse};
use serde_json::Value;

fn root(json: &str) -> Value {
    try_parse(Some(json)).unwrap()
}

#[test]
fn a_non_json_reply_is_rejected_rather_than_throwing() {
    assert!(try_parse(Some("Password: ")).is_none());
    assert!(try_parse(Some("")).is_none());
    assert!(try_parse(None).is_none());
}

#[test]
fn player_names_are_read_under_every_spelling_pavlov_uses() {
    let root = root(
        r#"{"PlayerList":[
          {"Username":"Alice","UniqueId":"1"},
          {"username":"Bob","uniqueId":"2"},
          {"PlayerName":"Carol","UniqueID":"3"},
          {"Name":"Dave","Id":"4"}
        ],"Successful":true}"#,
    );

    let players = players(&root);
    let names: Vec<&str> = players.iter().map(|p| p.name.as_str()).collect();
    let ids: Vec<&str> = players.iter().map(|p| p.unique_id.as_str()).collect();
    assert_eq!(names, ["Alice", "Bob", "Carol", "Dave"]);
    assert_eq!(ids, ["1", "2", "3", "4"]);
}

#[test]
fn an_entry_with_neither_name_nor_id_is_dropped() {
    let root = root(r#"{"PlayerList":[{"Username":"  "},{"Username":"Real"}],"Successful":true}"#);
    assert_eq!(names(&root), ["Real"]);
}

#[test]
fn a_missing_or_misshapen_player_list_is_empty_not_an_exception() {
    assert!(players(&root(r#"{"Successful":true}"#)).is_empty());
    assert!(players(&root(r#"{"PlayerList":"nope"}"#)).is_empty());
}

#[test]
fn successful_is_tri_state() {
    // "Did not say" is not "said no".
    assert_eq!(successful(&root(r#"{"Successful":true}"#)), Some(true));
    assert_eq!(successful(&root(r#"{"Successful":false}"#)), Some(false));
    assert_eq!(successful(&root(r#"{"PlayerList":[]}"#)), None);
}

#[test]
fn server_info_is_read_from_its_nested_key() {
    let info = server_info(&root(
        r#"{"ServerInfo":{"ServerName":"NYC RP","MapLabel":"Datacenter","GameMode":"SND","MaxPlayers":"32"},"Successful":true}"#,
    ));

    assert_eq!(info.server_name.as_deref(), Some("NYC RP"));
    assert_eq!(info.map_label.as_deref(), Some("Datacenter"));
    assert_eq!(info.game_mode.as_deref(), Some("SND"));
    assert_eq!(info.max_players, Some(32)); // delivered as a string, read as a number
}

#[test]
fn a_flat_server_info_is_also_accepted() {
    let info = server_info(&root(r#"{"ServerName":"Flat","MaxPlayers":24}"#));
    assert_eq!(info.server_name.as_deref(), Some("Flat"));
    assert_eq!(info.max_players, Some(24));
}

#[test]
fn absent_fields_are_null_never_invented() {
    let info = server_info(&root(r#"{"ServerInfo":{}}"#));
    assert_eq!(info.server_name, None);
    assert_eq!(info.map_label, None);
    assert_eq!(info.max_players, None);
}

#[test]
fn capacity_is_the_denominator_of_the_player_count() {
    let info = server_info(&root(r#"{"ServerInfo":{"PlayerCount":"2/24"}}"#));
    assert_eq!(info.max_players, Some(24));
    assert_eq!(capacity(Some("2")), None);
    assert_eq!(capacity(Some("2/0")), None);
    assert_eq!(capacity(Some(" 3 / 32 ")), Some(32));
    assert_eq!(capacity(None), None);
}
