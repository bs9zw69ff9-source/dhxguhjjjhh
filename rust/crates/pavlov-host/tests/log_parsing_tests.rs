//! Port of the host half of `LogParsingTests.cs`: the embed an RCON command the game logged is
//! posted as. The parsing half is in `pavlov-core/tests/log_parsing_tests.rs`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{DateTime, TimeZone, Utc};
use pavlov_core::embed::{Embed, palette};
use pavlov_host::feeds::rcon_embed;

fn field<'a>(embed: &'a Embed, name: &str) -> &'a str {
    &embed.field_named(name).unwrap().value
}

#[test]
fn the_rcon_embed_carries_command_instigator_and_time() {
    let at = Utc.with_ymd_and_hms(2026, 9, 16, 4, 8, 14).unwrap();
    let embed = rcon_embed(
        true,
        "SetCash",
        Some("Rickythegamer1001"),
        Some("9999999"),
        Some("Server 2"),
        at,
        Some(true),
    );

    assert_eq!(embed.author.as_deref(), Some("RCON+ command"));
    assert_eq!(embed.timestamp, Some(at));
    assert_eq!(field(&embed, "Command"), "`SetCash 9999999`"); // instigator stripped
    assert_eq!(field(&embed, "By"), "Rickythegamer1001");
    assert_eq!(field(&embed, "Server"), "Server 2");
    assert_eq!(field(&embed, "Went through"), "✅ true");
}

#[test]
fn the_rcon_embed_shows_the_outcome_and_red_ns_for_a_failure() {
    let at = DateTime::UNIX_EPOCH;

    let failed = rcon_embed(
        true,
        "RemoveAccessManager",
        Some("Console"),
        Some("DontFuccnKnowMe"),
        Some("Server 1"),
        at,
        Some(false),
    );
    assert_eq!(field(&failed, "Went through"), "❌ false");
    assert_eq!(failed.color, Some(palette::BAN_RED)); // a failure overrides the channel colour

    // Base RCON reports no result: "executed", not a false that would read as failure.
    let base = rcon_embed(
        false,
        "Ban",
        None,
        Some("Alice"),
        Some("Server 1"),
        at,
        None,
    );
    assert_eq!(field(&base, "Went through"), "☑️ executed");
    assert_eq!(base.color, Some(palette::BLUE));
}

#[test]
fn a_base_rcon_embed_reads_as_console_and_a_name_cannot_forge_content() {
    let embed = rcon_embed(
        false,
        "KickPlayer",
        None,
        Some("0002abc"),
        None,
        DateTime::UNIX_EPOCH,
        None,
    );

    assert_eq!(embed.author.as_deref(), Some("RCON command"));
    assert_eq!(field(&embed, "By"), "console"); // base RCON records no issuer
    assert_eq!(field(&embed, "Server"), "the server");

    let forged = rcon_embed(
        true,
        "Godmode",
        Some("Bob\nInjected"),
        Some("True"),
        None,
        DateTime::UNIX_EPOCH,
        None,
    );
    assert!(!field(&forged, "By").contains('\n'));
}
