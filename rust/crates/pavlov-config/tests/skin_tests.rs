//! Port of `SkinTests.cs`, the setting half (`THEME`). The skin itself - the police words and
//! colours - arrives with `pavlov-discord` (M7).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use common::features;

#[test]
fn theme_is_read_from_the_environment() {
    assert_eq!(
        features(&[("THEME", "police")]).theme_name.as_deref(),
        Some("police")
    );
    assert!(features(&[]).theme_name.is_none());
}
