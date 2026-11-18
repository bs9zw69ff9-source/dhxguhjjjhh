//! Port of `LedgerFileTests.cs`: recognising a caps ledger, and refusing to overwrite money
//! with nothing.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::sync::ledger_file::{balance, looks_like_ledger, would_wipe_balance};

#[test]
fn a_ledger_is_one_bare_non_negative_integer() {
    let cases = [
        ("0", true),
        ("1250", true),
        ("  42\n", true),
        ("", false),
        ("   ", false),
        ("-5", false), // a balance is never negative
        ("12.5", false),
        ("1,250", false),
        ("abc", false),
        ("EOS:0002abc", false),
    ];
    for (content, expected) in cases {
        assert_eq!(looks_like_ledger(Some(content)), expected, "{content:?}");
    }
}

#[test]
fn the_balance_is_parsed_when_it_looks_like_one() {
    assert_eq!(balance(Some(" 1250 ")), Some(1250));
    assert_eq!(balance(Some("not a number")), None);
}

#[test]
fn wiping_a_positive_balance_with_nothing_is_refused() {
    let cases = [
        // source empty/zero, destination a positive balance -> refuse, it would wipe caps
        ("", "500", true),
        ("0", "500", true),
        ("  ", "1", true),
        // source carries a real number -> a normal copy, whatever the destination is
        ("750", "500", false),
        ("0", "0", false), // destination has nothing to lose
        ("", "0", false),
        // destination is not a balance at all -> not this guard's business
        ("", "some config text", false),
        ("0", "", false),
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
    assert!(!would_wipe_balance(Some("0"), None));
    assert!(!would_wipe_balance(Some(""), None));
}
