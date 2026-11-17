//! Port of `IntelligenceRulesTests.cs`: faction activity, economy fraud and staff anomaly.
//! ALL THREE FLAG AND NONE OF THEM ACT; most of these test the refusal to be confident.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use pavlov_core::economy::{FraudSignalKind, FraudSignals, FraudVerdict};
use pavlov_core::events::StaffAnomaly;
use pavlov_core::factions::{FactionActivity, FactionDefinition, FactionRegistry, MemberActivity};

fn now() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 8, 10, 12, 0, 0).unwrap()
}

fn nypd() -> &'static FactionDefinition {
    FactionRegistry::get(Some("NYPD")).unwrap()
}

fn member(name: &str, rank: &str, minutes: i64, days_ago: Option<i64>) -> MemberActivity {
    MemberActivity {
        player: name.into(),
        rank: rank.into(),
        minutes,
        last_seen: days_ago.map(|d| now() - TimeDelta::days(d)),
    }
}

// ---- faction activity ----

#[test]
fn recently_seen_members_count_as_active() {
    let report = FactionActivity::report(
        nypd(),
        &[
            member("Alice", "Cadet", 600, Some(1)),
            member("Bob", "Cadet", 600, Some(40)),
        ],
        now(),
    );
    assert_eq!(report.total, 2);
    assert_eq!(report.active, 1);
    assert_eq!(report.active_percent(), 50);
}

#[test]
fn long_idle_members_are_flagged_with_their_days() {
    let report =
        FactionActivity::report(nypd(), &[member("Bob", "Sergeant", 600, Some(45))], now());
    assert_eq!(report.inactive.len(), 1);
    assert_eq!(report.inactive[0].days, Some(45));
    assert!(report.inactive[0].reason.contains("45 days"));
}

#[test]
fn a_recent_but_barely_playing_member_is_still_flagged() {
    let report = FactionActivity::report(nypd(), &[member("Ghost", "Cadet", 4, Some(1))], now());
    assert_eq!(report.inactive.len(), 1);
    assert!(report.inactive[0].reason.contains("4 minutes"));
}

#[test]
fn a_member_with_no_record_is_reported_separately_rather_than_as_maximally_idle() {
    let report = FactionActivity::report(
        nypd(),
        &[
            member("Unknown", "Chief of Police", 0, None),
            member("Idle", "Cadet", 600, Some(60)),
        ],
        now(),
    );
    let unknown = report
        .inactive
        .iter()
        .find(|i| i.member.player == "Unknown")
        .unwrap();
    assert_eq!(unknown.days, None);
    assert!(unknown.reason.to_lowercase().contains("never seen"));
    assert!(unknown.reason.to_lowercase().contains("predate"));
}

#[test]
fn an_active_member_is_not_flagged_at_all() {
    assert!(
        FactionActivity::report(nypd(), &[member("Alice", "Cadet", 5000, Some(2))], now())
            .inactive
            .is_empty()
    );
}

#[test]
fn ranks_are_counted_highest_first() {
    let n = nypd();
    let report = FactionActivity::report(
        n,
        &[
            member("A", n.lowest(), 600, Some(1)),
            member("B", n.lowest(), 600, Some(1)),
            member("C", n.highest(), 600, Some(1)),
        ],
        now(),
    );
    assert_eq!(report.rank_counts[0].0, n.highest());
    assert_eq!(
        report
            .rank_counts
            .iter()
            .find(|(r, _)| r == n.lowest())
            .unwrap()
            .1,
        2
    );
}

// ---- economy fraud ----

fn assess(credits: &[i64], own: i64, server: i64, minutes: i64) -> FraudVerdict {
    FraudSignals::assess("Alice", credits, own, server, TimeDelta::minutes(minutes))
}

fn has(verdict: &FraudVerdict, kind: FraudSignalKind) -> bool {
    verdict.signals.iter().any(|s| s.kind == kind)
}

#[test]
fn small_earnings_are_never_flagged_however_large_the_ratio() {
    let v = assess(&[20, 20, 20], 5, 5, 15);
    assert!(!v.suspicious());
    assert!(v.signals.is_empty());
}

#[test]
fn earnings_far_above_their_own_baseline_are_flagged() {
    let v = assess(&[9000, 9000], 1000, 1500, 15);
    assert!(v.suspicious());
    assert!(has(&v, FraudSignalKind::FarAboveOwnBaseline));
}

#[test]
fn a_run_of_identical_credits_is_flagged() {
    assert!(has(
        &assess(&[500; 6], 400, 400, 15),
        FraudSignalKind::RepeatedIdenticalAmounts
    ));
}

#[test]
fn credits_arriving_faster_than_play_allows_are_flagged() {
    // 80 credits in 10 minutes is 8 per minute.
    let credits: Vec<i64> = (1..=80).map(|i| 50 + i).collect();
    assert!(has(
        &assess(&credits, 0, 500, 10),
        FraudSignalKind::ImpossibleFrequency
    ));
}

#[test]
fn a_good_night_within_normal_bounds_is_not_flagged() {
    // The control: the detector must be able to stay quiet.
    assert!(!assess(&[600, 450, 700, 520, 380, 610], 2500, 2800, 15).suspicious());
}

#[test]
fn a_run_shorter_than_the_threshold_is_not_flagged() {
    assert!(!has(
        &assess(&[700, 700, 700, 700, 1200, 900], 400, 400, 15),
        FraudSignalKind::RepeatedIdenticalAmounts
    ));
}

// ---- staff anomaly ----

#[test]
fn steady_high_volume_is_not_an_anomaly() {
    assert!(!StaffAnomaly::check(15, 240, TimeDelta::hours(24)).unusual);
}

#[test]
fn a_sharp_burst_against_their_own_baseline_is_flagged() {
    let v = StaffAnomaly::check(27, 48, TimeDelta::hours(24));
    assert!(v.unusual);
    assert!(v.explanation.contains("27"));
}

#[test]
fn a_small_burst_is_below_the_floor() {
    assert!(!StaffAnomaly::check(4, 5, TimeDelta::hours(24)).unusual);
}

#[test]
fn a_window_with_no_baseline_never_flags() {
    assert!(!StaffAnomaly::check(12, 12, TimeDelta::zero()).unusual);
    assert!(!StaffAnomaly::check(40, 40, TimeDelta::hours(1)).unusual);
    // The same 40 over a day, where a baseline exists, IS a burst.
    assert!(StaffAnomaly::check(40, 60, TimeDelta::hours(24)).unusual);
}
