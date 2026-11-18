//! Port of `LoreTests.cs`, the setting half (`BOT_NAME`). The vocabulary and the branding
//! arrive with `pavlov-discord` (M7).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use common::features;

#[test]
fn the_skin_is_read_from_the_environment() {
    /* BOT_NAME was documented as "stamped on every embed" since the port and read by nothing
    at all - a skin that silently did nothing. */
    assert_eq!(
        features(&[("BOT_NAME", "GWPD Dispatch")])
            .bot_name
            .as_deref(),
        Some("GWPD Dispatch")
    );
    assert!(features(&[]).bot_name.is_none());
}
