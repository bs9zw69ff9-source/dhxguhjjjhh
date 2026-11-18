//! Port of `VpnVerdictTests.cs`: one verdict, worded one way. What is pinned is the
//! DISTINCTION, not the prose - likely, disputed, confirmed and clean stay four answers, because
//! collapsing any pair makes the bot sound certain about something it is not.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::vpn::verdict::{of, signals};
use pavlov_core::vpn::{VpnDecision, VpnRecord, VpnStance};

struct R {
    screen_hits: u32,
    screen_answered: u32,
    confirm_hits: u32,
    confirm_answered: u32,
    local: bool,
}

impl Default for R {
    fn default() -> Self {
        Self {
            screen_hits: 2,
            screen_answered: 3,
            confirm_hits: 0,
            confirm_answered: 1,
            local: false,
        }
    }
}

fn record(flagged: bool, confirmed: Option<bool>, actionable: bool, r: R) -> VpnRecord {
    VpnRecord {
        ip: "203.0.113.9".into(),
        local: r.local,
        decision: VpnDecision::new(flagged, confirmed, actionable, "test"),
        screen_hits: r.screen_hits,
        screen_answered: r.screen_answered,
        confirm_hits: r.confirm_hits,
        confirm_answered: r.confirm_answered,
        ..VpnRecord::default()
    }
}

// ---- the four answers stay four answers ----

#[test]
fn no_screening_is_not_checked_never_clean() {
    // Silence is not innocence.
    let summary = of(None);
    assert_eq!(summary.stance, VpnStance::NotChecked);
    assert_eq!(summary.headline, "NOT CHECKED");
    assert!(!summary.concerning());
}

#[test]
fn a_private_address_says_there_was_nothing_to_check() {
    let summary = of(Some(&record(
        false,
        None,
        false,
        R {
            local: true,
            ..R::default()
        },
    )));
    assert_eq!(summary.stance, VpnStance::Local);
    assert!(!summary.concerning());
}

#[test]
fn nothing_flagged_is_clean() {
    let summary = of(Some(&record(
        false,
        None,
        false,
        R {
            screen_hits: 0,
            ..R::default()
        },
    )));
    assert_eq!(summary.stance, VpnStance::Clean);
    assert_eq!(summary.headline, "CLEAN");
    assert!(!summary.concerning());
}

#[test]
fn flagged_with_no_confirmation_is_likely_not_confirmed() {
    // "FLAGGED (inconclusive)" read as a verdict when it is the absence of one.
    let summary = of(Some(&record(true, None, true, R::default())));
    assert_eq!(summary.stance, VpnStance::Likely);
    assert!(summary.headline.contains("LIKELY"));
    assert!(!summary.headline.contains("CONFIRMED"));
    assert!(summary.concerning());
}

#[test]
fn flagged_and_agreed_is_confirmed() {
    let summary = of(Some(&record(
        true,
        Some(true),
        true,
        R {
            confirm_hits: 1,
            ..R::default()
        },
    )));
    assert_eq!(summary.stance, VpnStance::Confirmed);
    assert!(summary.headline.contains("CONFIRMED"));
    assert!(summary.plain.contains("the confirming check agreed"));
}

#[test]
fn flagged_and_contradicted_is_disputed() {
    let summary = of(Some(&record(true, Some(false), false, R::default())));
    assert_eq!(summary.stance, VpnStance::Disputed);
    assert!(summary.plain.contains("cleared it"));
}

// ---- the action is separate from the verdict ----

#[test]
fn a_disputed_verdict_that_still_banned_says_banned() {
    let summary = of(Some(&record(true, Some(false), true, R::default())));
    assert_eq!(summary.stance, VpnStance::Disputed);
    assert!(summary.action.contains("AUTO-BANNED"));
    // And it must STILL not claim the confirmation supported the ban.
    assert!(summary.plain.contains("cleared it"));
}

#[test]
fn a_flag_below_the_threshold_says_not_banned() {
    let summary = of(Some(&record(true, None, false, R::default())));
    assert!(summary.action.contains("Not banned"));
    assert!(!summary.action.contains("AUTO-BANNED"));
}

// ---- the wording itself ----

#[test]
fn counts_are_words_rather_than_fractions() {
    let summary = of(Some(&record(
        true,
        Some(true),
        true,
        R {
            confirm_hits: 1,
            ..R::default()
        },
    )));
    assert!(summary.plain.contains("2 of 3 checks"));
    assert!(!summary.plain.contains("2/3"));
}

#[test]
fn one_check_is_singular() {
    let summary = of(Some(&record(
        true,
        None,
        false,
        R {
            screen_hits: 1,
            screen_answered: 1,
            ..R::default()
        },
    )));
    assert!(summary.plain.contains("1 of 1 check"));
    assert!(!summary.plain.contains("1 of 1 checks"));
}

#[test]
fn every_headline_is_upper_case_and_short() {
    // The only thing that has to be read, at a glance, beside a dozen other cards.
    let records = [
        None,
        Some(record(
            false,
            None,
            false,
            R {
                screen_hits: 0,
                ..R::default()
            },
        )),
        Some(record(true, None, true, R::default())),
        Some(record(
            true,
            Some(true),
            true,
            R {
                confirm_hits: 1,
                ..R::default()
            },
        )),
        Some(record(true, Some(false), false, R::default())),
        Some(record(
            false,
            None,
            false,
            R {
                local: true,
                ..R::default()
            },
        )),
    ];
    for r in &records {
        let headline = of(r.as_ref()).headline;
        assert_eq!(headline.to_uppercase(), headline);
        assert!(headline.chars().count() <= 26, "{headline}");
    }
}

// ---- signals ----

#[test]
fn only_asserted_signals_are_named() {
    // A missing answer is "the detector did not answer", never "no".
    let r = VpnRecord {
        vpn: Some(true),
        proxy: None,
        tor: Some(false),
        hosting: Some(true),
        ..record(true, None, false, R::default())
    };
    assert_eq!(signals(Some(&r)), ["VPN", "datacenter"]);
}

#[test]
fn no_record_has_no_signals() {
    assert!(signals(None).is_empty());
}
