//! Port of `BanLiftTests.cs`: lifting a ban, against the wire. The unban once named something
//! other than the ban, and the evidence outlived the ban, so a served temp ban came back as a
//! permanent one. Asserted against the fake server's recorded commands, never a format string.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use common::file_store;
use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::json::Stamp;
use pavlov_core::moderation::BanRecord;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::SystemClock;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::{BanEvidence, BanFileExport, BanService, MasterNames};
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;
use pavlov_storage::datasets::{TEMP_BANS, UNBAN_TOMBSTONES};

const NAME: &str = "Evader";
const ACCOUNT: &str = "0002a1b7c3d4e5f60718293a4b5c6d7e";

#[derive(Default)]
struct FakeEvidence {
    accounts: HashMap<String, String>,
    cleared: Mutex<Vec<String>>,
}

impl FakeEvidence {
    fn knowing(name: &str, id: &str) -> Self {
        Self {
            accounts: HashMap::from([(name.to_lowercase(), id.to_owned())]),
            ..Self::default()
        }
    }

    fn cleared(&self) -> Vec<String> {
        self.cleared.lock().unwrap().clone()
    }
}

impl BanEvidence for FakeEvidence {
    fn account_id_for(&self, name: &str) -> Option<String> {
        self.accounts.get(&name.to_lowercase()).cloned()
    }

    fn clear_flags<'a>(&'a self, account_id: &'a str) -> BoxFuture<'a, Result<(), String>> {
        self.cleared.lock().unwrap().push(account_id.to_owned());
        Box::pin(async { Ok(()) })
    }
}

/// Records exemptions and never protects anybody.
#[derive(Default)]
struct RecordingMasters {
    exempted: Mutex<Vec<String>>,
}

impl MasterNames for RecordingMasters {
    fn is_master(&self, _: &str) -> bool {
        false
    }
    fn is_exempt(&self, _: &str) -> bool {
        false
    }
    fn is_protected(&self, _: &str) -> bool {
        false
    }
    fn exempt<'a>(&'a self, name: &'a str, _: Option<Duration>) -> BoxFuture<'a, ()> {
        self.exempted.lock().unwrap().push(name.to_owned());
        Box::pin(async {})
    }
}

#[derive(Default)]
struct RecordingBanFile {
    exports: AtomicUsize,
    fail: AtomicBool,
}

impl BanFileExport for RecordingBanFile {
    fn export(&self) -> BoxFuture<'_, Result<usize, String>> {
        if self.fail.load(Ordering::SeqCst) {
            return Box::pin(async { Err("the ban file path is wrong".to_owned()) });
        }
        self.exports.fetch_add(1, Ordering::SeqCst);
        Box::pin(async { Ok(0) })
    }
}

struct Fixture {
    _dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    server: FakeRconServer,
}

impl Fixture {
    async fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        Self {
            store: file_store(dir.path()),
            _dir: dir,
            server: FakeRconServer::start().await.unwrap(),
        }
    }

    fn build(
        &self,
        masters: Arc<RecordingMasters>,
        evidence: Arc<FakeEvidence>,
        ban_file: Option<Arc<RecordingBanFile>>,
    ) -> BanService {
        let mut options = self.server.options();
        options.command_spacing = Duration::ZERO;
        options.read_cache_duration = Duration::ZERO;
        options.max_attempts = 1;
        let rcon = Arc::new(RconRegistry::with_system_clock(
            &[options],
            Duration::from_secs(60),
            Arc::new(MetricsRegistry::default()),
        ));
        let service = BanService::new(
            rcon,
            Arc::clone(&self.store),
            masters,
            Arc::new(SystemClock),
        )
        .with_evidence(evidence);
        match ban_file {
            Some(file) => service.with_ban_file(file),
            None => service,
        }
    }

    fn default_service(&self) -> BanService {
        self.build(Arc::default(), Arc::default(), None)
    }

    async fn seed(&self, record: BanRecord) {
        self.store.write(TEMP_BANS, &vec![record]).await.unwrap();
    }

    fn bans(&self) -> Vec<BanRecord> {
        self.store.read(TEMP_BANS, Vec::new()).unwrap()
    }

    fn commands(&self) -> Vec<String> {
        self.server.commands()
    }
}

