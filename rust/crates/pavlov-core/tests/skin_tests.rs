//! Port of `SkinTests.cs`, the pure half: the police bot says police things, and the removed
//! fallout skin is refused BY NAME. The board and command halves come with the Discord crate.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::events::EventCategory;
use pavlov_core::events::mapping::category_of;
use pavlov_core::factions::FactionRegistry;
use pavlov_core::penal::PenalCode;
use pavlov_core::skin::{self, lore, theme};
use pavlov_core::text::Currency;

#[test]
fn police_is_the_default() {
    assert!(std::ptr::eq(skin::police(), skin::all()[0]));
    assert_eq!(skin::all().len(), 1);
}

#[test]
fn no_fallout_word_reaches_the_police_skin() {
    // A phrase copied across from the fallout skin and never reworded: a police bot quoting
    // caps, or telling somebody they are exiled.
    let fallout = [
        "Mojave",
        "wasteland",
        "caps",
        "Caps",
        "courier",
        "bount",
        "Bount",
        "xile",
        "Dossier",
        "gate",
        "Terminal",
    ];
    let words = &skin::police().words;
    let leaks: Vec<_> = words
        .phrases()
        .into_iter()
        .filter(|(_, text)| fallout.iter().any(|f| text.contains(f)))
        .collect();
    assert!(leaks.is_empty(), "{leaks:?}");
    assert_eq!(words.money, Currency::dollars());
}

#[test]
fn bail_is_quoted_in_the_same_money_as_a_balance() {
    let booking = PenalCode::book(["PC 100"], 1.0, 0);
    let label = booking.bail_label(&skin::police().words.money);
    assert_eq!(lore::amount(i64::from(booking.bail)), label);
    assert!(label.starts_with('$'));
}

#[test]
fn a_named_bot_name_still_wins() {
    // BOT_NAME is applied over the skin, so an explicit name beats the skin's own.
    let embed = theme::notice("x", None).brand_as("Vault 21 Security", None);
    assert_eq!(embed.footer.as_deref(), Some("🚔 Vault 21 Security"));
}

#[test]
fn a_skin_is_chosen_by_name() {
    for (setting, expected) in [("police", "police"), (" POLICE ", "police")] {
        assert_eq!(skin::named(Some(setting)).unwrap().name, expected);
    }
}

#[test]
fn a_name_that_is_not_a_skin_is_refused() {
    for setting in [Some("polcie"), Some(""), None] {
        assert!(skin::named(setting).is_none(), "{setting:?}");
    }
}

#[test]
fn the_removed_fallout_skin_is_refused_by_name() {
    // A .env that said fallout last week is not a typo: the startup error says it is gone,
    // and it must not resolve to police either.
    for setting in ["fallout", " Fallout "] {
        assert!(skin::named(Some(setting)).is_none());
        assert!(skin::is_removed(Some(setting)));
    }
}

#[test]
fn only_fallout_reads_as_removed() {
    for setting in [Some("police"), Some("polcie"), None] {
        assert!(!skin::is_removed(setting), "{setting:?}");
    }
}

#[test]
fn the_renamed_money_commands_still_file_under_economy() {
    for name in ["givemoney", "adjustmoney"] {
        assert_eq!(category_of(Some(name)), EventCategory::Economy);
    }
}

#[test]
fn the_default_factions_are_the_police() {
    assert!(std::ptr::eq(
        FactionRegistry::police(),
        FactionRegistry::default_set()
    ));
}

#[test]
fn no_fallout_faction_reaches_the_police_bot() {
    let fallout = ["NCR", "Legion", "Brotherhood of Steel", "Enclave"];
    let names: Vec<&str> = FactionRegistry::default_set().names().collect();
    assert!(
        !names
            .iter()
            .any(|n| fallout.iter().any(|f| f.eq_ignore_ascii_case(n))),
        "{names:?}"
    );
    for name in fallout {
        assert!(FactionRegistry::get(Some(name)).is_none(), "{name}");
    }
}
