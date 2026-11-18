//! Port of `VpnResponderTests.cs`: acting on a VPN verdict, which nothing used to do. The ban
//! reason must not misstate its own evidence, and one shared exit node is one action.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashMap;

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use pavlov_core::vpn::{TierOutcome, VpnConsensus, VpnRecord, VpnThresholds};
use pavlov_host::moderation::vpn_responder::{label, should_action};

fn now() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap()
}

fn record(
    screen_hits: u32,
    screen_answered: u32,
    confirm_hits: u32,
    confirm_answered: u32,
    provider: Option<&str>,
) -> VpnRecord {
    let confirm =
        (confirm_answered > 0).then(|| TierOutcome::counts(confirm_hits, confirm_answered, 0));
    VpnRecord {
        ip: "45.1.1.1".into(),
        provider: provider.map(str::to_owned),
        decision: VpnConsensus::decide(
            Some(&TierOutcome::counts(screen_hits, screen_answered, 0)),
            confirm.as_ref(),
            u32::from(confirm_answered > 0),
            VpnThresholds::default(),
        ),
        screen_hits,
        screen_answered,
        confirm_hits,
        confirm_answered,
        checked_at: now(),
        ..VpnRecord::default()
    }
}

// ---- the ban reason ----

#[test]
fn a_ban_over_a_confirmers_objection_says_so() {
    // A ban record that misstates its own evidence is the thing an appeal turns on.
    let label = label(&record(2, 4, 0, 1, None));
    assert!(label.contains("DISPUTED"), "{label}");
    assert!(label.contains("the confirming check cleared it"), "{label}");
    assert!(!label.contains("agreed"), "{label}");
    assert!(label.contains("2 of 4 checks"), "{label}");
}

#[test]
fn a_confirmed_ban_cites_the_confirmation() {
    let label = label(&record(2, 4, 1, 1, None));
    assert!(label.contains("CONFIRMED"), "{label}");
    assert!(label.contains("the confirming check agreed"), "{label}");
    assert!(!label.contains("DISPUTED"), "{label}");
}

#[test]
fn a_consensus_ban_with_no_confirmer_says_there_was_none() {
    let label = label(&record(3, 4, 0, 0, None));
    assert!(label.contains("LIKELY"), "{label}");
    assert!(label.contains("3 of 4 checks"), "{label}");
    assert!(
        label.contains("No confirming check is configured"),
        "{label}"
    );
    assert!(!label.contains("CONFIRMED"), "{label}");
}

#[test]
fn the_provider_brand_is_named_when_known() {
    assert!(label(&record(2, 4, 0, 0, Some("NordVPN"))).contains("(NordVPN)"));
}

#[test]
fn a_provider_name_cannot_forge_the_ban_reason() {
    // A third-party string in a ban record and a plain-text feed line.
    let label = label(&record(2, 4, 0, 0, Some("Eve\n[VPN BAN] SomebodyElse")));
    assert!(!label.contains('\n'), "{label:?}");
}

// ---- the shared-exit-node debounce ----

#[test]
fn one_address_is_not_actioned_twice_in_the_window() {
    let mut seen = HashMap::new();
    let window = TimeDelta::hours(1);
    assert!(should_action(&mut seen, "45.1.1.1", now(), window));
    assert!(!should_action(
        &mut seen,
        "45.1.1.1",
        now() + TimeDelta::minutes(1),
        window
    ));
    assert!(!should_action(
        &mut seen,
        "45.1.1.1",
        now() + TimeDelta::minutes(59),
        window
    ));
}

#[test]
fn a_different_address_is_actioned_immediately() {
    let mut seen = HashMap::new();
    let window = TimeDelta::hours(1);
    assert!(should_action(&mut seen, "45.1.1.1", now(), window));
    assert!(should_action(&mut seen, "45.1.1.2", now(), window));
}

#[test]
fn the_same_address_is_actionable_again_after_the_window() {
    let mut seen = HashMap::new();
    let window = TimeDelta::hours(1);
    assert!(should_action(&mut seen, "45.1.1.1", now(), window));
    assert!(should_action(
        &mut seen,
        "45.1.1.1",
        now() + TimeDelta::hours(2),
        window
    ));
}

// ---- what actually reaches a ban ----

#[test]
fn two_detectors_make_a_verdict_actionable() {
    assert!(record(2, 4, 0, 0, None).decision.actionable);
    assert!(record(1, 4, 1, 1, None).decision.actionable);
    assert!(record(2, 4, 0, 1, None).decision.actionable); // confirmer dissented
}

#[test]
fn one_detector_never_is() {
    assert!(!record(1, 4, 0, 0, None).decision.actionable);
    assert!(!record(1, 4, 0, 1, None).decision.actionable);
    assert!(!record(0, 4, 0, 0, None).decision.actionable);
}
