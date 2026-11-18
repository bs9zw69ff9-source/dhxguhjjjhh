//! Port of `NodeCompatibilityTests.cs`: the JSON the Node bot wrote is the JSON on the live
//! server, so reading it - and writing what Node can read back - is a live contract.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use chrono::{DateTime, TimeZone, Utc};
use pavlov_core::data::{KeyValueBackend, MemoryBackend, SerializedStore};
use pavlov_core::moderation::{BanRecord, BanRules};

const TEMPBANS: &str = "tempbans";
const NODE_BAN_LIST: &str = r#"
    [{"playerId":"Alice","reason":"Cheating","moderator":"Dana","at":1753900000000,"expires":1754500000000,"durationLabel":"7d"},
     {"playerId":"Bob","reason":"Griefing","moderator":"Sam","at":1753900000000,"permanent":true}]
    "#;

fn ms(n: i64) -> DateTime<Utc> {
    DateTime::from_timestamp_millis(n).unwrap()
}

#[test]
fn nodes_ban_list_is_readable() {
    let bans: Vec<BanRecord> = serde_json::from_str(NODE_BAN_LIST).unwrap();

    assert_eq!(bans.len(), 2);
    let alice = &bans[0];
    assert_eq!(alice.player_id, "Alice");
    assert_eq!(alice.reason.as_deref(), Some("Cheating"));
    assert_eq!(alice.expires, Some(ms(1_754_500_000_000)));
    assert!(alice.is_valid());
    assert!(!alice.permanent);
    assert!(bans[1].permanent);
    assert_eq!(bans[1].expires, None);
}

#[test]
fn c_sharp_writes_the_format_node_can_read_back() {
    // WRITING milliseconds keeps a rollback possible: Node's `Number(b.expires)` of an ISO
    // string is NaN, and its validator drops the ban.
    let mut ban = BanRecord::for_player("Alice");
    ban.reason = Some("Cheating".into());
    ban.expires = Some(ms(1_754_500_000_000));

    let json = serde_json::to_string_pretty(&vec![ban]).unwrap();

    assert!(json.contains("1754500000000"));
    assert!(!json.contains("2025-"));
    assert!(!json.contains("2026-"));
    assert!(json.contains("\"playerId\""));
}

#[test]
fn a_round_trip_through_both_formats_preserves_the_instant() {
    let first: Vec<BanRecord> = serde_json::from_str(NODE_BAN_LIST).unwrap();
    let second: Vec<BanRecord> =
        serde_json::from_str(&serde_json::to_string(&first).unwrap()).unwrap();

    assert_eq!(first[0].expires, second[0].expires);
    assert_eq!(first[0].player_id, second[0].player_id);
}

#[test]
fn an_iso_timestamp_from_an_earlier_build_is_still_readable() {
    let iso = r#"[{"playerId":"Alice","reason":"x","expires":"2026-08-06T20:26:40+00:00"}]"#;
    let bans: Vec<BanRecord> = serde_json::from_str(iso).unwrap();

    assert_eq!(
        bans[0].expires,
        Some(Utc.with_ymd_and_hms(2026, 8, 6, 20, 26, 40).unwrap())
    );
}

#[test]
fn an_unreadable_timestamp_becomes_null_not_epoch_zero() {
    // A ban at epoch zero is "expired in 1970" and gets LIFTED on the next sweep.
    let broken =
        r#"[{"playerId":"Alice","reason":"x","expires":{"nested":"nonsense"},"permanent":true}]"#;
    let bans: Vec<BanRecord> = serde_json::from_str(broken).unwrap();

    assert_eq!(bans[0].expires, None);
    assert!(bans[0].permanent);
    assert!(bans[0].is_valid()); // permanent, so still enforceable
}

#[test]
fn camel_case_keys_from_node_bind() {
    let bans: Vec<BanRecord> = serde_json::from_str(
        r#"[{"playerId":"Alice","permanent":true,"durationLabel":"Permanent"}]"#,
    )
    .unwrap();

    assert_eq!(bans[0].player_id, "Alice");
    assert_eq!(bans[0].duration_label.as_deref(), Some("Permanent"));
}

#[test]
fn the_store_serves_nodes_data_rather_than_the_fallback() {
    let backend = Arc::new(MemoryBackend::new());
    backend.write(TEMPBANS, NODE_BAN_LIST).unwrap();
    let store = SerializedStore::new(backend);

    let bans: Vec<BanRecord> = store.read(TEMPBANS, Vec::new()).unwrap();

    assert_eq!(bans.len(), 2);
    assert!(!BanRules::active(&bans, ms(1_753_950_000_000)).is_empty());
}

#[tokio::test]
async fn a_write_does_not_destroy_records_it_could_not_parse() {
    // The exact wipe path: read, add one, write back.
    let backend = Arc::new(MemoryBackend::new());
    backend.write(TEMPBANS, NODE_BAN_LIST).unwrap();
    let store = SerializedStore::new(backend);

    store
        .update(TEMPBANS, Vec::<BanRecord>::new, |mut bans| {
            let mut carol = BanRecord::for_player("Carol");
            carol.reason = Some("New".into());
            carol.permanent = true;
            bans.push(carol);
            Some(bans)
        })
        .await
        .unwrap();

    let bans: Vec<BanRecord> = store.read(TEMPBANS, Vec::new()).unwrap();
    assert_eq!(bans.len(), 3);
}
