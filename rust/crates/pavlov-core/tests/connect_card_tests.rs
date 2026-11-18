//! Port of `ConnectCardTests.cs`: the connection card, which an earlier port had flattened
//! into a single line.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{TimeDelta, Utc};
use pavlov_core::cards::connect::{ConnectCardInput, build, sample};
use pavlov_core::embed::Embed;
use pavlov_core::evasion::AccountRecord;
use pavlov_core::vpn::{DetectorReading, VpnDecision, VpnRecord};

#[derive(Default)]
struct S {
    flagged: bool,
    confirmed: Option<bool>,
    vpn: Option<bool>,
    residential: Option<bool>,
    actionable: Option<bool>,
    detectors: Vec<DetectorReading>,
}

fn screened(s: S) -> VpnRecord {
    let count = |tier: i32, flagged: bool| {
        u32::try_from(
            s.detectors
                .iter()
                .filter(|d| d.tier == tier && (!flagged || d.flagged))
                .count(),
        )
        .unwrap()
    };
    VpnRecord {
        ip: "203.0.113.9".into(),
        // Actionable is INDEPENDENT of the confirmation: two screeners agreeing ban over a
        // confirmer's objection, so the card's tests have to set it.
        decision: VpnDecision::new(
            s.flagged,
            s.confirmed,
            s.actionable.unwrap_or(s.flagged),
            "test",
        ),
        vpn: s.vpn,
        residential: s.residential,
        asn: Some("AS577".into()),
        isp: Some("Bell Canada".into()),
        country: Some("Canada".into()),
        city: Some("Toronto".into()),
        screen_hits: count(1, true),
        screen_answered: count(1, false),
        confirm_hits: count(2, true),
        confirm_answered: count(2, false),
        detectors: s.detectors,
        ..VpnRecord::default()
    }
}

fn reading(name: &str, tier: i32, flagged: bool) -> DetectorReading {
    DetectorReading {
        name: name.into(),
        tier,
        flagged,
        ..DetectorReading::default()
    }
}

fn account(
    id: &str,
    confirmed: Vec<String>,
    guessed: Vec<String>,
    names: Vec<String>,
) -> AccountRecord {
    AccountRecord {
        confirmed_ips: confirmed,
        guessed_ips: guessed,
        names,
        ..AccountRecord::empty(id)
    }
}

#[derive(Default)]
struct R<'a> {
    vpn: Option<&'a VpnRecord>,
    account: Option<&'a AccountRecord>,
    alts: &'a [AccountRecord],
    flagged: bool,
    master: bool,
    unconfirmed: bool,
}

fn card(r: &R<'_>) -> Embed {
    build(&ConnectCardInput {
        name: "Pkdestroy",
        account_id: "76561198000000001",
        ip: Some("203.0.113.9"),
        confident_ip: !r.unconfirmed,
        server: "server1",
        account: r.account,
        alts: r.alts,
        vpn: r.vpn,
        flagged: r.flagged,
        master: r.master,
        at: Utc::now(),
    })
}

fn render(r: &R<'_>) -> String {
    let embed = card(r);
    let fields: Vec<String> = embed
        .fields
        .iter()
        .map(|f| format!("{}: {}", f.name, f.value))
        .collect();
    format!(
        "{}\n{}\n{}",
        embed.title.unwrap_or_default(),
        embed.description.unwrap_or_default(),
        fields.join("\n")
    )
}

fn assert_within_limits(embed: &Embed) {
    assert!(
        embed.length() <= 6000,
        "embed is {} characters",
        embed.length()
    );
    for f in &embed.fields {
        assert!(
            f.value.chars().count() <= 1024,
            "{} is {}",
            f.name,
            f.value.chars().count()
        );
    }
}

