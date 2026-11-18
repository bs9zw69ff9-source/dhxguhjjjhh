//! Port of `RoleMapRoundTripTests.cs` (and its `RoleStandingTests`): every role survives the
//! store, and "unset", "no member" and "not held" stay three different answers.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::roles::RoleMap;
use pavlov_discord::access::{Access, RoleStanding};
use pavlov_discord::caller::Caller;
use pavlov_storage::datasets::ROLES;

const ROLE: u64 = 1_234_567_890_123_456_789;

fn store() -> Arc<SerializedStore> {
    Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())))
}

fn read(store: &SerializedStore) -> RoleMap {
    store.read(ROLES, RoleMap::default()).unwrap()
}

#[tokio::test]
async fn every_tier_survives_the_store() {
    let store = store();
    store
        .update(ROLES, RoleMap::default, |_| {
            Some(RoleMap {
                mod_role: Some(ROLE),
                admin_role: Some(987_654_321_098_765_432),
                faction_leader_role: Some(111_111_111_111_111_111),
                police_role: Some(222_222_222_222_222_222),
                ..RoleMap::default()
            })
        })
        .await
        .unwrap();

    let roles = read(&store);
    assert_eq!(roles.mod_role, Some(ROLE));
    assert_eq!(roles.admin_role, Some(987_654_321_098_765_432));
    assert_eq!(roles.faction_leader_role, Some(111_111_111_111_111_111));
    assert_eq!(roles.police_role, Some(222_222_222_222_222_222));
}

#[tokio::test]
async fn per_faction_roles_survive_the_store() {
    // A map that fails to deserialise hands back the FALLBACK: every role unset at once.
    let store = store();
    store
        .update(ROLES, RoleMap::default, |_| {
            let mut roles = RoleMap {
                mod_role: Some(ROLE),
                ..RoleMap::default()
            };
            roles.faction_roles.insert("NCR", 555_555_555_555_555_555);
            roles
                .faction_roles
                .insert("Legion", 666_666_666_666_666_666);
            Some(roles)
        })
        .await
        .unwrap();

    let roles = read(&store);
    assert_eq!(roles.mod_role, Some(ROLE));
    assert_eq!(roles.role_for(Some("NCR")), Some(555_555_555_555_555_555));
    assert_eq!(
        roles.role_for(Some("Legion")),
        Some(666_666_666_666_666_666)
    );
}

#[tokio::test]
async fn a_per_faction_role_is_found_whatever_the_casing() {
    let store = store();
    store
        .update(ROLES, RoleMap::default, |_| {
            let mut roles = RoleMap::default();
            roles.faction_roles.insert("NCR", 555_555_555_555_555_555);
            Some(roles)
        })
        .await
        .unwrap();

    let roles = read(&store);
    assert_eq!(roles.role_for(Some("ncr")), Some(555_555_555_555_555_555));
    assert_eq!(roles.role_for(Some("NCR")), Some(555_555_555_555_555_555));
}

#[tokio::test]
async fn setting_one_tier_does_not_clear_the_others_across_a_restart() {
    let store = store();
    store
        .update(ROLES, RoleMap::default, |current: RoleMap| {
            Some(RoleMap {
                mod_role: Some(ROLE),
                ..current
            })
        })
        .await
        .unwrap();
    let between = read(&store);
    store
        .update(ROLES, RoleMap::default, move |_| {
            Some(RoleMap {
                admin_role: Some(987_654_321_098_765_432),
                ..between
            })
        })
        .await
        .unwrap();

    let roles = read(&store);
    assert_eq!(roles.mod_role, Some(ROLE));
    assert_eq!(roles.admin_role, Some(987_654_321_098_765_432));
}

fn access() -> Access {
    Access::new(store(), [], []).unwrap()
}

#[test]
fn no_role_mapped_is_its_own_answer() {
    assert_eq!(
        access().standing_on(&Caller::user(1), None),
        RoleStanding::Unset
    );
}

#[test]
fn a_user_who_is_not_a_guild_member_is_not_simply_missing_the_role() {
    assert_eq!(
        access().standing_on(&Caller::user(1), Some(ROLE)),
        RoleStanding::NoMember
    );
    assert_eq!(
        access().standing_on(&Caller::member(1, 0, &[ROLE]), Some(ROLE)),
        RoleStanding::Held
    );
    assert_eq!(
        access().standing_on(&Caller::member(1, 0, &[7]), Some(ROLE)),
        RoleStanding::NotHeld
    );
}

#[test]
fn visible_roles_is_null_when_there_is_no_member_to_read() {
    // None and empty mean different things: nothing to read, versus read and none.
    assert_eq!(access().visible_roles(&Caller::user(1)), None);
    assert_eq!(
        access().visible_roles(&Caller::member(1, 0, &[])),
        Some(Vec::new())
    );
}
