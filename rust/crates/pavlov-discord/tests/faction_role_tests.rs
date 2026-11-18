//! Port of `FactionRoleTests.cs`: per-faction delegation (and the legacy mafia and NYPD slots
//! a live `roles.json` still holds), and `CommandOptionNameTests`. `SetRolesShapeTests` is
//! ported with `/setroles` itself.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use indexmap::IndexMap;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::factions::{FactionDefinition, FactionRegistry, FactionSet};
use pavlov_core::roles::RoleMap;
use pavlov_discord::access::Access;
use pavlov_discord::caller::Caller;
use pavlov_discord::option_name::slug;
use pavlov_storage::datasets::ROLES;

const OWNER: u64 = 2;
const MEMBER: u64 = 50;
const NCR_ROLE: u64 = 700;
const LEGION_ROLE: u64 = 701;
const LEGACY_MAFIA_ROLE: u64 = 800;
const LEGACY_NYPD_ROLE: u64 = 801;

fn faction(name: &str, prefix: &str) -> FactionDefinition {
    FactionDefinition {
        name: name.into(),
        order: vec!["Recruit".into(), "Veteran".into()],
        default: "Recruit".into(),
        rank_files: IndexMap::from([
            ("Recruit".to_owned(), format!("{prefix}recruit.txt")),
            ("Veteran".to_owned(), format!("{prefix}veteran.txt")),
        ]),
        spawn_file: format!("{prefix}spawn.txt"),
        subclasses: IndexMap::new(),
        former_names: Vec::new(),
    }
}

fn two_factions() -> FactionSet {
    FactionSet::of([faction("NCR", "ncr"), faction("Legion", "legion")])
}

fn spawn_only(name: &str) -> FactionDefinition {
    let file = format!("{}spawn.txt", name.to_lowercase());
    FactionDefinition {
        name: name.into(),
        order: vec!["Member".into()],
        default: "Member".into(),
        rank_files: IndexMap::from([("Member".to_owned(), file.clone())]),
        spawn_file: file,
        subclasses: IndexMap::new(),
        former_names: Vec::new(),
    }
}

fn with_mafias() -> FactionSet {
    FactionSet::of([
        spawn_only("Gambino"),
        spawn_only("Colombo"),
        FactionRegistry::get(Some(FactionRegistry::POLICE_NAME))
            .unwrap()
            .clone(),
    ])
}

fn faction_roles(pairs: &[(&str, u64)]) -> RoleMap {
    let mut roles = RoleMap::default();
    for (name, id) in pairs {
        roles.faction_roles.insert(*name, *id);
    }
    roles
}

async fn build(factions: FactionSet, roles: RoleMap) -> Access {
    let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())));
    store.write(ROLES, &roles).await.unwrap();
    Access::new(store, [OWNER], [])
        .unwrap()
        .with_factions(factions)
}

fn member(roles: &[u64]) -> Caller {
    Caller::member(MEMBER, 0, roles)
}

#[tokio::test]
async fn a_configured_factions_role_manages_only_that_faction() {
    let access = build(two_factions(), faction_roles(&[("NCR", NCR_ROLE)])).await;
    let m = member(&[NCR_ROLE]);

    assert_eq!(access.manageable_factions(&m), ["NCR"]);
    assert!(access.can_manage(&m, "NCR"));
    assert!(!access.can_manage(&m, "Legion"));
}

#[tokio::test]
async fn a_themed_bot_has_per_faction_delegation_at_all() {
    let access = build(two_factions(), faction_roles(&[("Legion", LEGION_ROLE)])).await;
    assert!(
        !access
            .manageable_factions(&member(&[LEGION_ROLE]))
            .is_empty()
    );
}

#[tokio::test]
async fn one_role_may_manage_several_factions() {
    let access = build(
        two_factions(),
        faction_roles(&[("NCR", NCR_ROLE), ("Legion", NCR_ROLE)]),
    )
    .await;
    assert_eq!(access.manageable_factions(&member(&[NCR_ROLE])).len(), 2);
}

#[tokio::test]
async fn a_faction_name_is_matched_case_insensitively() {
    let access = build(two_factions(), faction_roles(&[("ncr", NCR_ROLE)])).await;
    assert!(access.can_manage(&member(&[NCR_ROLE]), "NCR"));
}

#[tokio::test]
async fn the_old_mafia_slot_still_manages_both_mafias() {
    let roles = RoleMap {
        mafia_role: Some(LEGACY_MAFIA_ROLE),
        ..RoleMap::default()
    };
    let access = build(with_mafias(), roles).await;
    let m = member(&[LEGACY_MAFIA_ROLE]);

    assert!(access.can_manage(&m, "Gambino"));
    assert!(access.can_manage(&m, "Colombo"));
    assert!(!access.can_manage(&m, "NYPD"));
}

