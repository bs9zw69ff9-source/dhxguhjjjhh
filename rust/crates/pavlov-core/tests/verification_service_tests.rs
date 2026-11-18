//! Port of `VerificationServiceTests.cs`: the verification flow's ids round-trip and fit, and
//! the rules refuse a taken name at request time AND at decision time. The Discord flow itself
//! is in the discord crate.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use indexmap::IndexMap;
use pavlov_core::component_id::{ComponentId, MAX_LENGTH};
use pavlov_core::verification::{
    COMPONENT_PREFIX, VerificationConflict, VerificationRecord, VerificationRules,
};

const TOKEN: &str = "a1b2c3d4e5f6a7b8";

fn verified(id: &str, name: &str) -> IndexMap<String, VerificationRecord> {
    IndexMap::from([(
        id.to_owned(),
        VerificationRecord {
            name: name.into(),
            ips: vec!["203.0.113.9".into()],
        },
    )])
}

#[test]
fn every_step_of_the_flow_round_trips_its_custom_id() {
    // An unrouted component is not an error anywhere: the button just does nothing.
    let start = ComponentId::parse(&ComponentId::encode(COMPONENT_PREFIX, &["start"]).unwrap());
    assert_eq!(start.prefix, COMPONENT_PREFIX);
    assert_eq!(start.argument(0), Some("start"));

    let submit = ComponentId::parse(&ComponentId::encode(COMPONENT_PREFIX, &["submit"]).unwrap());
    assert_eq!(submit.argument(0), Some("submit"));

    let accept =
        ComponentId::parse(&ComponentId::encode(COMPONENT_PREFIX, &["ok", TOKEN]).unwrap());
    assert_eq!(accept.argument(0), Some("ok"));
    assert_eq!(accept.argument(1), Some(TOKEN));
}

#[test]
fn a_decision_id_fits_inside_discords_limit() {
    let id = ComponentId::encode(COMPONENT_PREFIX, &["ok", TOKEN]).unwrap();
    assert!(
        id.len() <= MAX_LENGTH,
        "decision id is {} characters",
        id.len()
    );
}

#[test]
fn carrying_the_name_and_the_requester_would_not_fit() {
    let long_name = "x".repeat(64);
    assert!(
        ComponentId::encode(
            COMPONENT_PREFIX,
            &["ok", &long_name, "76561198000000001", "123456789012345678"]
        )
        .is_err()
    );
    assert!(
        ComponentId::encode(COMPONENT_PREFIX, &["ok", TOKEN])
            .unwrap()
            .len()
            < 30
    );
}

#[test]
fn an_already_verified_account_is_refused_before_staff_see_it() {
    let verdict = VerificationRules::check(&verified("111", "Alice"), "111", Some("Bob"), &[]);
    assert_eq!(verdict.conflict, VerificationConflict::AlreadyVerified);
}

#[test]
fn the_name_is_rechecked_at_decision_time() {
    // Two members request one name; staff approve the first; the second card still has Accept.
    assert!(VerificationRules::check(&IndexMap::new(), "222", Some("Alice"), &[]).is_allowed());
    assert_eq!(
        VerificationRules::check(&verified("111", "Alice"), "222", Some("Alice"), &[]).conflict,
        VerificationConflict::NameTaken
    );
}

#[test]
fn a_member_with_no_confirmed_address_is_allowed_through() {
    assert!(VerificationRules::check(&IndexMap::new(), "333", Some("NewPlayer"), &[]).is_allowed());
}
