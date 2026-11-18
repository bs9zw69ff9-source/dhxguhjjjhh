//! Port of `LicenseTests.cs`, the pure part: the licence choices. The C#'s keyed text-save
//! tests (`ModSaveFileTests`) are dropped with that format (owner, 2026-11-18); the JSON save
//! that replaced it is tested below them and in pavlov-storage.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::licensing::Licenses;
use pavlov_core::sync::PlayerSave;

#[test]
fn a_choice_names_its_licenses() {
    let cases: [(Option<&str>, &[&str]); 6] = [
        (Some("drivers"), &["drivers_license"]),
        (Some("firearm"), &["firearm_license"]),
        (Some("all"), &["drivers_license", "firearm_license"]),
        (Some("ALL"), &["drivers_license", "firearm_license"]),
        (Some("pilot"), &[]),
        (None, &[]),
    ];
    for (choice, keys) in cases {
        let named: Vec<&str> = Licenses::named(choice).iter().map(|l| l.key).collect();
        assert_eq!(named, keys, "{choice:?}");
    }
}

// ---- the JSON save ----

const SAVE: &str = "{\n\t\"money\": 153,\n\t\"drivers_license\": false,\n\t\"firearm_license\": true,\n\t\"look\": {\n\t\t\"on\": true\n\t},\n\t\"skin\": 3,\n\t\"name\": \"Ricky\",\n\t\"pf_version\": 1,\n\t\"updated\": \"2026-11-18 15:44\"\n}\n";

#[test]
fn a_save_reads_its_money_licences_and_name() {
    let save = PlayerSave::parse(Some(SAVE)).unwrap();
    assert_eq!(save.money(), Some(153));
    assert_eq!(save.license("drivers_license"), Some(false));
    assert_eq!(save.license("firearm_license"), Some(true));
    assert_eq!(save.license("pilot_license"), None);
    assert_eq!(save.name(), Some("Ricky"));
}

#[test]
fn an_unedited_save_renders_byte_for_byte() {
    assert_eq!(PlayerSave::parse(Some(SAVE)).unwrap().render(), SAVE);
}

#[test]
fn an_edit_changes_its_fields_and_keeps_the_rest_in_order() {
    let edited = PlayerSave::parse(Some(SAVE))
        .unwrap()
        .with_money(200)
        .with_license("drivers_license", true)
        .touched("2026-11-19 09:30")
        .render();
    let expected = SAVE
        .replace("\"money\": 153", "\"money\": 200")
        .replace("\"drivers_license\": false", "\"drivers_license\": true")
        .replace("2026-11-18 15:44", "2026-11-19 09:30");
    assert_eq!(edited, expected);
}

#[test]
fn anything_without_a_whole_money_is_not_a_save() {
    for content in [
        "",
        "{}",
        "{\"money\": 1.5}",
        "{\"money\": \"5\"}",
        "[]",
        "Money\n10",
    ] {
        assert!(PlayerSave::parse(Some(content)).is_none(), "{content:?}");
    }
    assert!(PlayerSave::parse(None).is_none());
}

#[test]
fn a_name_cannot_inject_json() {
    let save = PlayerSave::parse(Some(SAVE))
        .unwrap()
        .touched("\",\"money\":999999,\"x\":\"");
    let reread = PlayerSave::parse(Some(&save.render())).unwrap();
    assert_eq!(reread.money(), Some(153));
}
