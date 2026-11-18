//! Port of `ComponentIdTests.cs`: custom ids that route, refuse to truncate, and keep the Node
//! bot's persistent panel buttons working.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::component_id::{ComponentId, MAX_LENGTH};

#[test]
fn round_trips_prefix_and_arguments() {
    let id = ComponentId::parse(&ComponentId::encode("page", &["next", "3"]).unwrap());

    assert_eq!(id.prefix, "page");
    assert_eq!(id.argument(0), Some("next"));
    assert_eq!(id.argument_as_int(1, 0), 3);
}

#[test]
fn a_prefix_with_no_arguments_has_no_trailing_separator() {
    assert_eq!(ComponentId::encode("verify", &[]).unwrap(), "verify");
}

#[test]
fn missing_arguments_are_null_rather_than_an_index_error() {
    // Buttons from older builds may carry fewer arguments.
    let id = ComponentId::parse("page");

    assert_eq!(id.argument(0), None);
    assert_eq!(id.argument(5), None);
    assert_eq!(id.argument_as_int(0, 7), 7);
}

#[test]
fn an_overlong_id_is_rejected_rather_than_truncated() {
    let too_long = "x".repeat(MAX_LENGTH);
    let error = ComponentId::encode("page", &[&too_long]).unwrap_err();
    assert!(error.0.contains("100"));
}

#[test]
fn exactly_the_limit_is_allowed() {
    let argument = "x".repeat(MAX_LENGTH - "page:".len());
    assert_eq!(
        ComponentId::encode("page", &[&argument]).unwrap().len(),
        MAX_LENGTH
    );
}

#[test]
fn a_prefix_containing_the_separator_is_rejected() {
    assert!(ComponentId::encode("page:next", &["1"]).is_err());
}

#[test]
fn arguments_containing_the_separator_split_into_more() {
    // The sharp edge, documented: free text must be sanitised by the caller.
    let id = ComponentId::parse(&ComponentId::encode("cfg", &["a:b"]).unwrap());
    assert_eq!(id.argument(0), Some("a"));
    assert_eq!(id.argument(1), Some("b"));
}

#[test]
fn the_node_bots_persistent_panel_buttons_still_route() {
    let verify = ComponentId::parse("verify_start");
    assert_eq!(verify.prefix, "verify");
    assert_eq!(verify.argument(0), Some("start"));

    let menu = ComponentId::parse("menu_start");
    assert_eq!(menu.prefix, "menu");
    assert_eq!(menu.argument(0), Some("start"));
}

#[test]
fn transient_node_controls_are_not_translated() {
    assert_eq!(ComponentId::parse("arr_confirm").prefix, "arr_confirm");
    assert_eq!(ComponentId::parse("cfg_menu").prefix, "cfg_menu");
    assert_eq!(ComponentId::parse("pg_next").prefix, "pg_next");
}

#[test]
fn the_staff_decision_buttons_are_deliberately_not_translated() {
    // Their token points into the NODE bot's store; Accept would grant nothing.
    assert_eq!(
        ComponentId::parse("verifyreq_ok:abc123").prefix,
        "verifyreq_ok"
    );
}

#[test]
fn our_own_ids_are_unaffected_by_the_translation() {
    let id = ComponentId::parse(&ComponentId::encode("verify", &["ok", "a1b2c3"]).unwrap());
    assert_eq!(id.prefix, "verify");
    assert_eq!(id.argument(0), Some("ok"));
    assert_eq!(id.argument(1), Some("a1b2c3"));
}
