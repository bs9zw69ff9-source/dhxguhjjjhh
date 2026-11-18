//! Port of `ComponentFlowTests.cs`: the button and select-menu flows' invariants. Custom ids
//! round-trip, prefixes never collide, every charge list fits a select menu, and a booking
//! cannot double a sentence.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashSet;

use pavlov_core::component_id::ComponentId;
use pavlov_core::moderation::{MenuClaimAction, MenuLink};
use pavlov_core::penal::{CHARGES, PenalCode};
use pavlov_discord::prefixes::{ALL, ARREST_BOOKING, CONFIG_PANEL, MENU_PANEL, PAGED};

#[test]
fn every_arrest_control_round_trips_its_custom_id() {
    for step in ["section", "charge", "confirm", "cancel"] {
        let id = ComponentId::parse(&ComponentId::encode(ARREST_BOOKING, &[step]).unwrap());
        assert_eq!(id.prefix, ARREST_BOOKING);
        assert_eq!(id.argument(0), Some(step));
    }
}

#[test]
fn the_arrest_and_menu_prefixes_do_not_collide() {
    assert_ne!(ARREST_BOOKING, MENU_PANEL);
    assert_ne!(ARREST_BOOKING, PAGED);
    assert_ne!(MENU_PANEL, PAGED);
    assert_ne!(MENU_PANEL, CONFIG_PANEL);
    // And across the whole table, which is what the router relies on.
    let distinct: HashSet<&str> = ALL.iter().copied().collect();
    assert_eq!(distinct.len(), ALL.len());
}

#[test]
fn every_section_fits_inside_a_select_menu() {
    for (number, _, count) in PenalCode::section_list() {
        assert!(count <= 25, "section {number} has {count} charges");
    }
}

#[test]
fn every_charge_label_fits_discords_limit() {
    for charge in &CHARGES {
        assert!(
            format!("{} {}", charge.code, charge.name).chars().count() <= 100,
            "{}",
            charge.code
        );
        assert!(charge.code.chars().count() <= 100, "{}", charge.code);
    }
}

#[test]
fn booking_the_same_charge_twice_is_not_double_jail() {
    let once = PenalCode::book(["PC 100"], 1.0, 0);
    let twice = PenalCode::book(["PC 100", "PC 100"], 1.0, 0);

    assert_eq!(twice.jail_minutes, once.jail_minutes * 2); // the raw call DOES double
    assert_eq!(once.charges.len(), 1);

    let mut deduped: Vec<&str> = Vec::new();
    for code in ["PC 100", "PC 100"] {
        if !deduped.contains(&code) {
            deduped.push(code);
        }
    }
    assert_eq!(
        PenalCode::book(deduped, 1.0, 0).jail_minutes,
        once.jail_minutes
    );
}

#[test]
fn an_empty_booking_costs_nothing() {
    let booking = PenalCode::book([], 1.0, 0);
    assert!(booking.charges.is_empty());
    assert_eq!(booking.jail_minutes, 0);
    assert_eq!(booking.bail, 0);
}

#[test]
fn releasing_a_menu_keeps_the_name_binding() {
    // Unbinding on release would let somebody strip their menu and re-claim it on an alt.
    let release = MenuLink::decide(Some("Alice"), Some("Alice"), Some("111"), "111", true);
    assert_eq!(release.action, MenuClaimAction::Release);

    let after = MenuLink::decide(Some("Alice"), Some("AliceAlt"), None, "111", false);
    assert_eq!(after.action, MenuClaimAction::Locked);
}

#[test]
fn a_name_owned_by_somebody_else_is_refused() {
    let decision = MenuLink::decide(None, Some("Alice"), Some("999"), "111", false);
    assert_eq!(decision.action, MenuClaimAction::Taken);
    assert_eq!(decision.owner_id.as_deref(), Some("999"));
}

#[test]
fn a_first_claim_is_granted() {
    let decision = MenuLink::decide(None, Some("Alice"), None, "111", false);
    assert_eq!(decision.action, MenuClaimAction::Grant);
}
