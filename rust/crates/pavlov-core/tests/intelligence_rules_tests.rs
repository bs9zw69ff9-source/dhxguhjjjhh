//! Port of `IntelligenceRulesTests.cs`: faction activity. It flags and does not act; most of
//! these test the refusal to be confident.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
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
