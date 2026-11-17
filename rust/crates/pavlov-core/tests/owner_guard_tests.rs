//! Port of `OwnerGuardTests.cs`, the tests that need only core. The access-layer and
//! master-name tests in that file arrive with `pavlov-discord` and `pavlov-services`.
//!
//! The literals are WRITTEN OUT rather than read from `OwnerGuard`, so removing a constant
//! fails the build and changing one fails a test naming the old and new value.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashSet;

use pavlov_core::security::OwnerGuard;
use sha2::{Digest, Sha256};

const PINNED_SUPER_OWNER_ID: u64 = 1_014_251_293_159_731_310;
const PINNED_SECOND_MASTER_OWNER_ID: u64 = 307_052_224_087_851_009;
const PINNED_MASTER_NAME: &str = "fki6";

#[test]
fn the_super_owner_id_is_the_compiled_in_one() {
    assert_eq!(OwnerGuard::SUPER_OWNER_ID, PINNED_SUPER_OWNER_ID);
}

#[test]
fn the_fingerprint_matches_the_constants() {
    assert_eq!(OwnerGuard::compute(), OwnerGuard::FINGERPRINT);
    OwnerGuard::verify().unwrap();
}

#[test]
fn the_fingerprint_is_the_digest_of_the_ids_and_name() {
    // Recomputed from the pinned literals, so editing the constants AND the fingerprint
    // together (the only edit verify() accepts) still fails here.
    let digest = Sha256::digest(
        format!("{PINNED_SUPER_OWNER_ID}:{PINNED_SECOND_MASTER_OWNER_ID}:{PINNED_MASTER_NAME}")
            .as_bytes(),
    );
    let expected: String = digest.iter().map(|b| format!("{b:02x}")).collect();
    assert_eq!(OwnerGuard::FINGERPRINT, expected);
}

#[test]
fn an_owner_set_without_the_built_in_is_rejected() {
    let err = OwnerGuard::verify_owners(&HashSet::from([42])).unwrap_err();
    assert!(err.to_string().to_lowercase().contains("missing"));
}

#[test]
fn an_empty_configuration_still_yields_the_built_in() {
    assert!(
        OwnerGuard::with_built_in_owners([])
            .unwrap()
            .contains(&PINNED_SUPER_OWNER_ID)
    );
}

#[test]
fn configured_entries_are_kept_alongside_the_built_in() {
    let owners = OwnerGuard::with_built_in_owners([7, 8]).unwrap();
    assert!(owners.contains(&7));
    assert!(owners.contains(&8));
    assert!(owners.contains(&PINNED_SUPER_OWNER_ID));
}

#[test]
fn the_built_in_is_not_duplicated_when_also_configured() {
    assert_eq!(
        OwnerGuard::with_built_in_owners([PINNED_SUPER_OWNER_ID])
            .unwrap()
            .len(),
        1
    );
}
