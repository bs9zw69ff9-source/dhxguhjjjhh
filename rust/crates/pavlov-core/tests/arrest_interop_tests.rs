//! Port of `ArrestInteropTests.cs`: arrests are read in either bot's shape and written in Node's.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashMap;

use chrono::DateTime;
use pavlov_core::police::Arrest;
use serde_json::Value;

const NODE_SHAPE: &str = r#"
{
  "playerId": "Evader",
  "charges": [
    { "code": "PC 100", "name": "Disorderly Conduct", "cls": "Misdemeanor", "min": 2, "bail": 25 },
    { "code": "PC 210", "name": "Assault", "cls": "Felony", "min": 15, "bail": 500 }
  ],
  "minutes": 17,
  "bail": 525,
  "by": "Dana",
  "at": 1785000000000
}
"#;

fn evader() -> Arrest {
    Arrest {
        player: "Evader".into(),
        codes: vec!["PC 100".into(), "PC 210".into()],
        jail_minutes: 17,
        bail: 525,
        arrested_by: "Dana".into(),
        at: DateTime::from_timestamp_millis(1_785_000_000_000).unwrap(),
    }
}

#[test]
fn the_node_shape_is_read() {
    let arrest: Arrest = serde_json::from_str(NODE_SHAPE).unwrap();
    assert_eq!(arrest, evader());
}

#[test]
fn we_write_the_node_shape() {
    let mut arrest = evader();
    arrest.codes.truncate(1);
    let root: Value =
        serde_json::from_str(&serde_json::to_string_pretty(&arrest).unwrap()).unwrap();

    assert_eq!(root["playerId"], "Evader");
    assert_eq!(root["minutes"], 17);
    assert_eq!(root["bail"], 525);
    assert_eq!(root["by"], "Dana");
    assert_eq!(root["at"], 1_785_000_000_000_i64);
    assert_eq!(root["charges"][0]["code"], "PC 100");
    // Our own names must NOT appear, or a rolled-back Node bot reads neither.
    for ours in ["jailMinutes", "arrestedBy", "codes"] {
        assert!(root.get(ours).is_none(), "{ours}");
    }
}

#[test]
fn it_round_trips_through_its_own_format() {
    let original = evader();
    let again: Arrest = serde_json::from_str(&serde_json::to_string(&original).unwrap()).unwrap();
    assert_eq!(again, original);
}

#[test]
fn a_list_of_node_arrests_survives() {
    let json = format!(r#"{{ "evader": [ {NODE_SHAPE} ] }}"#);
    let all: HashMap<String, Vec<Arrest>> = serde_json::from_str(&json).unwrap();

    assert_eq!(all["evader"].len(), 1);
    assert_eq!(all["evader"][0].jail_minutes, 17);
}

#[test]
fn a_malformed_entry_does_not_take_the_registry_with_it() {
    let arrest: Arrest = serde_json::from_str(r#"{ "nonsense": true }"#).unwrap();
    assert_eq!(arrest.player, "");
    assert_eq!(arrest.jail_minutes, 0);

    // The C# shape, with codes as strings and an ISO time, is read as well.
    let ours: Arrest = serde_json::from_str(
        r#"{"player":"X","codes":["PC 1"],"jailMinutes":3,"arrestedBy":"Y","at":"2026-01-01T00:00:00Z"}"#,
    )
    .unwrap();
    assert_eq!(ours.codes, ["PC 1"]);
    assert_eq!(ours.jail_minutes, 3);
    assert_eq!(ours.arrested_by, "Y");
}
