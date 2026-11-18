//! Port of `MasterNamesConfigTests.cs`: a space separates two names, as a comma does.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use common::features;

fn names(value: &str) -> Vec<String> {
    features(&[("MASTER_NAMES", value)]).master_names
}

#[test]
fn spaces_and_commas_both_separate_names() {
    // "UserOne UserTwo" read as one name granted a player who does not exist.
    for value in [
        "UserOne UserTwo",
        "UserOne,UserTwo",
        " UserOne ,  UserTwo ",
        "UserOne\tUserTwo,userone",
    ] {
        assert_eq!(names(value), ["UserOne", "UserTwo"], "{value:?}");
    }
}

#[test]
fn the_reported_line_is_two_names() {
    assert_eq!(names("fki6 Holosight1"), ["fki6", "Holosight1"]);
}

#[test]
fn blank_is_no_masters() {
    assert!(names("").is_empty());
}
