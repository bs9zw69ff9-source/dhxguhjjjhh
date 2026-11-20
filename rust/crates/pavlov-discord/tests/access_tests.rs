//! Port of `AccessTests.cs`: owners pass every gate by id alone; everyone else by role;
//! Administrator counts only in the staff guild; and a refusal is actionable without naming
//! who holds the tier.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::factions::FactionRegistry;
use pavlov_core::moderation::StaffTier;
use pavlov_core::roles::RoleMap;
use pavlov_core::security::OwnerGuard;
use pavlov_discord::access::{Access, RequiredAccess};
use pavlov_discord::caller::{Caller, MemberView};
use pavlov_storage::datasets::ROLES;

const SUPER_OWNER: u64 = 1;
const OWNER: u64 = 2;
const STRANGER: u64 = 3;
const MOD_ROLE: u64 = 100;
const ADMIN_ROLE: u64 = 200;
const HOME_GUILD: u64 = 5000;
const FOREIGN_GUILD: u64 = 6000;

const ALL: [RequiredAccess; 6] = [
    RequiredAccess::Public,
    RequiredAccess::Police,
    RequiredAccess::FactionLeader,
    RequiredAccess::Mod,
    RequiredAccess::Admin,
    RequiredAccess::Owner,
];

fn store() -> Arc<SerializedStore> {
    Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())))
}

fn build() -> Access {
    Access::new(store(), [OWNER], [SUPER_OWNER]).unwrap()
}

async fn with_roles() -> Access {
    let store = store();
    let roles = RoleMap {
        mod_role: Some(MOD_ROLE),
        admin_role: Some(ADMIN_ROLE),
        ..RoleMap::default()
    };
    store.write(ROLES, &roles).await.unwrap();
    Access::new(store, [OWNER], [SUPER_OWNER]).unwrap()
}

fn member(id: u64, roles: &[u64]) -> Caller {
    Caller::member(id, 0, roles)
}

fn administrator(id: u64, guild_id: u64) -> Caller {
    Caller {
        id,
        member: Some(MemberView {
            guild_id,
            role_ids: Vec::new(),
            administrator: true,
        }),
    }
}

fn home_is(access: &Access, guild: Option<u64>) {
    access.use_home_guild(Arc::new(|_| None), Some(Arc::new(move || guild)));
}

#[test]
fn an_owner_passes_every_gate() {
    let access = build();
    for required in ALL {
        assert!(access.allows(required, &member(OWNER, &[])), "{required}");
        assert!(
            access.allows(required, &member(SUPER_OWNER, &[])),
            "{required}"
        );
    }
}

#[test]
fn an_owner_passes_every_gate_even_with_no_guild_member() {
    // THE ACTUAL BUG: ownership is a set of ids and needs nothing from the guild.
    let access = build();
    for required in ALL {
        assert!(access.allows(required, &Caller::user(OWNER)), "{required}");
        assert!(
            access.allows(required, &Caller::user(SUPER_OWNER)),
            "{required}"
        );
    }
}

#[tokio::test]
async fn a_stranger_with_no_guild_member_passes_only_public() {
    let access = with_roles().await;
    let stranger = Caller::user(STRANGER);

    assert!(access.allows(RequiredAccess::Public, &stranger));
    for required in &ALL[1..] {
        assert!(!access.allows(*required, &stranger), "{required}");
    }
}

#[tokio::test]
async fn an_admin_is_a_mod_but_not_an_owner() {
    let access = with_roles().await;
    let admin = member(STRANGER, &[ADMIN_ROLE]);

    assert!(access.is_mod(&admin));
    assert!(access.is_admin(&admin));
    assert!(!access.is_owner(&admin));
    assert_eq!(access.tier_of(&admin), StaffTier::Admin);
}

#[tokio::test]
async fn a_mod_is_not_an_admin() {
    let access = with_roles().await;
    let moderator = member(STRANGER, &[MOD_ROLE]);

    assert!(access.is_mod(&moderator));
    assert!(!access.is_admin(&moderator));
    assert_eq!(access.tier_of(&moderator), StaffTier::Mod);
}

#[tokio::test]
async fn discords_own_administrator_permission_counts_in_the_staff_guild() {
    let access = with_roles().await;
    home_is(&access, Some(HOME_GUILD));
    assert!(access.is_admin(&administrator(STRANGER, HOME_GUILD)));
}

#[tokio::test]
async fn administrator_of_some_other_server_is_not_bot_admin() {
    // Invite the bot to a server you own and you used to be its admin.
    let access = with_roles().await;
    home_is(&access, Some(HOME_GUILD));
    let intruder = administrator(STRANGER, FOREIGN_GUILD);

    assert!(!access.is_admin(&intruder));
    assert!(!access.is_mod(&intruder));
    assert!(!access.allows(RequiredAccess::Admin, &intruder));
}

