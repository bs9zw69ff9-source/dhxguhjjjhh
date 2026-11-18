//! Port of `CommandBlacklistTests.cs`: the bar list's rules, the Node bot's old flag object
//! under the same key, and the refusal at dispatch. The `BLACKLIST_IDS` parsing test is in
//! `pavlov-config`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;

use common::{RecordingResponder, RecordingSuggester, Sent};
use pavlov_core::data::SerializedStore;
use pavlov_core::security::OwnerGuard;
use pavlov_discord::access::Access;
use pavlov_discord::barred::{Asker, refuse_if_barred};
use pavlov_discord::caller::Caller;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::{BarOutcome, CommandBlacklist};
use pavlov_storage::FileKeyValueBackend;
use pavlov_storage::datasets::{IP_FLAGS, USER_BLACKLIST, USER_UNBARRED};

const BARRED: u64 = 678_362_059_905_171_471;
const STRANGER: u64 = 555_000_000_000_000_001;
const OWNER: u64 = 444_000_000_000_000_001;

struct Fixture {
    _dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = Arc::new(SerializedStore::new(Arc::new(
            FileKeyValueBackend::new(dir.path()).unwrap(),
        )));
        Self { _dir: dir, store }
    }

    fn list(&self, configured: &[u64]) -> CommandBlacklist {
        CommandBlacklist::new(
            Arc::clone(&self.store),
            Arc::new(|id| id == OWNER),
            configured,
        )
    }

    fn stored(&self, key: &str) -> Vec<String> {
        self.store.read(key, Vec::new()).unwrap()
    }

    async fn seed_old_flag_object(&self, key: &str) {
        let flags =
            serde_json::json!({"ips": ["203.0.113.9"], "names": [], "ids": [], "manualIps": []});
        self.store.write(key, &flags).await.unwrap();
    }
}

#[tokio::test]
async fn a_bar_from_configure_bars_them() {
    let f = Fixture::new();
    let bars = f.list(&[]);

    assert_eq!(bars.bar(BARRED).await.outcome, BarOutcome::Barred);
    assert!(bars.is_barred(BARRED));
    assert!(!bars.is_barred(STRANGER));
    // The Node bot's format: an ARRAY of id strings.
    assert_eq!(f.stored(USER_BLACKLIST), ["678362059905171471"]);
}

#[test]
fn blacklist_ids_in_env_bar_them() {
    assert!(Fixture::new().list(&[BARRED]).is_barred(BARRED));
}

#[tokio::test]
async fn an_owner_is_never_barred() {
    let f = Fixture::new();
    f.store
        .write(USER_BLACKLIST, &vec![OWNER.to_string()])
        .await
        .unwrap();
    let bars = f.list(&[OWNER]);

    assert!(!bars.is_barred(OWNER));
    assert_eq!(bars.bar(OWNER).await.outcome, BarOutcome::Owner);
    assert!(bars.entries().is_empty());
}

#[tokio::test]
async fn unbarring_an_env_bar_records_an_override_that_survives_a_restart() {
    let f = Fixture::new();
    let bars = f.list(&[BARRED]);

    assert_eq!(bars.unbar(BARRED).await.outcome, BarOutcome::Unbarred);
    assert!(!bars.is_barred(BARRED));
    assert!(!f.list(&[BARRED]).is_barred(BARRED)); // a restart re-reads the same .env
    assert!(
        f.stored(USER_UNBARRED)
            .contains(&"678362059905171471".to_owned())
    );
}

#[tokio::test]
async fn unbarring_a_configure_bar_leaves_no_override_behind() {
    let f = Fixture::new();
    let bars = f.list(&[]);
    bars.bar(BARRED).await;
    bars.unbar(BARRED).await;

    assert!(!bars.is_barred(BARRED));
    assert!(f.stored(USER_BLACKLIST).is_empty());
    assert!(f.stored(USER_UNBARRED).is_empty());
    assert!(f.list(&[BARRED]).is_barred(BARRED));
}

#[tokio::test]
async fn barring_again_clears_the_override() {
    let f = Fixture::new();
    let bars = f.list(&[BARRED]);
    bars.unbar(BARRED).await;

    assert_eq!(bars.bar(BARRED).await.outcome, BarOutcome::Barred);
    assert!(bars.is_barred(BARRED));
    assert!(f.stored(USER_UNBARRED).is_empty());
}

#[tokio::test]
async fn a_bar_from_configure_outlives_the_env_entry() {
    let f = Fixture::new();
    f.list(&[BARRED]).bar(BARRED).await;
    assert!(f.list(&[]).is_barred(BARRED));
}

#[tokio::test]
async fn unbarring_somebody_not_barred_changes_nothing() {
    let f = Fixture::new();
    assert_eq!(
        f.list(&[]).unbar(STRANGER).await.outcome,
        BarOutcome::NotBarred
    );
    assert_eq!(f.store.read_raw(USER_UNBARRED).unwrap(), None);
    assert_eq!(f.store.read_raw(USER_BLACKLIST).unwrap(), None);
}

#[tokio::test]
async fn barring_twice_says_so() {
    let f = Fixture::new();
    let bars = f.list(&[]);
    bars.bar(BARRED).await;

    assert_eq!(bars.bar(BARRED).await.outcome, BarOutcome::AlreadyBarred);
    assert_eq!(f.stored(USER_BLACKLIST).len(), 1);
}

