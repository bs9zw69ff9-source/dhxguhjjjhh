//! Port of `HomeGuildAccessTests.cs`: roles read from the staff (home) guild when the
//! interaction carries no member, or a hollow one with no roles, and never otherwise.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::factions::FactionRegistry;
use pavlov_core::roles::RoleMap;
use pavlov_discord::access::{Access, HomeLookup, RequiredAccess};
use pavlov_discord::caller::{Caller, MemberView};
use pavlov_storage::datasets::ROLES;

const MOD_ROLE: u64 = 100;
const ADMIN_ROLE: u64 = 200;
const STRANGER: u64 = 3;

async fn with(roles: RoleMap) -> Access {
    let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())));
    store.write(ROLES, &roles).await.unwrap();
    Access::new(store, [], [])
        .unwrap()
        .with_factions(FactionRegistry::police().clone())
}

async fn with_roles() -> Access {
    with(RoleMap {
        mod_role: Some(MOD_ROLE),
        admin_role: Some(ADMIN_ROLE),
        ..RoleMap::default()
    })
    .await
}

fn holding(roles: &'static [u64]) -> HomeLookup {
    Arc::new(move |id| {
        (id != 0).then(|| MemberView {
            guild_id: 0,
            role_ids: roles.to_vec(),
            administrator: false,
        })
    })
}

fn nobody() -> HomeLookup {
    Arc::new(|_| None)
}

fn must_not_be_asked() -> HomeLookup {
    Arc::new(|_| {
        panic!("the interaction member had roles; the home guild should not have been asked")
    })
}

fn stranger() -> Caller {
    Caller::user(STRANGER)
}

fn member(roles: &[u64]) -> Caller {
    Caller::member(STRANGER, 0, roles)
}

#[tokio::test]
async fn with_no_home_guild_a_user_with_no_membership_holds_nothing() {
    assert!(!with_roles().await.is_mod(&stranger()));
}

#[tokio::test]
async fn the_home_guilds_roles_apply_when_the_interaction_carries_no_member() {
    let access = with_roles().await;
    access.use_home_guild(holding(&[MOD_ROLE]), None);

    assert!(access.is_mod(&stranger()));
    assert!(access.allows(RequiredAccess::Mod, &stranger()));
}

#[tokio::test]
async fn somebody_who_is_not_in_the_home_guild_still_holds_nothing() {
    let access = with_roles().await;
    access.use_home_guild(nobody(), None);

    assert!(!access.is_mod(&stranger()));
    assert!(!access.allows(RequiredAccess::Mod, &stranger()));
}

#[tokio::test]
async fn only_the_roles_the_guild_granted_carry_over() {
    let access = with_roles().await;
    access.use_home_guild(holding(&[MOD_ROLE]), None);

    assert!(access.is_mod(&stranger()));
    assert!(!access.is_admin(&stranger()));
}

#[tokio::test]
async fn the_interactions_own_member_is_used_without_consulting_the_home_guild() {
    let access = with_roles().await;
    access.use_home_guild(must_not_be_asked(), None);

    assert!(access.is_mod(&member(&[MOD_ROLE])));
    assert!(!access.is_mod(&member(&[9999])));
}

#[tokio::test]
async fn a_hollow_member_with_no_roles_falls_back_to_the_home_guild() {
    // THE USER-APP-IN-A-GUILD CASE: a real member, handed over empty.
    let access = with_roles().await;
    access.use_home_guild(holding(&[MOD_ROLE]), None);

    assert!(access.is_mod(&member(&[])));
    assert!(access.allows(RequiredAccess::Mod, &member(&[])));
}

#[tokio::test]
async fn a_genuinely_roleless_member_stays_roleless_when_the_home_guild_agrees() {
    let access = with_roles().await;
    access.use_home_guild(holding(&[]), None);

    assert!(!access.is_mod(&member(&[])));
    assert!(!access.allows(RequiredAccess::Mod, &member(&[])));
}

#[tokio::test]
async fn a_hollow_member_with_no_home_guild_match_stays_public() {
    let access = with_roles().await;
    access.use_home_guild(nobody(), None);
    assert!(!access.is_mod(&member(&[])));
}

#[tokio::test]
async fn a_member_that_already_has_roles_is_never_second_guessed_by_the_home_guild() {
    let access = with_roles().await;
    access.use_home_guild(must_not_be_asked(), None);

    assert!(access.is_mod(&member(&[MOD_ROLE])));
    assert!(!access.is_mod(&member(&[9999])));
}

#[tokio::test]
async fn discords_own_administrator_permission_carries_over_too() {
    let access = with_roles().await;
    access.use_home_guild(
        Arc::new(|_| {
            Some(MemberView {
                guild_id: 5000,
                role_ids: Vec::new(),
                administrator: true,
            })
        }),
        Some(Arc::new(|| Some(5000))),
    );

    assert!(access.is_admin(&stranger()));
}

#[tokio::test]
async fn per_faction_delegation_carries_over_as_well() {
    let mut roles = RoleMap::default();
    roles.faction_roles.insert("GWPD", 777);
    let access = with(roles).await;
    access.use_home_guild(holding(&[777]), None);

    assert!(access.can_manage(&stranger(), "NYPD"));
    assert!(access.allows(RequiredAccess::FactionLeader, &stranger()));
}

#[tokio::test]
async fn the_refusal_explains_where_roles_are_read_from_when_there_is_no_member_anywhere() {
    let refusal = with_roles()
        .await
        .explain_refusal(&stranger(), RequiredAccess::Mod);
    assert!(refusal.to_lowercase().contains("staff guild"));
}

#[tokio::test]
async fn that_explanation_is_not_shown_to_somebody_whose_roles_were_read() {
    let access = with_roles().await;
    access.use_home_guild(holding(&[]), None);

    let refusal = access.explain_refusal(&stranger(), RequiredAccess::Mod);
    assert!(!refusal.to_lowercase().contains("staff guild"));
}
