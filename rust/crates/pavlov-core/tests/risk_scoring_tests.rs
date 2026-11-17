//! Port of `RiskScoringTests.cs`. Most of these are about the score REFUSING to be confident:
//! the failure that matters is a household sharing an address with somebody banned last year.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::intelligence::{
    RiskBand, RiskConfidence, RiskRecommendation, RiskScorer, RiskSignal, RiskSignalKind,
};

fn signal(kind: RiskSignalKind) -> RiskSignal {
    RiskSignal::new(
        kind,
        format!("test signal: {kind:?}"),
        Some("evidence".to_owned()),
        false,
    )
}

#[test]
fn no_signals_is_not_risky() {
    let a = RiskScorer::assess([]);
    assert_eq!(a.score, 0);
    assert_eq!(a.band(), RiskBand::None);
    assert_eq!(a.recommendation, RiskRecommendation::None);
}

#[test]
fn weak_signals_do_not_add_up_to_a_strong_verdict() {
    use RiskSignalKind::*;
    let a =
        RiskScorer::assess([Anonymising, ActiveWarnings, BrandNewAccount, PriorBan].map(signal));
    assert!(
        a.score < 55,
        "four weak signals reached {}, which reads as a finding",
        a.score
    );
    assert_ne!(a.recommendation, RiskRecommendation::Investigate);
}

#[test]
fn no_pile_of_weak_signals_overtakes_one_strong_signal() {
    use RiskSignalKind::*;
    let pile = RiskScorer::assess(
        [
            Anonymising,
            Anonymising,
            Anonymising,
            ActiveWarnings,
            BrandNewAccount,
        ]
        .map(signal),
    )
    .score;
    let one_strong = RiskScorer::assess([signal(SharedAddressWithBanned)]).score;
    assert!(
        pile < one_strong,
        "weak pile scored {pile}, one strong signal scored {one_strong}"
    );
}

#[test]
fn a_banned_account_scores_outright() {
    let a = RiskScorer::assess([signal(RiskSignalKind::BannedAccount)]);
    assert_eq!(a.score, 100);
    assert_eq!(a.confidence, RiskConfidence::High);
    assert_eq!(a.band(), RiskBand::Critical);
}

#[test]
fn repeats_of_one_kind_do_not_inflate_the_score() {
    let once = RiskScorer::assess([signal(RiskSignalKind::SharedAddressWithBanned)]);
    let thrice = RiskScorer::assess([RiskSignalKind::SharedAddressWithBanned; 3].map(signal));
    assert_eq!(once.score, thrice.score);
    assert_eq!(thrice.signals.len(), 3); // but all three are SHOWN
}

#[test]
fn independent_strong_signals_compound() {
    let one = RiskScorer::assess([signal(RiskSignalKind::SharedAddressWithBanned)]).score;
    let two = RiskScorer::assess([
        signal(RiskSignalKind::SharedAddressWithBanned),
        signal(RiskSignalKind::EvasionMatch),
    ])
    .score;
    assert!(two > one);
    assert!(two < 100, "two strong signals should not be certainty");
}

#[test]
fn the_score_stays_in_range() {
    let a = RiskScorer::assess(RiskSignalKind::ALL.map(signal));
    assert!((0..=100).contains(&a.score));
}

#[test]
fn one_weak_signal_is_low_confidence() {
    assert_eq!(
        RiskScorer::assess([signal(RiskSignalKind::Anonymising)]).confidence,
        RiskConfidence::Low
    );
}

#[test]
fn two_strong_signals_are_high_confidence() {
    let a = RiskScorer::assess([
        signal(RiskSignalKind::NameMatchesBanned),
        signal(RiskSignalKind::EvasionMatch),
    ]);
    assert_eq!(a.confidence, RiskConfidence::High);
}

#[test]
fn low_confidence_never_recommends_investigating() {
    for kind in RiskSignalKind::ALL {
        let a = RiskScorer::assess([signal(kind), signal(kind)]);
        if a.confidence == RiskConfidence::Low {
            assert_ne!(
                a.recommendation,
                RiskRecommendation::Investigate,
                "kind {kind:?}"
            );
        }
    }
}

#[test]
fn no_combination_of_signals_ever_recommends_a_ban() {
    for a in RiskSignalKind::ALL {
        for b in RiskSignalKind::ALL {
            let r = RiskScorer::assess([signal(a), signal(b)]).recommendation;
            assert!(
                r <= RiskRecommendation::Investigate,
                "{a:?} + {b:?} recommended {r:?}"
            );
        }
    }
    // The type has no value beyond Investigate: there is nothing that means "ban".
    assert_eq!(
        RiskRecommendation::ALL.iter().max(),
        Some(&RiskRecommendation::Investigate)
    );
}

#[test]
fn the_assessment_is_driven_by_the_strongest_signal_not_the_score() {
    use RiskSignalKind::*;
    let evasion = RiskScorer::assess([signal(SharedAddressWithBanned), signal(EvasionMatch)]);
    let warnings =
        RiskScorer::assess([ActiveWarnings, PriorBan, Anonymising, BrandNewAccount].map(signal));
    assert!(evasion.assessment.to_lowercase().contains("evasion"));
    assert_ne!(evasion.assessment, warnings.assessment);
}

#[test]
fn signals_are_ordered_strongest_first() {
    use RiskSignalKind::*;
    let a = RiskScorer::assess([BrandNewAccount, BannedAccount, Anonymising].map(signal));
    assert_eq!(a.signals[0].kind, BannedAccount);
}
