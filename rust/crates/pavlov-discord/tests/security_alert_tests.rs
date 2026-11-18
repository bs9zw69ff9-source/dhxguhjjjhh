//! Port of `SecurityAlertTests.cs`, the delivery half: every recipient is tried, a closed inbox
//! is REPORTED rather than silent, and the card leads with what was done. The detectors' half
//! is in `pavlov-host`, the recipient list in `pavlov-config`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;

use chrono::{TimeZone, Utc};
use common::directory::FakeDirectory;
use pavlov_discord::direct_message_alerts::{DirectMessageAlerts, card};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::{SecurityAlert, SecurityAlertKind};

fn sample() -> SecurityAlert {
    SecurityAlert {
        kind: SecurityAlertKind::Evasion,
        player: "Evader".into(),
        headline: "Their join matched blacklisted ip 203.0.113.9 (left by a previous ban).".into(),
        action: "Banned permanently, on every server.".into(),
        account_id: Some("0002913af4f445df86da1be6a2a01728".into()),
        ip: Some("203.0.113.9".into()),
        server: Some("server1".into()),
        alts: vec!["SecondAccount".into()],
        at: Some(Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap()),
    }
}

fn sink(directory: &Arc<FakeDirectory>, recipients: Vec<u64>) -> DirectMessageAlerts {
    DirectMessageAlerts::new(
        Arc::clone(directory) as _,
        recipients,
        Arc::new(MetricsRegistry::default()),
    )
}

#[tokio::test]
async fn every_recipient_is_tried_and_a_closed_inbox_is_reported() {
    let directory = Arc::new(FakeDirectory::delivering(false));
    let results = sink(&directory, vec![11, 22]).deliver(&sample()).await;

    assert_eq!(directory.attempted(), [11, 22]);
    assert!(results.iter().all(|r| !r.delivered));
    // NOT silence: the reason is carried so /testalert can print it.
    assert!(results.iter().all(|r| {
        r.problem
            .as_deref()
            .unwrap()
            .contains("DMs are probably closed")
    }));
}

#[tokio::test]
async fn a_delivered_alert_says_so() {
    let directory = Arc::new(FakeDirectory::delivering(true));
    let results = sink(&directory, vec![11]).deliver(&sample()).await;

    assert_eq!(results.len(), 1);
    assert!(results[0].delivered);
    assert!(results[0].problem.is_none());
}

#[tokio::test]
async fn with_nobody_configured_nothing_is_sent_and_nothing_throws() {
    let directory = Arc::new(FakeDirectory::delivering(true));
    assert!(
        sink(&directory, Vec::new())
            .deliver(&sample())
            .await
            .is_empty()
    );
    assert!(directory.attempted().is_empty());
}

#[test]
fn the_card_leads_with_what_was_done_about_it() {
    let card = card(&sample());

    assert!(card.title.as_deref().unwrap().contains("Evader"));
    assert_eq!(card.fields[0].name, "What happened");
    assert!(card.fields[0].value.contains("Banned permanently"));
    // Named, not counted: the point of the alert is which accounts.
    assert!(
        card.fields
            .iter()
            .any(|f| f.value.contains("SecondAccount"))
    );
}