#[tokio::test]
async fn the_list_shows_both_sources_once() {
    let f = Fixture::new();
    let bars = f.list(&[BARRED, STRANGER]);
    bars.bar(BARRED).await;

    let entries = bars.entries();
    assert_eq!(entries.len(), 2);
    assert!(
        !entries
            .iter()
            .find(|e| e.id == "678362059905171471")
            .unwrap()
            .from_environment
    );
    assert!(
        entries
            .iter()
            .find(|e| e.id == "555000000000000001")
            .unwrap()
            .from_environment
    );
}

#[tokio::test]
async fn the_old_flag_object_bars_nobody_and_is_not_reported_as_unreadable() {
    let f = Fixture::new();
    f.seed_old_flag_object(USER_BLACKLIST).await;

    assert!(!f.list(&[]).is_barred(BARRED));
    assert!(!f.store.unreadable().contains(&USER_BLACKLIST.to_owned()));
}

#[tokio::test]
async fn the_old_flag_object_is_replaced_once_the_flags_live_elsewhere() {
    let f = Fixture::new();
    f.seed_old_flag_object(USER_BLACKLIST).await;
    f.seed_old_flag_object(IP_FLAGS).await;

    assert_eq!(f.list(&[]).bar(BARRED).await.outcome, BarOutcome::Barred);
    assert_eq!(f.stored(USER_BLACKLIST), ["678362059905171471"]);
}

#[tokio::test]
async fn the_old_flag_object_is_kept_while_it_is_the_only_copy_of_the_flags() {
    let f = Fixture::new();
    f.seed_old_flag_object(USER_BLACKLIST).await;
    let before = f.store.read_raw(USER_BLACKLIST).unwrap();

    let result = f.list(&[]).bar(BARRED).await;

    assert_eq!(result.outcome, BarOutcome::Failed);
    assert!(result.error.unwrap().contains("/configure"));
    assert_eq!(f.store.read_raw(USER_BLACKLIST).unwrap(), before);
}

#[tokio::test]
async fn a_barred_users_command_is_refused_privately_and_nothing_runs() {
    let f = Fixture::new();
    let bars = f.list(&[]);
    bars.bar(BARRED).await;
    let metrics = MetricsRegistry::default();
    let responder = RecordingResponder::default();

    let refused = refuse_if_barred(
        &bars,
        BARRED,
        "x",
        Asker::Interaction(&responder),
        "/permban",
        &metrics,
    )
    .await;

    assert!(refused);
    let sent = responder.sent();
    assert_eq!(sent.len(), 1);
    let Sent::Respond { reply, ephemeral } = &sent[0] else {
        panic!("expected one private reply, got {sent:?}");
    };
    assert!(*ephemeral);
    assert!(
        reply.embeds[0]
            .title
            .as_deref()
            .unwrap()
            .contains("Blacklisted")
    );
    assert!(metrics.render().contains("bot_interactions_barred_total 1"));
}

#[tokio::test]
async fn anyone_else_is_let_through_untouched() {
    let f = Fixture::new();
    let bars = f.list(&[]);
    bars.bar(BARRED).await;
    let responder = RecordingResponder::default();

    let refused = refuse_if_barred(
        &bars,
        STRANGER,
        "x",
        Asker::Interaction(&responder),
        "/permban",
        &MetricsRegistry::default(),
    )
    .await;

    assert!(!refused);
    assert!(responder.sent().is_empty());
}

#[tokio::test]
async fn an_owner_on_the_list_is_let_through() {
    let f = Fixture::new();
    let responder = RecordingResponder::default();
    assert!(
        !refuse_if_barred(
            &f.list(&[OWNER]),
            OWNER,
            "x",
            Asker::Interaction(&responder),
            "/configure",
            &MetricsRegistry::default()
        )
        .await
    );
}

#[tokio::test]
async fn a_barred_user_gets_no_autocomplete_suggestions() {
    let f = Fixture::new();
    let suggester = RecordingSuggester::default();

    let refused = refuse_if_barred(
        &f.list(&[BARRED]),
        BARRED,
        "x",
        Asker::Autocomplete(&suggester),
        "autocomplete",
        &MetricsRegistry::default(),
    )
    .await;

    assert!(refused);
    let offered = suggester.offered.lock().unwrap();
    assert_eq!(offered.len(), 1);
    assert!(offered[0].is_empty());
}

#[tokio::test]
async fn a_failed_reply_still_refuses() {
    let f = Fixture::new();
    let responder = RecordingResponder::failing();
    assert!(
        refuse_if_barred(
            &f.list(&[BARRED]),
            BARRED,
            "x",
            Asker::Interaction(&responder),
            "/permban",
            &MetricsRegistry::default()
        )
        .await
    );
}

#[test]
fn ownership_by_id_agrees_with_ownership_by_user() {
    let f = Fixture::new();
    let access = Access::new(Arc::clone(&f.store), [OWNER], [STRANGER + 1]).unwrap();
    for id in [
        OWNER,
        STRANGER + 1,
        OwnerGuard::SUPER_OWNER_ID,
        STRANGER,
        BARRED,
    ] {
        assert_eq!(
            access.is_owner(&Caller::user(id)),
            access.is_owner_id(id),
            "{id}"
        );
    }
}
