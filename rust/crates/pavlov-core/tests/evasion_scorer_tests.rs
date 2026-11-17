//! Port of `EvasionScorerTests.cs` (itself ported from the Node bot's evasion tests).
//! Weak signals must never reach the threshold together by accident: entire ISPs share an
//! ASN, and millions of people share a VPN brand.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashSet;

use pavlov_core::evasion::{
    BanNetwork, BanRecord, EvasionScorer, JoinContext, RegistryFlags, SignalKind,
};

fn ban(player_id: &str, network: Option<BanNetwork>) -> BanRecord {
    BanRecord {
        player_id: player_id.to_owned(),
        reason: "griefing".to_owned(),
        moderator: "mod#1".to_owned(),
        permanent: true,
        expires: None,
        network,
    }
}

fn net() -> BanNetwork {
    BanNetwork::default()
}

fn strings(list: &[&str]) -> Vec<String> {
    list.iter().map(|s| (*s).to_owned()).collect()
}

fn join(name: &str) -> JoinContext {
    JoinContext {
        name: Some(name.to_owned()),
        ..JoinContext::default()
    }
}

#[test]
fn nothing_known_is_not_evasion() {
    let result = EvasionScorer::find(&join("Newcomer"), &[], None);
    assert!(!result.is_evasion());
    assert!(!result.is_certain());
    assert_eq!(result.score(), 0);
}

#[test]
fn same_eos_account_is_definitive_even_under_a_new_name() {
    let b = ban(
        "OldName",
        Some(BanNetwork {
            eos_ids: strings(&["0002abc"]),
            ..net()
        }),
    );
    let result = EvasionScorer::find(
        &JoinContext {
            eos_id: Some("0002abc".into()),
            ..join("BrandNewName")
        },
        &[b],
        None,
    );
    assert!(result.is_evasion());
    assert!(result.is_certain());
    assert_eq!(result.score(), 100);
    assert_eq!(result.matches[0].reasons[0].kind, SignalKind::Eos);
}

#[test]
fn banned_ip_scores_high_enough_alone() {
    let b = ban(
        "Griefer",
        Some(BanNetwork {
            ips: strings(&["203.0.113.5"]),
            ..net()
        }),
    );
    let result = EvasionScorer::find(
        &JoinContext {
            ip: Some("203.0.113.5".into()),
            ..join("Someone")
        },
        &[b],
        None,
    );
    assert!(result.is_evasion());
    assert!(!result.is_certain()); // strong, but households share addresses
    assert_eq!(result.score(), 70);
}

#[test]
fn exact_username_match_reaches_the_threshold() {
    let result = EvasionScorer::find(&join("Griefer"), &[ban("Griefer", None)], None);
    assert!(result.is_evasion());
    assert_eq!(result.score(), 50);
}

#[test]
fn known_alias_counts_as_a_username_match() {
    let b = ban(
        "Griefer",
        Some(BanNetwork {
            names: strings(&["GrieferAlt"]),
            ..net()
        }),
    );
    let result = EvasionScorer::find(
        &JoinContext {
            alt_names: strings(&["GrieferAlt"]),
            ..join("Unrelated")
        },
        &[b],
        None,
    );
    assert!(result.is_evasion());
    assert!(
        result.matches[0]
            .reasons
            .iter()
            .any(|r| r.kind == SignalKind::Username)
    );
}

#[test]
fn shared_asn_alone_never_reports() {
    let b = ban(
        "Griefer",
        Some(BanNetwork {
            asn: Some("AS7029".into()),
            ..net()
        }),
    );
    let result = EvasionScorer::find(
        &JoinContext {
            asn: Some("AS7029".into()),
            ..join("Innocent")
        },
        &[b],
        None,
    );
    assert!(!result.is_evasion());
    assert!(result.matches.is_empty());
}

#[test]
fn shared_provider_plus_asn_stays_below_the_threshold() {
    // 25 + 10 = 35 < 50, deliberately.
    let b = ban(
        "Griefer",
        Some(BanNetwork {
            provider: Some("NordVPN".into()),
            asn: Some("AS9009".into()),
            ..net()
        }),
    );
    let j = JoinContext {
        provider: Some("NordVPN".into()),
        asn: Some("AS9009".into()),
        ..join("Innocent")
    };
    assert!(!EvasionScorer::find(&j, &[b], None).is_evasion());
}

