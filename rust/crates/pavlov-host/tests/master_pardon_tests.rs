//! Port of `MasterPardonTests.cs`: a master owner's unban is a pardon.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::time::SystemClock;
use pavlov_host::moderation::{MasterNames, StoredMasterNames};

fn masters() -> StoredMasterNames {
    let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())));
    StoredMasterNames::new([], store, Arc::new(SystemClock)).unwrap()
}

#[tokio::test]
async fn a_pardoned_player_is_protected_from_every_automated_path() {
    let m = masters();
    assert!(m.pardon("Forgiven").await.unwrap());
    // is_protected is what the sweep, the reconcile and both responders consult.
    assert!(m.is_pardoned("forgiven"));
    assert!(m.is_protected("FORGIVEN"));
    assert!(!m.is_protected("SomeoneElse"));
}

#[tokio::test]
async fn clearing_the_pardon_ends_the_protection() {
    let m = masters();
    m.pardon("Forgiven").await.unwrap();
    m.clear_pardon("Forgiven").await.unwrap();
    assert!(!m.is_pardoned("Forgiven"));
    assert!(!m.is_protected("Forgiven"));
}

#[tokio::test]
async fn a_pardon_is_separate_from_the_never_ban_list() {
    let m = masters();
    m.pardon("Forgiven").await.unwrap();
    m.unprotect("Forgiven").await.unwrap();
    assert!(m.is_protected("Forgiven"));
    assert!(!m.protected().contains_key("Forgiven"));
}

#[tokio::test]
async fn the_built_in_master_is_always_a_master_and_exemptions_expire() {
    // Rust-only: the owner cannot be configured away, and an exemption is a window.
    let m = masters();
    assert!(m.is_master(" FKI6 "));
    m.exempt("Lifted", Some(std::time::Duration::from_secs(3600)))
        .await;
    assert!(m.is_exempt("lifted"));
    m.exempt("Gone", Some(std::time::Duration::ZERO)).await;
    assert!(!m.is_exempt("Gone"));
}
