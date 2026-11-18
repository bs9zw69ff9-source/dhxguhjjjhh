//! Port of `PenalCodeTests.cs`, bail quoted in dollars as the C# now does.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashSet;

use pavlov_core::penal::{BailRule, CHARGES, PenalCode};
use pavlov_core::text::Currency;
use pavlov_core::text::case::fold_case;

fn dollars() -> Currency {
    Currency::dollars()
}

#[test]
fn the_charge_table_is_complete() {
    assert_eq!(CHARGES.len(), 72);
    assert_eq!(PenalCode::sections().len(), 8);
}

#[test]
fn every_section_has_the_right_number_of_charges() {
    let sections = PenalCode::sections();
    for (section, count) in [
        (100, 8),
        (200, 12),
        (300, 11),
        (400, 6),
        (500, 9),
        (600, 12),
        (700, 8),
        (800, 6),
    ] {
        assert_eq!(sections[&section].len(), count, "section {section}");
    }
}

#[test]
fn no_section_overflows_the_charge_picker() {
    // Discord caps a select at 25 options; a 26th charge would vanish from the menu only.
    for (number, _, count) in PenalCode::section_list() {
        assert!(count <= 25, "section {number} has {count} charges");
    }
}

#[test]
fn charges_match_the_sheet() {
    let rows = [
        ("PC 100", "Disorderly Conduct", 2, 10),
        ("PC 107", "Failure to Obey a Lawful Order", 3, 75),
        ("PC 205", "Aggravated Menacing", 6, 500),
        ("PC 210", "Homicide", 8, 250),
        ("PC 300", "Petty Theft (<$500)", 3, 400),
        ("PC 309", "Extortion", 6, 475),
        ("PC 403", "Possession of Explosives", 8, 500),
        ("PC 506", "Perjury", 6, 325),
        ("VC 601", "Reckless Driving", 3, 5),
        ("VC 606", "Driving Without a License", 2, 600),
    ];
    for (code, name, minutes, bail) in rows {
        let charge = PenalCode::get(Some(code)).unwrap_or_else(|| panic!("{code} missing"));
        assert_eq!(charge.name, name);
        assert_eq!(charge.jail_minutes, minutes, "{code}");
        assert_eq!(charge.bail, Some(bail), "{code}");
    }
}

#[test]
fn codes_are_unique_and_lookup_is_case_insensitive() {
    let codes: HashSet<String> = CHARGES.iter().map(|c| fold_case(c.code)).collect();
    assert_eq!(codes.len(), CHARGES.len());
    assert!(PenalCode::get(Some("PC 100")).is_some());
    assert!(PenalCode::get(Some("pc 100")).is_some());
    assert!(PenalCode::get(Some("PC 999")).is_none());
    assert!(PenalCode::get(None).is_none());
}

#[test]
fn sections_are_derived_from_the_hundreds_series() {
    assert_eq!(PenalCode::section_of("PC 104"), 100);
    assert_eq!(PenalCode::section_of("VC 604"), 600);
    assert_eq!(PenalCode::section_of("PC 707"), 700);
    assert_eq!(PenalCode::section_of("no digits here"), 0);
}

#[test]
fn every_section_has_a_title_and_the_counts_match() {
    let sections = PenalCode::sections();
    for (number, title, count) in PenalCode::section_list() {
        assert!(!title.trim().is_empty());
        assert!(!title.contains("Section "), "fallback title for {number}");
        assert_eq!(sections[&number].len(), count);
    }
}

#[test]
fn every_charge_is_internally_consistent() {
    for c in &CHARGES {
        assert!(!c.name.trim().is_empty());
        assert!(c.jail_minutes >= 0);
        // A figure and a rule cannot disagree.
        if c.rule == BailRule::Fixed {
            assert!(
                c.bail.is_some(),
                "{} is priced but carries no figure",
                c.code
            );
        } else {
            assert!(
                c.bail.is_none(),
                "{} carries a figure it should not have",
                c.code
            );
        }
        if c.sentence_ranges {
            assert_eq!(c.jail_minutes, 0);
        }
    }
}

#[test]
fn not_bailable_means_exactly_that() {
    let booking = PenalCode::book(["PC 211"], 1.0, 0);
    assert!(!booking.bailable);
    assert_eq!(
        booking.bail_label(&dollars()),
        "No bail - the sentence must be served"
    );
    assert_eq!(booking.jail_minutes, 10);
}

