//! Port of `LedgerFileTests.cs` over the JSON saves: recognising a save by its contents, and
//! refusing to overwrite money with nothing. `ALedgerIsOneBareNonNegativeInteger` is dropped
//! with the text saves (owner, 2026-11-18); its JSON counterpart is below it.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::sync::ledger_file::{balance, looks_like_ledger, would_wipe_balance};

fn save(money: &str) -> String {
    format!("{{\n\t\"money\": {money},\n\t\"skin\": 3\n}}")
}

#[test]
fn a_save_is_a_json_object_with_a_non_negative_whole_money() {
    let cases = [
        (save("0"), true),
        (save("1250"), true),
        (String::new(), false),
        ("   ".to_owned(), false),
        (save("-5"), false), // a balance is never negative
        (save("12.5"), false),
        (save("\"1250\""), false),
        ("1250".to_owned(), false), // the old bare-number shape is not a save any more
        ("{\"skin\": 3}".to_owned(), false),
        ("[1, 2]".to_owned(), false),
        ("not json".to_owned(), false),
    ];
    for (content, expected) in cases {
        assert_eq!(looks_like_ledger(Some(&content)), expected, "{content:?}");
    }
}

#[test]
fn the_balance_is_parsed_when_it_looks_like_one() {
    assert_eq!(balance(Some(&save("1250"))), Some(1250));
    assert_eq!(balance(Some("not a number")), None);
}

#[test]
fn wiping_a_positive_balance_with_nothing_is_refused() {
    let (zero, five_hundred, one, seven_fifty) = (save("0"), save("500"), save("1"), save("750"));
    let cases = [
        // source empty/zero, destination a positive balance -> refuse, it would wipe money
        ("", five_hundred.as_str(), true),
        (zero.as_str(), five_hundred.as_str(), true),
        ("  ", one.as_str(), true),
        // source carries a real balance -> a normal copy, whatever the destination is
        (seven_fifty.as_str(), five_hundred.as_str(), false),
        (zero.as_str(), zero.as_str(), false), // destination has nothing to lose
        ("", zero.as_str(), false),
        // destination is not a save at all -> not this guard's business
        ("", "some config text", false),
        (zero.as_str(), "", false),
    ];
    for (source, destination, expected) in cases {
        assert_eq!(
            would_wipe_balance(Some(source), Some(destination)),
            expected,
            "{source:?} over {destination:?}"
        );
    }
}

#[test]
fn a_missing_destination_is_never_a_wipe() {
    // Copying into an install that has no file yet is exactly what the sync is for.
    assert!(!would_wipe_balance(Some(&save("0")), None));
    assert!(!would_wipe_balance(Some(""), None));
}
