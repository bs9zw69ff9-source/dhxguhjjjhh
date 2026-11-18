//! Port of `VpnAutoBanSwitchTests.cs`, the responder half (the `.env` default is in
//! `pavlov-config`). OFF STILL SCREENS AND STILL REPORTS: an operator has to see what WOULD
//! have been banned before deciding to let it.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use common::FakeHttp;
use futures_util::future::BoxFuture;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::moderation::BanRecord;
use pavlov_core::time::SystemClock;
use pavlov_core::vpn::{VpnDecision, VpnRecord};
use pavlov_host::feeds::FeedWebhooks;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::{AuditLog, BanService, MasterNames, VpnBanOutcome, VpnResponder};
use pavlov_host::registry::RconRegistry;
use pavlov_storage::datasets::TEMP_BANS;

const PLAYER: &str = "SomeVpnUser";

struct NoMasters;

impl MasterNames for NoMasters {
    fn is_master(&self, _: &str) -> bool {
        false
    }
    fn is_exempt(&self, _: &str) -> bool {
        false
    }
    fn is_protected(&self, _: &str) -> bool {
        false
    }
    fn exempt<'a>(&'a self, _: &'a str, _: Option<Duration>) -> BoxFuture<'a, ()> {
        Box::pin(async {})
    }
}

fn responder(store: &Arc<SerializedStore>, auto_ban: bool) -> VpnResponder {
    let metrics = Arc::new(MetricsRegistry::default());
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[],
        Duration::from_secs(60),
        Arc::clone(&metrics),
    ));
    let clock = Arc::new(SystemClock);
    VpnResponder::new(
        Arc::new(BanService::new(
            rcon,
            Arc::clone(store),
            Arc::new(NoMasters),
            clock.clone(),
        )),
        Arc::new(NoMasters),
        Arc::clone(store),
        Arc::new(AuditLog::new(Arc::clone(store), clock.clone())),
        Arc::new(FeedWebhooks::new(
            Arc::new(FakeHttp::canned(204, "")),
            Arc::clone(&metrics),
            clock.clone(),
        )),
        metrics,
        clock,
        auto_ban,
        None,
    )
}

fn store() -> Arc<SerializedStore> {
    Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())))
}

fn actionable() -> VpnRecord {
    VpnRecord {
        ip: "45.10.20.30".into(),
        decision: VpnDecision::new(
            true,
            Some(true),
            true,
            "confirmation agreed (1/1); 2 of 3 detector(s) flagged it, 2 required -> actionable",
        ),
        screen_hits: 2,
        screen_answered: 3,
        ..VpnRecord::default()
    }
}

fn bans(store: &SerializedStore) -> Vec<BanRecord> {
    store.read(TEMP_BANS, Vec::new()).unwrap()
}

#[tokio::test]
async fn with_the_switch_off_an_actionable_verdict_bans_nobody() {
    let store = store();
    let outcome = responder(&store, false)
        .respond(PLAYER, Some(&actionable()), None, "connection")
        .await;
    assert_eq!(outcome, VpnBanOutcome::Disabled);
    assert!(bans(&store).is_empty());
}

#[tokio::test]
async fn with_the_switch_on_the_same_verdict_still_bans() {
    let store = store();
    let outcome = responder(&store, true)
        .respond(PLAYER, Some(&actionable()), None, "connection")
        .await;
    assert_eq!(outcome, VpnBanOutcome::Banned);
    let bans = bans(&store);
    assert_eq!(bans.len(), 1);
    assert_eq!(bans[0].player_id, PLAYER);
    assert_eq!(bans[0].moderator.as_deref(), Some("auto"));
}

#[tokio::test]
async fn the_switch_is_checked_after_the_threshold_so_it_cannot_hide_a_weak_verdict() {
    let store = store();
    let weak = VpnRecord {
        decision: VpnDecision::new(
            true,
            None,
            false,
            "1 of 3 detector(s) flagged it, 2 required -> NOT actionable",
        ),
        ..actionable()
    };
    assert_eq!(
        responder(&store, false)
            .respond(PLAYER, Some(&weak), None, "connection")
            .await,
        VpnBanOutcome::BelowThreshold
    );
}

#[tokio::test]
async fn a_clean_verdict_is_still_nothing_to_do_either_way() {
    let store = store();
    let clean = VpnRecord {
        decision: VpnDecision::new(false, None, false, "all regular checks clean"),
        ..actionable()
    };
    assert_eq!(
        responder(&store, false)
            .respond(PLAYER, Some(&clean), None, "connection")
            .await,
        VpnBanOutcome::NoVerdict
    );
    assert_eq!(
        responder(&store, true)
            .respond(PLAYER, Some(&clean), None, "connection")
            .await,
        VpnBanOutcome::NoVerdict
    );
}

#[tokio::test]
async fn a_shared_exit_node_is_one_ban_and_an_existing_ban_is_never_rewritten() {
    // Rust-only: the rest of the order, end to end.
    let store = store();
    let r = responder(&store, true);
    assert_eq!(
        r.respond("First", Some(&actionable()), None, "join").await,
        VpnBanOutcome::Banned
    );
    assert_eq!(
        r.respond("Second", Some(&actionable()), None, "join").await,
        VpnBanOutcome::Debounced
    );
    assert_eq!(
        r.respond("First", Some(&actionable()), None, "disconnect")
            .await,
        VpnBanOutcome::AlreadyBanned
    );
    assert_eq!(bans(&store).len(), 1);
}
