//! Port of `LoreTests.cs`, the pure half: one vocabulary, one stamp, and the currency the
//! server actually uses. The `BOT_NAME` setting test is in `pavlov-config`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashMap;

use pavlov_core::embed::Embed;
use pavlov_core::penal::PenalCode;
use pavlov_core::skin::{self, lore, theme};

const BRAND: &str = "GWPD Dispatch";

fn mark() -> &'static str {
    skin::police().words.mark
}

#[test]
fn money_is_counted_in_dollars() {
    assert_eq!(lore::amount(1250), "$1,250");
}

#[test]
fn the_game_and_the_bot_quote_prices_the_same_way() {
    // Two formatters for one currency is how a booking quotes one unit beside a balance in
    // another.
    let booking = PenalCode::book(["PC 100"], 1.0, 0);
    assert_eq!(
        lore::amount(i64::from(booking.bail)),
        booking.bail_label(&skin::police().words.money)
    );
}

#[test]
fn every_embed_carries_the_brand() {
    let plain = Embed::new().title("x").brand_as(BRAND, None);
    let with_footer = Embed::new()
        .title("x")
        .brand_as(BRAND, Some("Updated 14:02 Eastern"));
    assert_eq!(plain.footer.unwrap(), format!("{} {BRAND}", mark()));
    assert_eq!(
        with_footer.footer.unwrap(),
        format!("{} {BRAND} • Updated 14:02 Eastern", mark())
    );
}

#[test]
fn branding_twice_does_not_stack_the_name_up() {
    let embed = theme::notice("Title", Some("body"))
        .brand_as(BRAND, None)
        .brand_as(BRAND, Some("Updated 14:02 Eastern"));
    assert_eq!(
        embed.footer.unwrap(),
        format!("{} {BRAND} • Updated 14:02 Eastern", mark())
    );
}

#[test]
fn a_footer_set_before_branding_is_kept() {
    // /health set "build <commit>" and branding replaced it, so nobody could tell from
    // Discord whether a deploy had replaced the running process.
    let health = Embed::new()
        .title("Health")
        .footer("build b625e1d")
        .brand_as(BRAND, None);
    let monitor = theme::notice("Server status", Some("body"))
        .footer("3 server(s)")
        .brand_as(BRAND, None);
    assert_eq!(
        health.footer.unwrap(),
        format!("{} {BRAND} • build b625e1d", mark())
    );
    assert_eq!(
        monitor.footer.unwrap(),
        format!("{} {BRAND} • 3 server(s)", mark())
    );
}

#[test]
fn a_kept_footer_is_not_doubled_by_branding_again() {
    let embed = Embed::new()
        .title("x")
        .footer("build b625e1d")
        .brand_as(BRAND, None)
        .brand_as(BRAND, None);
    assert_eq!(
        embed.footer.unwrap(),
        format!("{} {BRAND} • build b625e1d", mark())
    );
}

#[test]
fn no_two_colours_in_the_palette_are_the_same() {
    // THE BAR IS READ BEFORE THE WORDS ARE: two entries on one hue merge two meanings.
    for skin in skin::all() {
        let mut seen: HashMap<u32, Vec<&str>> = HashMap::new();
        for (name, value) in skin.colors.entries() {
            seen.entry(value).or_default().push(name);
        }
        let duplicates: Vec<_> = seen.values().filter(|names| names.len() > 1).collect();
        assert!(duplicates.is_empty(), "{}: {duplicates:?}", skin.name);
    }
}
