//! Port of `RoleMapLegacyShapeTests.cs`: the `roles` dataset in the Node bot's shape (suffixed
//! keys, string ids) is read, and every write normalises it to this type's own.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_core::data::{KeyValueBackend, MemoryBackend, SerializedStore};
use pavlov_core::roles::RoleMap;
use serde_json::Value;

const ROLES: &str = "roles";
const MOD: u64 = 1_400_000_000_000_000_001;
const ADMIN: u64 = 1_400_000_000_000_000_002;
const LEADER: u64 = 1_400_000_000_000_000_003;
const POLICE: u64 = 1_400_000_000_000_000_004;
const GAMBINO: u64 = 1_400_000_000_000_000_005;
const COLOMBO: u64 = 1_400_000_000_000_000_006;
const NYPD: u64 = 1_400_000_000_000_000_007;
/// The seed `pavlov-storage` writes for a fresh install.
const SEED: &str = r#"{"modRole":null,"adminRole":null,"factionLeaderRole":null,"policeRole":null,"factionRoles":{}}"#;

fn node_file() -> String {
    format!(
        r#"{{"modRoleId":"{MOD}","adminRoleId":"{ADMIN}","factionLeaderRoleId":"{LEADER}","policeRoleId":"{POLICE}","gambinoRoleId":"{GAMBINO}","colomboRoleId":"{COLOMBO}","nypdRoleId":"{NYPD}"}}"#
    )
}

fn store(seeded: Option<&str>) -> SerializedStore {
    let backend = Arc::new(MemoryBackend::new());
    if let Some(json) = seeded {
        backend.write(ROLES, json).unwrap();
    }
    SerializedStore::new(backend)
}

fn read(json: &str) -> RoleMap {
    store(Some(json)).read(ROLES, RoleMap::default()).unwrap()
}

#[test]
fn every_tier_the_node_bot_stored_is_read() {
    let r = read(&node_file());
    assert_eq!(r.mod_role, Some(MOD));
    assert_eq!(r.admin_role, Some(ADMIN));
    assert_eq!(r.faction_leader_role, Some(LEADER));
    assert_eq!(r.police_role, Some(POLICE));
    assert_eq!(r.nypd_role, Some(NYPD));
}

#[test]
fn the_two_mafia_roles_arrive_as_separate_faction_entries() {
    let r = read(&node_file());
    assert_eq!(r.role_for(Some("Gambino")), Some(GAMBINO));
    assert_eq!(r.role_for(Some("Colombo")), Some(COLOMBO));
    assert_ne!(r.role_for(Some("Gambino")), r.role_for(Some("Colombo")));
}

#[test]
fn an_empty_string_is_no_role_rather_than_an_unreadable_dataset() {
    let r = read(r#"{"modRoleId":"","adminRoleId":"","policeRoleId":""}"#);
    assert_eq!(r.mod_role, None);
    assert_eq!(r.admin_role, None);
    assert_eq!(r.police_role, None);
}

#[test]
fn a_numeric_legacy_id_is_accepted_too() {
    assert_eq!(
        read(&format!(r#"{{"modRoleId":{MOD}}}"#)).mod_role,
        Some(MOD)
    );
}

#[test]
fn an_unparseable_id_is_no_role_and_leaves_the_rest_intact() {
    let r = read(&format!(
        r#"{{"modRoleId":"not-a-snowflake","adminRoleId":"{ADMIN}"}}"#
    ));
    assert_eq!(r.mod_role, None);
    assert_eq!(r.admin_role, Some(ADMIN));
}

#[test]
fn the_canonical_key_beats_the_legacy_alias() {
    let r = read(&format!(r#"{{"modRole":{MOD},"modRoleId":"{ADMIN}"}}"#));
    assert_eq!(r.mod_role, Some(MOD));
}

#[tokio::test]
async fn writing_normalises_the_dataset_to_the_unsuffixed_names() {
    let s = store(Some(&node_file()));
    s.update(ROLES, RoleMap::default, |mut current| {
        current.mod_role = Some(MOD);
        Some(current)
    })
    .await
    .unwrap();

    let raw: Value = serde_json::from_str(&s.read_raw(ROLES).unwrap().unwrap()).unwrap();
    let keys: Vec<&str> = raw
        .as_object()
        .unwrap()
        .keys()
        .map(String::as_str)
        .collect();
    assert!(!keys.contains(&"modRoleId"));
    assert!(!keys.contains(&"gambinoRoleId"));
    assert!(keys.contains(&"modRole"));

    let r: RoleMap = s.read(ROLES, RoleMap::default()).unwrap();
    assert_eq!(r.admin_role, Some(ADMIN));
    assert_eq!(r.role_for(Some("Gambino")), Some(GAMBINO));
}

#[test]
fn the_seed_round_trips_through_the_type_it_seeds() {
    let r = read(SEED);
    assert_eq!(r.mod_role, None);
    assert!(r.faction_roles.is_empty());
    let set = RoleMap {
        mod_role: Some(MOD),
        ..r
    };
    assert_eq!(set.mod_role, Some(MOD));
}

#[test]
fn a_null_faction_map_reads_as_empty_rather_than_throwing() {
    let r = read(r#"{"factionRoles":null}"#);
    assert!(r.faction_roles.is_empty());
    assert_eq!(r.role_for(Some("Gambino")), None);
}

#[tokio::test]
async fn faction_roles_stay_case_insensitive_after_a_read_back() {
    let s = store(None);
    s.update(ROLES, RoleMap::default, |_| {
        let mut r = RoleMap::default();
        r.faction_roles.insert("Brotherhood of Steel", GAMBINO);
        Some(r)
    })
    .await
    .unwrap();

    let r: RoleMap = s.read(ROLES, RoleMap::default()).unwrap();
    assert_eq!(r.role_for(Some("brotherhood of steel")), Some(GAMBINO));
    assert!(r.faction_roles.contains_key("BROTHERHOOD OF STEEL"));
}
