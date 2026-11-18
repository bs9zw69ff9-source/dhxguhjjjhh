//! Port of the access half of `OwnerGuardTests.cs`: the compiled-in owners and master name
//! hold with NOTHING configured. The fingerprint half is in `pavlov-core`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::security::OwnerGuard;
use pavlov_core::time::SystemClock;
use pavlov_discord::access::{Access, RequiredAccess};
use pavlov_discord::caller::Caller;
use pavlov_host::moderation::{MasterNames, StoredMasterNames};

const PINNED_SUPER_OWNER_ID: u64 = 1_014_251_293_159_731_310;
const PINNED_SECOND_MASTER_OWNER_ID: u64 = 307_052_224_087_851_009;
const PINNED_MASTER_NAME: &str = "fki6";

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

fn unconfigured() -> Access {
    Access::new(store(), [], []).unwrap()
}

#[test]
fn the_built_in_is_a_master_owner_with_nothing_configured() {
    let access = unconfigured();
    let owner = Caller::user(PINNED_SUPER_OWNER_ID);

    assert!(access.is_master_owner(&owner));
    assert!(access.is_super_owner(&owner));
    assert!(access.is_owner(&owner));
    assert_eq!(access.describe_access(&owner), "MASTER OWNER");
}

#[test]
fn the_built_in_passes_every_gate_with_no_guild_member() {
    let access = unconfigured();
    let owner = Caller::user(PINNED_SUPER_OWNER_ID);
    for required in ALL {
        assert!(
            access.allows(required, &owner),
            "built-in owner refused {required}"
        );
    }
}

#[test]
fn adding_the_built_in_does_not_promote_anybody_else() {
    // The guard widens authority by exactly one id.
    let access = unconfigured();
    assert!(!access.is_super_owner(&Caller::member(PINNED_SUPER_OWNER_ID + 1, 0, &[])));
    assert!(!access.is_owner(&Caller::member(PINNED_SUPER_OWNER_ID - 1, 0, &[])));
    assert!(!access.is_owner(&Caller::member(0, 0, &[])));
}

#[test]
fn the_built_in_master_name_is_always_a_master() {
    let masters = StoredMasterNames::new([], store(), Arc::new(SystemClock)).unwrap();

    assert_eq!(OwnerGuard::MASTER_NAME, PINNED_MASTER_NAME);
    assert!(masters.is_master("fki6"));
    assert!(masters.is_master("  FKI6 "));
    assert!(masters.masters().contains("fki6"));
    assert!(!masters.is_master("LxPXHam"));
}

#[test]
fn configured_masters_are_kept_alongside_the_built_in() {
    let masters = StoredMasterNames::new(
        ["  Spaced  ", "", "   ", "FKI6"],
        store(),
        Arc::new(SystemClock),
    )
    .unwrap();

    assert!(masters.is_master("spaced"));
    assert_eq!(masters.masters().len(), 2); // Spaced and fki6, not fki6 twice
}

#[test]
fn the_second_master_owner_is_a_master_owner_with_nothing_configured() {
    let access = unconfigured();
    let second = Caller::user(PINNED_SECOND_MASTER_OWNER_ID);

    assert_eq!(
        OwnerGuard::SECOND_MASTER_OWNER_ID,
        PINNED_SECOND_MASTER_OWNER_ID
    );
    assert!(access.is_master_owner(&second));
    assert!(access.is_super_owner(&second));
    assert!(access.is_owner(&second));
    assert!(access.is_owner_id(PINNED_SECOND_MASTER_OWNER_ID));
    assert_eq!(access.describe_access(&second), "MASTER OWNER");
    for required in ALL {
        assert!(
            access.allows(required, &second),
            "second master owner refused {required}"
        );
    }
}

#[test]
fn nobody_else_becomes_a_master_owner() {
    let access = Access::new(store(), [42], [43]).unwrap();
    for id in [42, 43, PINNED_SECOND_MASTER_OWNER_ID + 1] {
        assert!(!access.is_master_owner(&Caller::user(id)), "{id}");
    }
}
