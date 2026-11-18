//! Port of `LicenseTests.cs`. So far the pure parts: the keyed save file (C# class
//! `ModSaveFileTests`) and the licence choices. `LicenseStoreTests` needs the ledger file store
//! (M4) and `LicenseCommandShapeTests` the command layer (M8); they join this file then.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::licensing::Licenses;
use pavlov_core::sync::ledger_file::{looks_like_ledger, would_wipe_balance};
use pavlov_core::sync::{ModSaveEditError, ModSaveFile};

/// Exactly what the police server's mod writes.
const POLICE_SAVE: &str = "Money\n0\nDriversLiscense\nfalse\nFirearmLiscense\nfalse";

fn parse(content: &str) -> ModSaveFile {
    ModSaveFile::parse(Some(content)).unwrap()
}

#[test]
fn the_keyed_shape_is_read() {
    let save = parse("Money\n1250\nDriversLiscense\ntrue\nFirearmLiscense\nfalse\n");

    assert!(!save.is_bare());
    assert_eq!(save.money(), Some(1250));
    assert!(save.is_true(Licenses::DRIVERS.key));
    assert!(!save.is_true(Licenses::FIREARM.key));
}

#[test]
fn the_bare_shape_still_reads() {
    let save = parse(" 500\n");

    assert!(save.is_bare());
    assert_eq!(save.money(), Some(500));
    assert_eq!(save.with_money(750).unwrap().to_string(), "750");
}

#[test]
fn an_unedited_file_comes_back_byte_for_byte() {
    let with_newline = format!("{POLICE_SAVE}\n");
    let cases = [
        POLICE_SAVE,
        with_newline.as_str(),
        "Money\r\n0\r\nDriversLiscense\r\nfalse\r\n",
        "Money\n0\nSomethingElse\n  keep me  \nFirearmLiscense\nfalse\n",
    ];
    for content in cases {
        assert_eq!(parse(content).to_string(), content);
    }
}

#[test]
fn an_edit_changes_one_value_and_nothing_else() {
    let before =
        "Money\r\n40\r\nDriversLiscense\r\ntrue\r\nUnknownKey\r\nx\r\nFirearmLiscense\r\ntrue\r\n";

    let after = parse(before)
        .with("firearmliscense", "false")
        .unwrap()
        .to_string();

    assert_eq!(
        after,
        before.replace("FirearmLiscense\r\ntrue", "FirearmLiscense\r\nfalse")
    );
}

#[test]
fn money_is_written_into_its_own_line() {
    assert_eq!(
        parse(POLICE_SAVE).with_money(900).unwrap().to_string(),
        "Money\n900\nDriversLiscense\nfalse\nFirearmLiscense\nfalse"
    );
}

#[test]
fn a_key_that_is_not_there_is_never_invented() {
    let save = parse("Money\n0\n");

    assert_eq!(
        save.with(Licenses::FIREARM.key, "false"),
        Err(ModSaveEditError::NoSuchKey)
    );
    assert!(parse("DriversLiscense\ntrue\n").with_money(5).is_none());
}

#[test]
fn a_neither_shape_file_is_not_a_save() {
    for content in [
        "",
        "Money\n0\nDriversLiscense", // odd: the game is mid-write
        "\n\nMoney\n0",              // a blank key
        "some config text",
    ] {
        assert!(ModSaveFile::parse(Some(content)).is_none(), "{content:?}");
    }
    assert!(ModSaveFile::parse(None).is_none());
}

#[test]
fn a_value_cannot_smuggle_in_a_new_line() {
    assert_eq!(
        parse(POLICE_SAVE).with("Money", "1\nFirearmLiscense"),
        Err(ModSaveEditError::MultiLineValue)
    );
    assert_eq!(
        parse(POLICE_SAVE).with("Money", "1\r"),
        Err(ModSaveEditError::MultiLineValue)
    );
}

#[test]
fn the_sync_guards_see_keyed_money() {
    // A fresh zero-money keyed file must not be copied over one with money in it.
    assert!(looks_like_ledger(Some(POLICE_SAVE)));
    assert!(would_wipe_balance(
        Some(POLICE_SAVE),
        Some("Money\n500\nDriversLiscense\ntrue\nFirearmLiscense\ntrue")
    ));
    assert!(!would_wipe_balance(
        Some("Money\n10\nDriversLiscense\nfalse"),
        Some("Money\n500\n")
    ));
}

#[test]
fn a_choice_names_its_licenses() {
    let cases: [(Option<&str>, &[&str]); 6] = [
        (Some("drivers"), &["DriversLiscense"]),
        (Some("firearm"), &["FirearmLiscense"]),
        (Some("all"), &["DriversLiscense", "FirearmLiscense"]),
        (Some("ALL"), &["DriversLiscense", "FirearmLiscense"]),
        (Some("pilot"), &[]),
        (None, &[]),
    ];
    for (choice, keys) in cases {
        let named: Vec<&str> = Licenses::named(choice).iter().map(|l| l.key).collect();
        assert_eq!(named, keys, "{choice:?}");
    }
}