#[test]
fn one_non_bailable_charge_makes_the_whole_booking_non_bailable() {
    let booking = PenalCode::book(["VC 607", "PC 801"], 1.0, 0);
    assert!(!booking.bailable);
    assert_eq!(
        booking.bail_label(&dollars()),
        "No bail - the sentence must be served"
    );
}

#[test]
fn a_bailable_stack_still_adds_up() {
    let booking = PenalCode::book(["PC 100", "PC 200"], 1.0, 0); // 2min/$10 + 4min/$75
    assert!(booking.bailable);
    assert_eq!(booking.charges.len(), 2);
    assert_eq!(booking.jail_minutes, 6);
    assert_eq!(booking.bail, 85);
    assert_eq!(booking.bail_label(&dollars()), "$85");
}

#[test]
fn bail_from_the_associated_crime_says_so() {
    let alone = PenalCode::book(["PC 700"], 1.0, 0);
    assert!(alone.bailable);
    assert_eq!(
        alone.bail_label(&dollars()),
        "Based on the associated charge"
    );
    assert_eq!(alone.jail_minutes, 3);

    let with_others = PenalCode::book(["PC 100", "PC 700"], 1.0, 0);
    assert_eq!(
        with_others.bail_label(&dollars()),
        "$10 + based on the associated charge"
    );
}

#[test]
fn a_ranging_sentence_is_annotated_rather_than_guessed() {
    let booking = PenalCode::book(["PC 100", "PC 707"], 1.0, 0);
    assert!(booking.ranges);
    assert!(!booking.bailable);
    assert_eq!(
        booking.sentence_label(),
        "2 min + ranges with the associated charge"
    );
}

#[test]
fn a_ranging_charge_alone_has_no_fixed_jail_time() {
    assert_eq!(
        PenalCode::book(["PC 707"], 1.0, 0).sentence_label(),
        "ranges with the associated charge"
    );
}

#[test]
fn an_infraction_with_no_jail_is_still_a_real_charge() {
    let booking = PenalCode::book(["VC 600"], 1.0, 0);
    assert_eq!(booking.jail_minutes, 0);
    assert_eq!(booking.bail, 50);
    assert_eq!(booking.sentence_label(), "No jail time");
}

#[test]
fn unknown_codes_are_skipped_never_guessed_at() {
    let booking = PenalCode::book(["PC 100", "PC 999", "not a code"], 1.0, 0);
    assert_eq!(booking.charges.len(), 1);
    assert_eq!(booking.bail, 10);
}

#[test]
fn the_rate_scales_bail_and_is_rounded_per_charge() {
    // Both are $15: per charge 23 + 23 = 46, where summing first would give 45.
    assert_eq!(PenalCode::book(["PC 101", "PC 102"], 1.5, 0).bail, 46);
    assert_eq!(PenalCode::book(["PC 101"], 1.5, 0).bail, 23);
}

#[test]
fn a_rate_of_zero_makes_everything_free() {
    assert_eq!(PenalCode::book(["PC 100", "PC 200"], 0.0, 0).bail, 0);
}

#[test]
fn an_empty_booking_is_coherent() {
    let booking = PenalCode::book([], 1.0, 0);
    assert!(booking.charges.is_empty());
    assert!(booking.bailable);
    assert_eq!(booking.sentence_label(), "No jail time");
    assert_eq!(booking.bail_label(&dollars()), "$0");
}

#[test]
fn bail_is_formatted_with_separators() {
    let fixed = CHARGES
        .iter()
        .filter(|c| c.rule == BailRule::Fixed)
        .map(|c| c.code);
    let big = PenalCode::book(fixed, 10.0, 0);
    assert!(big.bailable);
    assert!(big.bail_label(&dollars()).contains(','));
}

#[test]
fn charge_bail_at_respects_null_figures() {
    let pc801 = PenalCode::get(Some("PC 801")).unwrap();
    assert_eq!(pc801.bail_at(2.0), None);
    assert!(!pc801.bailable());
    let pc100 = PenalCode::get(Some("PC 100")).unwrap();
    assert_eq!(pc100.bail_at(2.0), Some(20));
    assert!(pc100.bailable());
}