fn temp_ban(expires: DateTime<Utc>, unique_id: Option<&str>) -> BanRecord {
    BanRecord {
        unique_id: unique_id.map(str::to_owned),
        reason: Some("griefing".into()),
        moderator: Some("mod".into()),
        at: Some(expires - TimeDelta::days(2)),
        expires: Some(expires),
        permanent: false,
        duration_label: Some("2d".into()),
        ..BanRecord::for_player(NAME)
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn an_expired_temp_ban_is_lifted_by_name_not_account_id() {
    let f = Fixture::new().await;
    f.seed(temp_ban(Utc::now() - TimeDelta::minutes(5), Some(ACCOUNT)))
        .await;

    let lifted = f.default_service().process_expired().await;

    assert_eq!(lifted.len(), 1);
    assert!(f.commands().contains(&format!("Unban {NAME}")));
    assert!(!f.commands().contains(&format!("Unban {ACCOUNT}")));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_record_is_lifted_by_name_not_the_recorded_id() {
    let f = Fixture::new().await;
    f.seed(temp_ban(Utc::now() - TimeDelta::minutes(5), None))
        .await;

    f.build(
        Arc::default(),
        Arc::new(FakeEvidence::knowing(NAME, ACCOUNT)),
        None,
    )
    .process_expired()
    .await;

    assert!(f.commands().contains(&format!("Unban {NAME}")));
    assert!(!f.commands().contains(&format!("Unban {ACCOUNT}")));
}

#[tokio::test(flavor = "multi_thread")]
async fn lifting_clears_the_flags_the_ban_created() {
    let f = Fixture::new().await;
    f.seed(temp_ban(Utc::now() - TimeDelta::minutes(5), Some(ACCOUNT)))
        .await;
    let evidence = Arc::new(FakeEvidence::default());

    f.build(Arc::default(), Arc::clone(&evidence), None)
        .process_expired()
        .await;

    assert_eq!(evidence.cleared(), [ACCOUNT]);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_manual_lift_and_an_expiry_do_the_same_thing() {
    let f = Fixture::new().await;
    f.seed(temp_ban(Utc::now() + TimeDelta::days(1), Some(ACCOUNT)))
        .await; // still in force
    let masters = Arc::new(RecordingMasters::default());
    let evidence = Arc::new(FakeEvidence::default());

    let result = f
        .build(Arc::clone(&masters), Arc::clone(&evidence), None)
        .lift(NAME, Some(ACCOUNT))
        .await;

    assert!(f.bans().is_empty()); // record gone
    assert_eq!(evidence.cleared(), [ACCOUNT]); // flags gone
    assert_eq!(*masters.exempted.lock().unwrap(), [NAME]); // exempt while anything lingers
    assert!(result.landed());
    assert!(f.commands().contains(&format!("Unban {NAME}")));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_ban_still_in_force_is_left_alone() {
    let f = Fixture::new().await;
    f.seed(temp_ban(Utc::now() + TimeDelta::days(1), Some(ACCOUNT)))
        .await;

    let lifted = f.default_service().process_expired().await;

    assert!(lifted.is_empty());
    assert_eq!(f.bans().len(), 1);
    assert!(!f.commands().iter().any(|c| c.starts_with("Unban")));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_permanent_ban_is_never_swept_away() {
    let f = Fixture::new().await;
    f.seed(BanRecord {
        unique_id: Some(ACCOUNT.into()),
        reason: Some("cheating".into()),
        moderator: Some("admin".into()),
        at: Some(Utc::now() - TimeDelta::days(400)),
        expires: None,
        permanent: true,
        duration_label: Some("Permanent".into()),
        ..BanRecord::for_player(NAME)
    })
    .await;

    assert!(f.default_service().process_expired().await.is_empty());
    assert_eq!(f.bans().len(), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_ban_is_enforced_against_the_name_not_the_recorded_id() {
    let f = Fixture::new().await;

    f.build(
        Arc::default(),
        Arc::new(FakeEvidence::knowing(NAME, ACCOUNT)),
        None,
    )
    .hard_enforce(NAME, Some(ACCOUNT), true, true)
    .await;

    assert!(f.commands().contains(&format!("Ban {NAME}")));
    assert!(!f.commands().contains(&format!("Ban {ACCOUNT}")));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_ban_the_server_refuses_is_not_counted_as_enforced() {
    // The kick that follows is not verified, so it must not stand in for the ban either.
    let f = Fixture::new().await;
    f.server.configure(|b| b.refuse_everything = true);

    let result = f
        .default_service()
        .hard_enforce("NeverSeen", None, true, true)
        .await;

    assert_eq!(result.servers, 0);
}

#[tokio::test(flavor = "multi_thread")]
async fn an_unknown_player_is_still_enforced_by_name() {
    let f = Fixture::new().await;
    f.default_service()
        .hard_enforce("NeverSeen", None, true, true)
        .await;
    assert!(f.commands().contains(&"Ban NeverSeen".to_owned()));
}

#[tokio::test(flavor = "multi_thread")]
async fn lifting_rewrites_the_games_ban_file() {
    let f = Fixture::new().await;
    f.seed(temp_ban(Utc::now() + TimeDelta::days(1), Some(ACCOUNT)))
        .await;
    let ban_file = Arc::new(RecordingBanFile::default());

    f.build(Arc::default(), Arc::default(), Some(Arc::clone(&ban_file)))
        .lift(NAME, Some(ACCOUNT))
        .await;

    assert_eq!(ban_file.exports.load(Ordering::SeqCst), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn lifting_records_a_tombstone_so_the_importer_cannot_resurrect_it() {
    let f = Fixture::new().await;
    f.seed(temp_ban(Utc::now() + TimeDelta::days(1), Some(ACCOUNT)))
        .await;

    f.default_service().lift(NAME, Some(ACCOUNT)).await;

    let tombstones: IgnoreCaseMap<Stamp> = f
        .store
        .read(UNBAN_TOMBSTONES, IgnoreCaseMap::default())
        .unwrap();
    assert!(tombstones.contains_key(NAME));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_failing_ban_file_export_does_not_fail_the_lift() {
    let f = Fixture::new().await;
    f.seed(temp_ban(Utc::now() + TimeDelta::days(1), Some(ACCOUNT)))
        .await;
    let ban_file = Arc::new(RecordingBanFile::default());
    ban_file.fail.store(true, Ordering::SeqCst);

    let result = f
        .build(Arc::default(), Arc::default(), Some(ban_file))
        .lift(NAME, Some(ACCOUNT))
        .await;

    assert!(result.landed());
    assert!(f.bans().is_empty());
}

#[tokio::test(flavor = "multi_thread")]
async fn the_sweep_removes_a_banned_player_who_is_online_and_spares_a_protected_one() {
    // Rust-only: the sweep against a real roster. Evader is banned and online; Owner is
    // banned too, but a master, so automation never touches them.
    struct Protecting;
    impl MasterNames for Protecting {
        fn is_master(&self, name: &str) -> bool {
            name == "Owner"
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
    let f = Fixture::new().await;
    f.server.configure(|b| {
        b.players = vec![
            (NAME.into(), ACCOUNT.into()),
            ("Owner".into(), "0002b".into()),
            ("Innocent".into(), "0002c".into()),
        ];
    });
    f.store
        .write(
            TEMP_BANS,
            &vec![
                BanRecord {
                    permanent: true,
                    ..BanRecord::for_player(NAME)
                },
                BanRecord {
                    permanent: true,
                    ..BanRecord::for_player("Owner")
                },
            ],
        )
        .await
        .unwrap();
    let mut options = f.server.options();
    options.read_cache_duration = Duration::ZERO;
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    rcon.refresh_rosters().await;
    let service = BanService::new(
        rcon,
        Arc::clone(&f.store),
        Arc::new(Protecting),
        Arc::new(SystemClock),
    );

    assert_eq!(service.enforce_sweep().await, 1);

    let commands = f.commands();
    assert!(commands.contains(&format!("Ban {NAME}")), "{commands:?}");
    assert!(commands.contains(&format!("Kick {NAME}")), "{commands:?}");
    assert!(
        !commands
            .iter()
            .any(|c| c.ends_with("Owner") || c.ends_with("Innocent")),
        "{commands:?}"
    );
}