#[tokio::test]
async fn administrator_counts_for_nothing_when_the_staff_guild_is_unknown() {
    let access = with_roles().await;
    home_is(&access, None);

    assert!(!access.is_admin(&administrator(STRANGER, HOME_GUILD)));
    assert!(access.is_admin(&Caller::member(STRANGER, FOREIGN_GUILD, &[ADMIN_ROLE])));
}

#[test]
fn the_built_in_owner_is_a_master_owner_above_the_super_owners() {
    let access = build();
    let built_in = member(OwnerGuard::SUPER_OWNER_ID, &[]);

    assert_eq!(access.tier_of(&built_in), StaffTier::MasterOwner);
    assert!(access.is_super_owner(&built_in));
    assert!(!access.is_master_owner(&member(SUPER_OWNER, &[])));
}

#[test]
fn a_super_owner_is_never_a_master_owner() {
    let access = build();
    assert!(!access.is_master_owner(&member(SUPER_OWNER, &[])));
    assert_eq!(
        access.tier_of(&member(SUPER_OWNER, &[])),
        StaffTier::SuperOwner
    );
}

#[test]
fn a_super_owner_outranks_an_owner() {
    let access = build();
    assert_eq!(
        access.tier_of(&member(SUPER_OWNER, &[])),
        StaffTier::SuperOwner
    );
    assert_eq!(access.tier_of(&member(OWNER, &[])), StaffTier::Owner);
    assert!(access.is_owner(&member(SUPER_OWNER, &[])));
    assert!(!access.is_super_owner(&member(OWNER, &[])));
}

#[test]
fn an_owner_manages_every_faction_roster() {
    let access = build();
    let every = FactionRegistry::default_set().names().count();

    assert_eq!(access.manageable_factions(&member(OWNER, &[])).len(), every);
    let first = FactionRegistry::default_set().names().next().unwrap();
    assert!(access.can_manage(&Caller::user(SUPER_OWNER), first));
    assert!(
        access
            .manageable_factions(&member(STRANGER, &[]))
            .is_empty()
    );
}

#[test]
fn an_owner_is_described_as_one() {
    let access = build();
    assert_eq!(
        access.describe_access(&Caller::user(SUPER_OWNER)),
        "SUPER OWNER"
    );
    assert_eq!(access.describe_access(&Caller::user(OWNER)), "OWNER");
    assert_eq!(access.describe_access(&Caller::user(STRANGER)), "PUBLIC");
}

#[test]
fn a_refusal_says_what_you_hold_not_just_what_you_need() {
    let refusal = build().explain_refusal(&member(STRANGER, &[]), RequiredAccess::Mod);

    assert!(refusal.contains("Mod"));
    assert!(refusal.contains("PUBLIC"));
    assert!(refusal.contains("/setroles"));
}

#[tokio::test]
async fn a_refusal_names_the_unresolved_member_case() {
    let refusal = with_roles()
        .await
        .explain_refusal(&Caller::user(STRANGER), RequiredAccess::Mod);
    assert!(refusal.contains("roles could not be read"));
}

#[tokio::test]
async fn a_refusal_tells_a_resolved_member_with_no_roles_where_to_look() {
    let refusal = with_roles()
        .await
        .explain_refusal(&member(STRANGER, &[]), RequiredAccess::Mod);

    assert!(refusal.contains("No roles were read on you"));
    assert!(refusal.contains("HOME_GUILD_ID"));
    assert!(!refusal.contains("roles could not be read"));
    assert!(!refusal.contains("No moderator role is configured"));
}

#[test]
fn a_refusal_still_says_unconfigured_when_the_member_has_roles_but_none_mapped() {
    let refusal = build().explain_refusal(&member(STRANGER, &[999]), RequiredAccess::Mod);

    assert!(refusal.contains("No moderator role is configured"));
    assert!(!refusal.contains("No roles were read on you"));
}

#[test]
fn a_refusal_says_when_no_owners_exist_at_all() {
    let access = Access::new(store(), [], []).unwrap();
    let refusal = access.explain_refusal(&member(STRANGER, &[]), RequiredAccess::Owner);
    assert!(refusal.contains("OWNER_IDS"));
}

#[test]
fn a_refusal_never_names_who_does_hold_the_tier() {
    // A refusal listing the holders is a map of who to social-engineer.
    let refusal = build().explain_refusal(&member(STRANGER, &[]), RequiredAccess::Owner);
    assert!(!refusal.contains(&OWNER.to_string()));
    assert!(!refusal.contains(&SUPER_OWNER.to_string()));
}

#[tokio::test]
async fn a_hollow_member_is_read_from_the_staff_guild() {
    // Rust-only: a user-installed app hands over the caller with no roles; the staff guild
    // sees the same person with theirs.
    let access = with_roles().await;
    access.use_home_guild(
        Arc::new(|id| {
            (id == STRANGER).then(|| MemberView {
                guild_id: HOME_GUILD,
                role_ids: vec![MOD_ROLE],
                administrator: false,
            })
        }),
        Some(Arc::new(|| Some(HOME_GUILD))),
    );

    assert!(access.is_mod(&member(STRANGER, &[])));
    assert!(access.is_mod(&Caller::user(STRANGER)));
}