#[tokio::test]
async fn the_old_nypd_slot_still_manages_the_nypd() {
    let roles = RoleMap {
        nypd_role: Some(LEGACY_NYPD_ROLE),
        ..RoleMap::default()
    };
    let access = build(with_mafias(), roles).await;
    let m = member(&[LEGACY_NYPD_ROLE]);

    assert!(access.can_manage(&m, "NYPD"));
    assert!(!access.can_manage(&m, "Gambino"));
}

#[tokio::test]
async fn an_explicit_faction_role_beats_the_legacy_slot() {
    let mut roles = faction_roles(&[("Gambino", NCR_ROLE)]);
    roles.mafia_role = Some(LEGACY_MAFIA_ROLE);
    let access = build(with_mafias(), roles).await;

    assert!(access.can_manage(&member(&[NCR_ROLE]), "Gambino"));
    assert!(!access.can_manage(&member(&[LEGACY_MAFIA_ROLE]), "Gambino"));
    assert!(access.can_manage(&member(&[LEGACY_MAFIA_ROLE]), "Colombo"));
}

#[tokio::test]
async fn the_legacy_slots_grant_nothing_on_a_themed_bot() {
    let roles = RoleMap {
        mafia_role: Some(LEGACY_MAFIA_ROLE),
        nypd_role: Some(LEGACY_NYPD_ROLE),
        ..RoleMap::default()
    };
    let access = build(two_factions(), roles).await;
    assert!(
        access
            .manageable_factions(&member(&[LEGACY_MAFIA_ROLE, LEGACY_NYPD_ROLE]))
            .is_empty()
    );
}

#[tokio::test]
async fn role_map_survives_a_round_trip_through_the_store() {
    let store = SerializedStore::new(Arc::new(MemoryBackend::new()));
    let mut roles = faction_roles(&[("NCR", NCR_ROLE)]);
    roles.mod_role = Some(1);
    roles.mafia_role = Some(LEGACY_MAFIA_ROLE);
    store.write(ROLES, &roles).await.unwrap();

    let read: RoleMap = store.read(ROLES, RoleMap::default()).unwrap();
    assert_eq!(read.mod_role, Some(1));
    assert_eq!(read.mafia_role, Some(LEGACY_MAFIA_ROLE));
    assert_eq!(read.role_for(Some("NCR")), Some(NCR_ROLE));
    assert_eq!(read.role_for(Some("ncr")), Some(NCR_ROLE));
}

// ---- CommandOptionNameTests ----

#[test]
fn a_faction_name_becomes_a_lowercase_option_name() {
    for (faction, expected) in [
        ("NCR", "ncr_role"),
        ("Legion", "legion_role"),
        ("Brotherhood of Steel", "brotherhood_of_steel_role"),
        ("NYPD", "nypd_role"),
    ] {
        assert_eq!(slug(Some(faction), "_role").as_deref(), Some(expected));
    }
}

#[test]
fn punctuation_collapses_rather_than_repeating() {
    assert_eq!(
        slug(Some("Mojave Express Couriers!"), "_role").as_deref(),
        Some("mojave_express_couriers_role")
    );
}

#[test]
fn a_long_name_is_truncated_and_keeps_its_suffix() {
    let slug = slug(Some("The Brotherhood of Steel Mojave Chapter"), "_role").unwrap();
    assert!(slug.len() <= 32, "\"{slug}\" is {} characters", slug.len());
    assert!(slug.ends_with("_role"));
    assert!(!slug.contains("__"));
}

#[test]
fn a_name_with_nothing_usable_in_it_yields_null() {
    assert_eq!(slug(Some("!!!"), "_role"), None);
    assert_eq!(slug(Some("   "), "_role"), None);
    assert_eq!(slug(None, "_role"), None);
}

#[test]
fn every_produced_name_matches_discords_rule() {
    for name in [
        "NCR",
        "Legion",
        "Brotherhood of Steel",
        "Enclave",
        "Mojave Express Couriers!",
        "Crimson Caravan Co.",
        "Vault 21",
        "Caesar's Legion",
    ] {
        let slug = slug(Some(name), "_role").unwrap();
        assert!(
            !slug.is_empty()
                && slug.len() <= 32
                && slug
                    .chars()
                    .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '_' || c == '-'),
            "{slug}"
        );
    }
}
