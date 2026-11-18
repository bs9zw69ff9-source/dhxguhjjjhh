//! Port of `RotateMapTests.cs`, the unit-name half (`ServiceControl.IsPlausibleUnitName` and
//! `ParseUnits`). The rest of that file drives `systemctl` and the map rotation through a fake
//! process runner and arrives with `pavlov-host` (M5).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::servers::units::{DEFAULT_UNITS, is_plausible_unit_name, parse_units};

#[test]
fn real_unit_names_are_accepted() {
    for name in [
        "pavlovserver",
        "pavlovserver1",
        "pavlov-ttt",
        "pavlov_server.service",
        "pavlov@1.service",
    ] {
        assert!(is_plausible_unit_name(name), "{name}");
    }
}

#[test]
fn anything_that_is_not_a_unit_name_is_refused() {
    /* Belt to the argv brace. There is no shell - the arguments go in as an array - so none
    of these could execute anything even if they got through. They still do not get
    through, because the cost of being wrong about that reasoning exactly once is root. */
    for name in [
        "pavlovserver; rm -rf /",
        "pavlovserver && reboot",
        "pavlovserver\nreboot",
        "$(reboot)",
        "`reboot`",
        "pavlov server", // a space is two arguments
        "--force",       // an option, not a unit
        "-pavlovserver", // ditto, and it looks much more innocent
        "@@@",
        ".hidden",
        "",
    ] {
        assert!(!is_plausible_unit_name(name), "{name:?}");
    }
}

#[test]
fn unset_units_fall_back_to_the_defaults() {
    assert_eq!(parse_units(None), DEFAULT_UNITS);
    assert_eq!(parse_units(Some("   ")), DEFAULT_UNITS);
}

#[test]
fn configured_units_are_used_in_order() {
    // Order IS the server numbering, so it is preserved rather than sorted.
    assert_eq!(
        parse_units(Some("pavlov-c, pavlov-a, pavlov-b")),
        ["pavlov-c", "pavlov-a", "pavlov-b"]
    );
}

#[test]
fn a_legacy_docker_entry_keeps_its_slot_as_the_unit_of_the_same_name() {
    // An older, Docker-running build wrote these. Dropping one as invalid would shift every
    // server after it down a slot, so the prefix goes and the entry stays where it was.
    for configured in [
        "docker:pavlovserver, docker:pavlovserver1, docker:pavlovserver2",
        "DOCKER:pavlovserver,  pavlovserver1 ,docker:pavlovserver2",
    ] {
        assert_eq!(
            parse_units(Some(configured)),
            ["pavlovserver", "pavlovserver1", "pavlovserver2"]
        );
    }
}

#[test]
fn a_legacy_docker_entry_in_the_middle_does_not_renumber_the_servers_after_it() {
    assert_eq!(
        parse_units(Some("pavlovserver, docker:pavlovserver1, pavlovserver2")),
        ["pavlovserver", "pavlovserver1", "pavlovserver2"]
    );
    assert_eq!(
        parse_units(Some("pavlovserver, bad name, docker:pavlovserver2")),
        ["pavlovserver", "pavlovserver2"]
    );
}

#[test]
fn a_garbled_unit_in_the_list_is_dropped_rather_than_taking_the_rest_with_it() {
    assert_eq!(
        parse_units(Some("pavlovserver, bad name here, pavlovserver2")),
        ["pavlovserver", "pavlovserver2"]
    );
}

#[test]
fn a_list_with_nothing_usable_in_it_falls_back_rather_than_disabling_the_command() {
    /* An empty unit list would make /rotatemap and /serverswitch commands that report
    success for acting on nothing, which is the worst of the available outcomes. */
    assert_eq!(parse_units(Some("!!!, @@@")), DEFAULT_UNITS);
}

#[test]
fn duplicates_are_collapsed() {
    // Otherwise a copy-pasted .env restarts one server twice and reports it as two.
    assert_eq!(
        parse_units(Some("pavlovserver, pavlovserver")),
        ["pavlovserver"]
    );
}
