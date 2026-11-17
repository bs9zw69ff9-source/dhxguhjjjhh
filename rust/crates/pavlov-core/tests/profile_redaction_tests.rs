//! Port of `ProfileRedactionTests.cs`. AN ADDRESS POSTED TO A CHANNEL CANNOT BE UN-POSTED, so
//! the central test serialises the whole redacted profile and searches it: it catches the
//! field that has not been written yet.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::borrow::Cow;

use chrono::{TimeDelta, Utc};
use pavlov_core::intelligence::{
    Addresses, PlayerProfile, ProfileActivity, ProfileAssociation, ProfileEconomy, ProfileFaction,
    ProfileIdentity, ProfileModeration, ProfileNetwork, ProfileRedaction, ProfileVisibility,
    RiskScorer, RiskSignal, RiskSignalKind,
};

const ADDRESS: &str = "203.0.113.77";
const OTHER_ADDRESS: &str = "198.51.100.4";

fn full() -> PlayerProfile {
    PlayerProfile {
        identity: ProfileIdentity {
            current: "Alice".into(),
            account_id: Some("0002abc".into()),
            previous_names: vec!["OldAlice".into()],
        },
        activity: ProfileActivity {
            first_seen: Some(Utc::now() - TimeDelta::days(30)),
            last_seen: Some(Utc::now()),
            playtime_minutes: 120,
            online: false,
            servers: vec![],
        },
        network: ProfileNetwork {
            confirmed_ips: vec![ADDRESS.into()],
            guessed_ips: vec![OTHER_ADDRESS.into()],
            vpn: Some(true),
            proxy: Some(false),
            asn: Some("AS15169".into()),
            country: Some("US".into()),
        },
        moderation: ProfileModeration {
            active_ban: Some("griefing".into()),
            ban_expires: None,
            ban_is_permanent: true,
            warnings: 2,
            total_warnings: 5,
            kicks: 3,
            actions: vec!["ban by mod".into()],
        },
        faction: ProfileFaction {
            name: Some("NYPD".into()),
            rank: Some("Sergeant".into()),
            discord_id: Some(123_456_789_012_345_678),
        },
        economy: ProfileEconomy {
            balance: Some(5000),
            owed_wages: 250,
            lifetime_wages: 10_000,
        },
        associations: vec![ProfileAssociation {
            name: "Bob".into(),
            account_id: "0002def".into(),
            reason: "shared a confirmed address".into(),
            banned: true,
        }],
        risk: RiskScorer::assess([
            RiskSignal::new(
                RiskSignalKind::SharedAddressWithBanned,
                format!("Shares {ADDRESS} with banned player Bob."),
                Some(ADDRESS.into()),
                true,
            ),
            RiskSignal::new(
                RiskSignalKind::ActiveWarnings,
                "2 active warnings.",
                None,
                false,
            ),
        ]),
        redacted: false,
        combat: None,
    }
}

fn moderation_view(profile: &PlayerProfile) -> PlayerProfile {
    ProfileRedaction::for_visibility(profile, ProfileVisibility::Moderation).into_owned()
}

#[test]
fn a_redacted_profile_contains_no_address_anywhere() {
    let serialised = serde_json::to_string(&moderation_view(&full())).unwrap();
    assert!(!serialised.contains(ADDRESS));
    assert!(!serialised.contains(OTHER_ADDRESS));
}

#[test]
fn associations_are_withheld_entirely() {
    assert!(moderation_view(&full()).associations.is_empty());
}

#[test]
fn moderation_relevant_verdicts_survive() {
    let r = moderation_view(&full());
    assert_eq!(r.network.vpn, Some(true));
    assert_eq!(r.network.proxy, Some(false));
    assert_eq!(r.network.country.as_deref(), Some("US"));
    assert!(r.network.confirmed_ips.is_empty());
}

#[test]
fn the_score_does_not_change_with_the_viewer() {
    let f = full();
    let r = moderation_view(&f);
    assert_eq!(f.risk.score, r.risk.score);
    assert_eq!(f.risk.confidence, r.risk.confidence);
    assert_eq!(f.risk.signals.len(), r.risk.signals.len());
}

#[test]
fn a_sensitive_signal_keeps_its_reason_and_loses_its_evidence() {
    let r = moderation_view(&full());
    let signal = r.risk.signals.iter().find(|s| s.sensitive).unwrap();
    // The address is embedded in the SUMMARY on purpose: that was the first version's leak.
    assert!(signal.summary.contains("banned player Bob"));
    assert!(!signal.summary.contains(ADDRESS));
    assert!(signal.summary.contains(Addresses::PLACEHOLDER));
    assert_eq!(
        signal.evidence.as_deref(),
        Some("withheld - needs network access")
    );
}

#[test]
fn withholding_is_declared_rather_than_silent() {
    assert!(moderation_view(&full()).redacted);
}

#[test]
fn nothing_to_withhold_is_not_reported_as_withheld() {
    let bare = PlayerProfile {
        network: ProfileNetwork::default(),
        associations: vec![],
        risk: RiskScorer::assess([RiskSignal::new(
            RiskSignalKind::ActiveWarnings,
            "2 warnings.",
            None,
            false,
        )]),
        ..full()
    };
    assert!(!moderation_view(&bare).redacted);
}

#[test]
fn network_access_sees_everything() {
    // Not a copy: the very same profile, borrowed.
    let f = full();
    match ProfileRedaction::for_visibility(&f, ProfileVisibility::Network) {
        Cow::Borrowed(same) => assert!(std::ptr::eq(same, &f)),
        Cow::Owned(_) => panic!("network access got a rebuilt profile"),
    }
}

#[test]
fn visibility_follows_the_access_check() {
    assert_eq!(
        ProfileRedaction::visibility_for(true),
        ProfileVisibility::Network
    );
    assert_eq!(
        ProfileRedaction::visibility_for(false),
        ProfileVisibility::Moderation
    );
}

#[test]
fn moderation_and_faction_and_economy_are_not_redacted() {
    let f = full();
    let r = moderation_view(&f);
    assert_eq!(f.moderation, r.moderation);
    assert_eq!(f.faction, r.faction);
    assert_eq!(f.economy, r.economy);
    assert_eq!(f.identity, r.identity);
}