#[test]
fn the_card_carries_everything_the_one_liner_dropped() {
    let now = Utc::now();
    let mut acc = account(
        "76561198000000001",
        vec!["203.0.113.9".into(), "198.51.100.4".into()],
        vec![],
        vec!["Pkdestroy".into()],
    );
    acc.first_seen = Some(now - TimeDelta::days(30));
    acc.last_seen = Some(now);
    let vpn = screened(S {
        vpn: Some(true),
        residential: Some(false),
        ..S::default()
    });
    let alts = [account("2", vec![], vec![], vec!["OtherGuy".into()])];

    let text = render(&R {
        vpn: Some(&vpn),
        account: Some(&acc),
        alts: &alts,
        ..R::default()
    });
    for expected in [
        "IP address",
        "Account ID",
        "First seen",
        "Last seen",
        "Possible alts",
        "Network",
        "Detection",
        "VPN / proxy",
        "Location",
        "Recent addresses",
    ] {
        assert!(text.contains(expected), "missing {expected}");
    }
    assert!(text.contains("AS577"));
    assert!(text.contains("OtherGuy"));
    assert!(text.contains("198.51.100.4"));
}

#[test]
fn the_verdict_names_which_detectors_said_what() {
    let vpn = screened(S {
        flagged: true,
        detectors: vec![reading("iphub", 1, true), reading("ipapi.is", 1, false)],
        ..S::default()
    });
    let text = render(&R {
        vpn: Some(&vpn),
        ..R::default()
    });

    assert!(text.contains("iphub"));
    assert!(text.contains("ipapi.is"));
    assert!(text.contains('🔴'));
    assert!(text.contains('🟢'));
    // The headline leads, and it must not claim more than "likely" without a confirmer.
    assert!(text.contains("VPN / PROXY — LIKELY"));
    assert!(text.contains("No confirming check is configured"));
    assert!(!text.contains("CONFIRMED"));
}

#[test]
fn a_confirmed_verdict_says_so_and_shows_the_confirmer() {
    let vpn = screened(S {
        flagged: true,
        confirmed: Some(true),
        detectors: vec![reading("iphub", 1, true), reading("ipqs", 2, true)],
        ..S::default()
    });
    let text = render(&R {
        vpn: Some(&vpn),
        ..R::default()
    });

    assert!(text.contains("VPN / PROXY — CONFIRMED"));
    assert!(text.contains("the confirming check agreed"));
    assert!(text.contains("Confirmer:"));
    assert!(text.contains("ipqs"));
}

#[test]
fn a_disputed_verdict_that_did_not_ban_says_so() {
    let vpn = screened(S {
        flagged: true,
        confirmed: Some(false),
        actionable: Some(false),
        detectors: vec![reading("iphub", 1, true), reading("ipqs", 2, false)],
        ..S::default()
    });
    let text = render(&R {
        vpn: Some(&vpn),
        ..R::default()
    });

    assert!(text.contains("VPN / PROXY — DISPUTED"));
    assert!(text.contains("the confirming check cleared it"));
    assert!(text.contains("Not banned"));
    assert!(!text.contains("AUTO-BANNED"));
}

#[test]
fn a_disputed_verdict_that_banned_anyway_says_that_instead() {
    // Two screeners agreeing bans over the confirmer's objection, and the card must say so.
    let vpn = screened(S {
        flagged: true,
        confirmed: Some(false),
        actionable: Some(true),
        detectors: vec![
            reading("iphub", 1, true),
            reading("ipapi.is", 1, true),
            reading("ipqs", 2, false),
        ],
        ..S::default()
    });
    let text = render(&R {
        vpn: Some(&vpn),
        ..R::default()
    });

    assert!(text.contains("VPN / PROXY — DISPUTED"));
    assert!(text.contains("AUTO-BANNED"));
    assert!(!text.contains("Not banned"));
    assert!(text.contains("the confirming check cleared it"));
}

#[test]
fn unknown_is_its_own_answer_not_no() {
    let vpn = screened(S::default());
    let text = render(&R {
        vpn: Some(&vpn),
        ..R::default()
    });
    assert!(text.contains("VPN: unknown"));
    assert!(!text.contains("VPN: No"));
}

#[test]
fn an_unconfirmed_address_is_marked_as_such() {
    assert!(
        render(&R {
            unconfirmed: true,
            ..R::default()
        })
        .contains("unconfirmed")
    );
}

