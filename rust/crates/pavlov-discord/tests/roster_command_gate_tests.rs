//! Port of `RosterCommandGateTests.cs`: the faction-leader gate the roster commands run first,
//! and that it is not the whole check.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use indexmap::IndexMap;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::factions::{FactionDefinition, FactionRegistry, FactionSet};
use pavlov_core::roles::RoleMap;
use pavlov_discord::access::{Access, RequiredAccess};
use pavlov_discord::caller::Caller;
use pavlov_storage::datasets::ROLES;

const NCR_ROLE: u64 = 900;
const LEADER_ROLE: u64 = 901;
const STRANGER: u64 = 5;
const GUILD: u64 = 1;

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

async fn with_faction_roles() -> Access {
    let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())));
    let mut roles = RoleMap {
        faction_leader_role: Some(LEADER_ROLE),
        ..RoleMap::default()
    };
    roles.faction_roles.insert("GWPD", NCR_ROLE);
    store.write(ROLES, &roles).await.unwrap();
    Access::new(store, [], [])
        .unwrap()
        .with_factions(with_mafias())
}

fn member(roles: &[u64]) -> Caller {
    Caller::member(STRANGER, GUILD, roles)
}

#[tokio::test]
async fn somebody_who_manages_nothing_is_refused_at_the_gate() {
    let access = with_faction_roles().await;
    assert!(!access.allows(RequiredAccess::FactionLeader, &member(&[])));
    assert!(!access.allows(RequiredAccess::FactionLeader, &Caller::user(STRANGER)));
}

#[tokio::test]
async fn a_factions_own_role_gets_past_the_gate() {
    let access = with_faction_roles().await;
    assert!(access.allows(RequiredAccess::FactionLeader, &member(&[NCR_ROLE])));
}

#[tokio::test]
async fn the_gate_is_not_the_whole_check() {
    let access = with_faction_roles().await;
    let holder = member(&[NCR_ROLE]);
    assert!(access.can_manage(&holder, "NYPD")); // the GWPD's former name
    assert!(!access.can_manage(&holder, "Gambino"));
}

#[tokio::test]
async fn the_overall_leader_role_manages_every_faction() {
    let access = with_faction_roles().await;
    let leader = member(&[LEADER_ROLE]);
    assert!(access.allows(RequiredAccess::FactionLeader, &leader));
    assert!(access.can_manage(&leader, "NYPD"));
    assert!(access.can_manage(&leader, "Gambino"));
}

#[test]
fn an_owner_passes_without_any_role_at_all() {
    let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())));
    let access = Access::new(store, [STRANGER], [])
        .unwrap()
        .with_factions(FactionRegistry::police().clone());
    assert!(access.allows(RequiredAccess::FactionLeader, &Caller::user(STRANGER)));
}
