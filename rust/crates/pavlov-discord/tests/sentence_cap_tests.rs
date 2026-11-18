//! Port of `SentenceCapTests.cs`: the per-booking jail cap, an officer's override, the
//! controlled-substances section, and the receipt line.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::penal::{BailRule, PenalCode, SECTION_TITLES};
use pavlov_core::text::Currency;
use pavlov_discord::commands::arrest::{charge_line, override_time};

const STACK: [&str; 4] = ["PC 403", "PC 302", "PC 301", "PC 300"];

fn book(codes: &[&str], cap: i32) -> pavlov_core::penal::Booking {
    PenalCode::book(codes.iter().copied(), 1.0, cap)
}

fn charge(code: &str) -> &'static pavlov_core::penal::Charge {
    PenalCode::get(Some(code)).unwrap()
}

#[test]
fn with_no_cap_nothing_changes() {
    let booking = book(&STACK, 0);
    assert_eq!(booking.jail_minutes, 22);
    assert!(!booking.capped);
}

#[test]
fn stacked_jail_is_clamped_to_the_cap() {
    let booking = book(&STACK, 15);
    assert_eq!(booking.jail_minutes, 15);
    assert!(booking.capped);
    assert_eq!(booking.cap_minutes, Some(15));
}

#[test]
fn bail_is_never_capped() {
    let uncapped = book(&STACK, 0);
    let capped = book(&STACK, 2);
    assert_eq!(capped.bail, uncapped.bail);
    assert!(capped.bail > 0);
    assert_eq!(capped.jail_minutes, 2);
}

#[test]
fn a_booking_under_the_cap_is_not_marked_capped() {
    let booking = book(&["PC 100"], 60);
    assert!(!booking.capped);
    assert_eq!(booking.cap_minutes, None);
    assert!(!booking.sentence_label().contains("capped"));
}

#[test]
fn the_sentence_label_says_when_it_was_capped() {
    assert!(book(&STACK, 15).sentence_label().contains("capped"));
}

#[test]
fn capping_does_not_make_a_non_bailable_booking_bailable() {
    let booking = book(&["PC 211", "PC 403"], 5);
    assert_eq!(booking.jail_minutes, 5);
    assert!(!booking.bailable);
}

#[test]
fn an_officers_time_replaces_the_computed_one() {
    assert_eq!(override_time(&book(&STACK, 0), 4, 0).jail_minutes, 4);
}

#[test]
fn the_override_leaves_bail_alone() {
    let computed = book(&STACK, 0);
    let overridden = override_time(&computed, 1, 0);
    assert_eq!(overridden.bail, computed.bail);
    assert_eq!(overridden.charges.len(), computed.charges.len());
}

#[test]
fn the_override_does_not_make_a_non_bailable_booking_bailable() {
    let overridden = override_time(&book(&["PC 801"], 0), 1, 0);
    assert!(!overridden.bailable);
    assert_eq!(
        overridden.bail_label(&Currency::dollars()),
        "No bail - the sentence must be served"
    );
}

#[test]
fn an_override_is_itself_held_to_the_cap() {
    let booking = override_time(&book(&STACK, 15), 99, 15);
    assert_eq!(booking.jail_minutes, 15);
    assert!(booking.capped);
}

#[test]
fn a_negative_override_is_floored_at_zero() {
    assert_eq!(override_time(&book(&STACK, 0), -5, 0).jail_minutes, 0);
}

#[test]
fn the_controlled_substances_section_exists() {
    let sections = PenalCode::sections();
    assert_eq!(sections.get(&800).map(Vec::len), Some(6));
    assert_eq!(
        SECTION_TITLES
            .iter()
            .find(|(n, _)| *n == 800)
            .map(|(_, t)| *t),
        Some("Controlled Substances")
    );
}

#[test]
fn every_controlled_substance_charge_is_served_not_paid() {
    for (code, minutes) in [
        ("PC 801", 3),
        ("PC 802", 5),
        ("PC 803", 6),
        ("PC 804", 8),
        ("PC 805", 3),
        ("PC 806", 4),
    ] {
        let charge = charge(code);
        assert_eq!(charge.jail_minutes, minutes, "{code}");
        assert_eq!(charge.rule, BailRule::None, "{code}");
        assert_eq!(charge.bail_at(1.0), None, "{code}");
    }
}

#[test]
fn a_drug_stack_still_serves_its_time() {
    let booking = book(&["PC 804", "PC 803"], 0);
    assert_eq!(booking.jail_minutes, 14);
    assert_eq!(booking.bail, 0);
    assert!(!booking.bailable);
}

#[test]
fn the_receipt_names_the_price_or_says_there_is_none() {
    assert_eq!(charge_line(charge("PC 100"), 1.0), "2 min, $10");
    assert_eq!(charge_line(charge("PC 801"), 1.0), "3 min, no bail");
    assert_eq!(charge_line(charge("VC 600"), 1.0), "no jail, $50");
    assert_eq!(
        charge_line(charge("PC 700"), 1.0),
        "3 min, bail from the associated charge"
    );
    assert_eq!(
        charge_line(charge("PC 707"), 1.0),
        "ranges with the associated charge, no bail"
    );
}

#[test]
fn the_receipt_shows_the_scaled_price() {
    assert_eq!(charge_line(charge("PC 100"), 2.0), "2 min, $20");
}