#[test]
fn shared_anonymising_asn_plus_provider_does_report() {
    // 25 + 20 = 45: still short, by arithmetic...
    let anon = || BanNetwork {
        provider: Some("NordVPN".into()),
        asn: Some("AS9009".into()),
        vpn: Some(true),
        ..net()
    };
    let j = JoinContext {
        provider: Some("NordVPN".into()),
        asn: Some("AS9009".into()),
        vpn: Some(true),
        ..join("Innocent")
    };
    assert!(!EvasionScorer::find(&j, &[ban("Griefer", Some(anon()))], None).is_evasion());

    // ...but any real signal on top crosses it.
    let with_ip = JoinContext {
        ip: Some("1.2.3.4".into()),
        ..j
    };
    let b = ban(
        "Griefer",
        Some(BanNetwork {
            ips: strings(&["1.2.3.4"]),
            ..anon()
        }),
    );
    assert!(EvasionScorer::find(&with_ip, &[b], None).is_evasion());
}

#[test]
fn null_anonymising_flags_do_not_count_as_anonymising() {
    // `None` is "no detector could answer"; reading it as true would upgrade a shared-ISP hit.
    let b = ban(
        "Griefer",
        Some(BanNetwork {
            asn: Some("AS7029".into()),
            vpn: None,
            hosting: None,
            ..net()
        }),
    );
    let j = JoinContext {
        asn: Some("AS7029".into()),
        vpn: None,
        hosting: None,
        ..join("Innocent")
    };
    assert!(!EvasionScorer::find(&j, &[b], None).is_evasion());
}

#[test]
fn matching_is_case_and_whitespace_insensitive() {
    let b = ban(
        "Griefer",
        Some(BanNetwork {
            names: strings(&["  GRIEFER  "]),
            ..net()
        }),
    );
    assert!(EvasionScorer::find(&join("griefer"), &[b], None).is_evasion());
}

#[test]
fn registry_flags_fire_without_any_ban_record() {
    let flags = RegistryFlags {
        eos_ids: HashSet::from(["0002abc".to_owned()]),
        ..RegistryFlags::default()
    };
    let result = EvasionScorer::find(
        &JoinContext {
            eos_id: Some("0002abc".into()),
            ..join("Whoever")
        },
        &[],
        Some(&flags),
    );
    assert!(result.is_evasion());
    assert!(result.is_certain());
    assert_eq!(result.score(), 100);
    assert_eq!(result.flags.len(), 1);
}

#[test]
fn a_flagged_ip_is_not_certain_on_its_own() {
    let flags = RegistryFlags {
        ips: HashSet::from(["203.0.113.5".to_owned()]),
        ..RegistryFlags::default()
    };
    let result = EvasionScorer::find(
        &JoinContext {
            ip: Some("203.0.113.5".into()),
            ..join("Whoever")
        },
        &[],
        Some(&flags),
    );
    assert!(result.is_evasion());
    assert!(!result.is_certain());
}

#[test]
fn strongest_match_is_reported_first() {
    let weak = ban(
        "WeakMatch",
        Some(BanNetwork {
            names: strings(&["Target"]),
            ..net()
        }),
    ); // 50
    let strong = ban(
        "StrongMatch",
        Some(BanNetwork {
            ips: strings(&["1.2.3.4"]),
            names: strings(&["Target"]),
            ..net()
        }),
    ); // 120
    let result = EvasionScorer::find(
        &JoinContext {
            ip: Some("1.2.3.4".into()),
            ..join("Target")
        },
        &[weak, strong],
        None,
    );
    assert_eq!(result.matches[0].player_id, "StrongMatch");
    assert_eq!(result.score(), 120);
    assert_eq!(result.matches[0].reasons[0].kind, SignalKind::Ip);
}

#[test]
fn corrupt_ban_rows_are_skipped_not_thrown() {
    let bans = [ban("", None), ban("   ", None), ban("Griefer", None)];
    assert_eq!(
        EvasionScorer::find(&join("Griefer"), &bans, None)
            .matches
            .len(),
        1
    );
}

#[test]
fn a_ban_with_no_network_snapshot_still_matches_on_name() {
    assert!(EvasionScorer::find(&join("Griefer"), &[ban("Griefer", None)], None).is_evasion());
}