#[test]
fn a_master_account_says_it_is_protected() {
    assert!(
        render(&R {
            master: true,
            ..R::default()
        })
        .contains("master account")
    );
}

#[test]
fn a_flag_match_is_called_out() {
    assert!(
        render(&R {
            flagged: true,
            ..R::default()
        })
        .contains("Flagged")
    );
}

#[test]
fn no_screening_at_all_says_not_checked_rather_than_clean() {
    let text = render(&R::default());
    assert!(text.contains("NOT CHECKED"));
    assert!(!text.contains("CLEAN"));
}

#[test]
fn the_feed_test_card_is_a_valid_card() {
    let card = sample(Utc::now());
    assert_within_limits(&card);
    assert!(card.title.as_deref().is_some_and(|t| !t.trim().is_empty()));
}

#[test]
fn every_field_stays_inside_discords_limit() {
    // An over-long field makes Discord reject the WHOLE message.
    let alts: Vec<AccountRecord> = (0..200)
        .map(|i| {
            account(
                &i.to_string(),
                vec![],
                vec![],
                vec![format!("AltAccountWithALongName{i}")],
            )
        })
        .collect();
    let acc = account(
        "1",
        (0..200).map(|i| format!("203.0.113.{}", i % 256)).collect(),
        vec![],
        vec!["Pkdestroy".into()],
    );
    let vpn = screened(S::default());

    assert_within_limits(&card(&R {
        vpn: Some(&vpn),
        account: Some(&acc),
        alts: &alts,
        ..R::default()
    }));
}

#[test]
fn an_alt_whose_name_is_unprintable_does_not_take_the_card_down() {
    // A name of nothing but zero-width spaces sanitises to empty; the field must not be.
    let ghost = [account(
        "2",
        vec![],
        vec![],
        vec!["\u{200B}\u{200B}".into()],
    )];
    let embed = card(&R {
        alts: &ghost,
        ..R::default()
    });
    let alts = embed.field_named("Possible alts").unwrap();
    assert!(!alts.value.trim().is_empty());
}

#[test]
fn the_card_stays_valid_when_every_field_is_at_its_worst() {
    // Kept to keep it true: a card is biggest exactly on the players worth looking at.
    let long = "W".repeat(400);
    let mut loaded = screened(S {
        flagged: true,
        confirmed: Some(true),
        vpn: Some(true),
        actionable: Some(true),
        detectors: (0..40)
            .map(|i| reading(&format!("Detector-Number-{i}"), (i % 2) + 1, i % 3 == 0))
            .collect(),
        ..S::default()
    });
    loaded.city = Some(long.clone());
    loaded.region = Some(long.clone());
    loaded.country = Some(long.clone());
    loaded.isp = Some(long.clone());
    loaded.provider = Some(long.clone());
    let acc = account(
        "1",
        (0..300).map(|i| format!("203.0.113.{}", i % 256)).collect(),
        (0..300)
            .map(|i| format!("198.51.100.{}", i % 256))
            .collect(),
        vec!["Pkdestroy".into()],
    );
    let alts: Vec<AccountRecord> = (0..300)
        .map(|i| {
            account(
                &i.to_string(),
                vec![],
                vec![],
                vec![format!("{}{i}", "A".repeat(60))],
            )
        })
        .collect();
    let nines = "9".repeat(200);

    let embed = build(&ConnectCardInput {
        name: &long,
        account_id: &nines,
        ip: Some("203.0.113.9"),
        confident_ip: false,
        server: &long,
        account: Some(&acc),
        alts: &alts,
        vpn: Some(&loaded),
        flagged: true,
        master: true,
        at: Utc::now(),
    });
    assert_within_limits(&embed);
    for f in &embed.fields {
        assert!(!f.value.trim().is_empty(), "{}", f.name);
    }
    // The two things somebody opens this card FOR are charged first.
    assert!(embed.field_named("IP address").is_some());
    assert!(embed.field_named("Account ID").is_some());
}
