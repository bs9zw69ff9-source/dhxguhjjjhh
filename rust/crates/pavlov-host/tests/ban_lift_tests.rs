//! Port of `BanLiftTests.cs`, moved to save-file bans (owner, 2026-11-19): a ban is written
//! into the player's `<ModSave>/<name>.json`, lifting clears it, and the bot sends no RCON
//! `Ban` or `Unban` at all. Asserted against the save on disk and the fake server's recorded
//! commands, never a format string.
//!
//! Dropped with the behaviour: the ban-file export after a lift (and its failure case), and
//! the unban tombstone, which only the removed blacklist.txt importer read.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use common::file_store;
use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::BanRecord;
use pavlov_core::sync::UNBAN_DATE_FORMAT;
use pavlov_core::time::SystemClock;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::{BanEvidence, BanService, MasterNames};
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;
use pavlov_storage::datasets::TEMP_BANS;
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::ledger_store::{LedgerFileStore, SaveBanWrite};
use serde_json::Value;

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

/// A save shaped like the mod's (owner's sample, 2026-11-19), with no ban.
const SAVE: &str = r#"{
	"name": "Evader",
	"pf_version": 1,
	"updated": "2026-11-19 17:40",
	"money": 1500,
	"drivers_license": true,
	"firearm_license": false,
	"look": { "on": true, "shirt": 24 },
	"skin": 6,
	"weapons": ["/UGC5880638/.../SomeGun.SomeGun_C"],
	"ban": { "banned": false, "reason": "", "unban_date": "" }
}
"#;

struct Fixture {
    dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    server: FakeRconServer,
}

impl Fixture {
    async fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(dir.path().join("ModSave")).unwrap();
        Self {
            store: file_store(dir.path()),
            dir,
            server: FakeRconServer::start().await.unwrap(),
        }
    }

    fn modsave(&self) -> PathBuf {
        self.dir.path().join("ModSave")
    }

    fn save_path(&self, player: &str) -> PathBuf {
        self.modsave().join(format!("{player}.json"))
    }

    fn write_save(&self, player: &str) {
        std::fs::write(self.save_path(player), SAVE.replace("Evader", player)).unwrap();
    }

    fn save(&self, player: &str) -> Value {
        serde_json::from_str(&std::fs::read_to_string(self.save_path(player)).unwrap()).unwrap()
    }

    fn rcon(&self) -> Arc<RconRegistry> {
        let mut options = self.server.options();
        options.command_spacing = Duration::ZERO;
        options.read_cache_duration = Duration::ZERO;
        options.max_attempts = 1;
        Arc::new(RconRegistry::with_system_clock(
            &[options],
            Duration::from_secs(60),
            Arc::new(MetricsRegistry::default()),
        ))
    }

    fn saves(&self) -> Arc<LedgerFileStore> {
        Arc::new(LedgerFileStore::new(
            Some(self.modsave()),
            Arc::new(GameFileGuard::none()),
        ))
    }

    fn build(&self, masters: Arc<dyn MasterNames>, evidence: Arc<FakeEvidence>) -> BanService {
        self.build_on(self.rcon(), masters, evidence)
    }

    fn build_on(
        &self,
        rcon: Arc<RconRegistry>,
        masters: Arc<dyn MasterNames>,
        evidence: Arc<FakeEvidence>,
    ) -> BanService {
        BanService::new(
            rcon,
            Arc::clone(&self.store),
            masters,
            Arc::new(SystemClock),
        )
        .with_evidence(evidence)
        .with_saves(self.saves())
    }

    fn default_service(&self) -> BanService {
        self.build(Arc::new(RecordingMasters::default()), Arc::default())
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

    /// No RCON ban verb ever reaches a server any more.
    fn assert_no_rcon_ban(&self) {
        let commands = self.commands();
        assert!(
            !commands
                .iter()
                .any(|c| c.starts_with("Ban ") || c.starts_with("Unban ")),
            "{commands:?}"
        );
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

fn permanent_ban(player: &str, reason: &str) -> BanRecord {
    BanRecord {
        reason: Some(reason.into()),
        at: Some(Utc::now()),
        permanent: true,
        duration_label: Some("Permanent".into()),
        ..BanRecord::for_player(player)
    }
}

fn local_date(at: DateTime<Utc>) -> String {
    at.with_timezone(&chrono::Local)
        .format(UNBAN_DATE_FORMAT)
        .to_string()
}

fn ban_block(save: &Value) -> (bool, String, String) {
    let block = &save["ban"];
    (
        block["banned"].as_bool().unwrap(),
        block["reason"].as_str().unwrap().to_owned(),
        block["unban_date"].as_str().unwrap().to_owned(),
    )
}

#[tokio::test(flavor = "multi_thread")]
async fn a_temp_ban_is_written_into_the_players_save_and_nothing_else_changes() {
    let f = Fixture::new().await;
    f.write_save(NAME);
    let expires = Utc::now() + TimeDelta::days(1);
    f.seed(temp_ban(expires, Some(ACCOUNT))).await;

    let result = f
        .default_service()
        .hard_enforce(NAME, Some(ACCOUNT), true, true)
        .await;

    assert_eq!(result.save, Some(SaveBanWrite::Written));
    assert!(result.landed());
    let save = f.save(NAME);
    assert_eq!(
        ban_block(&save),
        (true, "griefing".into(), local_date(expires))
    );
    // Every other field is the mod's, and is kept.
    assert_eq!(save["money"], 1500);
    assert_eq!(save["look"]["shirt"], 24);
    assert_eq!(save["weapons"][0], "/UGC5880638/.../SomeGun.SomeGun_C");
    f.assert_no_rcon_ban();
}

#[tokio::test(flavor = "multi_thread")]
async fn a_permanent_ban_is_written_with_a_blank_unban_date() {
    let f = Fixture::new().await;
    f.write_save(NAME);
    f.seed(permanent_ban(NAME, "cheating")).await;

    f.default_service()
        .hard_enforce(NAME, None, true, true)
        .await;

    assert_eq!(
        ban_block(&f.save(NAME)),
        (true, "cheating".into(), String::new())
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_ban_kicks_by_name_not_the_recorded_id() {
    let f = Fixture::new().await;
    f.write_save(NAME);
    f.seed(permanent_ban(NAME, "cheating")).await;

    f.build(
        Arc::new(RecordingMasters::default()),
        Arc::new(FakeEvidence::knowing(NAME, ACCOUNT)),
    )
    .hard_enforce(NAME, Some(ACCOUNT), true, true)
    .await;

    assert!(f.commands().contains(&format!("Kick {NAME}")));
    assert!(!f.commands().contains(&format!("Kick {ACCOUNT}")));
    f.assert_no_rcon_ban();
}

#[tokio::test(flavor = "multi_thread")]
async fn a_player_with_no_save_is_kicked_and_the_ban_waits_for_the_file() {
    let f = Fixture::new().await;
    f.seed(permanent_ban("NeverJoined", "alt")).await;

    // Asked to ban WITHOUT a kick: with no save to write, the kick happens anyway.
    let result = f
        .default_service()
        .hard_enforce("NeverJoined", None, true, false)
        .await;

    assert!(result.pending());
    assert!(!result.landed());
    assert!(f.commands().contains(&"Kick NeverJoined".to_owned()));
    assert!(
        !f.save_path("NeverJoined").exists(),
        "the bot never creates a save"
    );
    f.assert_no_rcon_ban();
}

#[tokio::test(flavor = "multi_thread")]
async fn the_sweep_writes_a_waiting_ban_once_the_game_creates_the_save() {
    let f = Fixture::new().await;
    f.seed(permanent_ban(NAME, "alt account")).await;
    let service = f.default_service();
    assert!(service.hard_enforce(NAME, None, true, true).await.pending());

    f.write_save(NAME); // they joined; the game made their save
    service.enforce_sweep().await;

    assert_eq!(
        ban_block(&f.save(NAME)),
        (true, "alt account".into(), String::new())
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn the_sweep_puts_back_a_ban_the_game_overwrote() {
    let f = Fixture::new().await;
    f.write_save(NAME);
    f.seed(permanent_ban(NAME, "cheating")).await;
    let service = f.default_service();
    service.hard_enforce(NAME, None, true, true).await;

    f.write_save(NAME); // the mod saved them from memory: banned is false again
    service.enforce_sweep().await;

    assert!(ban_block(&f.save(NAME)).0);
}

#[tokio::test(flavor = "multi_thread")]
async fn without_a_modsave_directory_a_ban_is_not_reported_enforced() {
    let f = Fixture::new().await;
    f.seed(permanent_ban(NAME, "cheating")).await;
    let service = BanService::new(
        f.rcon(),
        Arc::clone(&f.store),
        Arc::new(RecordingMasters::default()),
        Arc::new(SystemClock),
    );

    let result = service.hard_enforce(NAME, None, true, true).await;

    assert_eq!(result.save, None);
    assert!(!result.landed(), "a kick alone is not a ban");
    assert!(result.summary().contains("MODSAVE_PATH"));
    f.assert_no_rcon_ban();
}

#[tokio::test(flavor = "multi_thread")]
async fn an_expired_temp_ban_is_lifted_and_cleared_from_the_save() {
    let f = Fixture::new().await;
    f.write_save(NAME);
    f.seed(temp_ban(Utc::now() + TimeDelta::days(1), Some(ACCOUNT)))
        .await;
    let service = f.default_service();
    service.hard_enforce(NAME, None, true, true).await;
    f.seed(temp_ban(Utc::now() - TimeDelta::minutes(5), Some(ACCOUNT)))
        .await; // now served

    let lifted = service.process_expired().await;

    assert_eq!(lifted.len(), 1);
    assert!(f.bans().is_empty());
    assert_eq!(
        ban_block(&f.save(NAME)),
        (false, String::new(), String::new())
    );
    f.assert_no_rcon_ban();
}

#[tokio::test(flavor = "multi_thread")]
async fn lifting_clears_the_flags_the_ban_created() {
    let f = Fixture::new().await;
    f.seed(temp_ban(Utc::now() - TimeDelta::minutes(5), Some(ACCOUNT)))
        .await;
    let evidence = Arc::new(FakeEvidence::default());

    f.build(Arc::new(RecordingMasters::default()), Arc::clone(&evidence))
        .process_expired()
        .await;

    assert_eq!(evidence.cleared(), [ACCOUNT]);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_manual_lift_and_an_expiry_do_the_same_thing() {
    let f = Fixture::new().await;
    f.write_save(NAME);
    f.seed(temp_ban(Utc::now() + TimeDelta::days(1), Some(ACCOUNT)))
        .await; // still in force
    let masters = Arc::new(RecordingMasters::default());
    let evidence = Arc::new(FakeEvidence::default());
    let service = f.build(
        Arc::clone(&masters) as Arc<dyn MasterNames>,
        Arc::clone(&evidence),
    );
    service.hard_enforce(NAME, None, true, true).await;

    let result = service.lift(NAME, Some(ACCOUNT)).await;

    assert!(f.bans().is_empty()); // record gone
    assert_eq!(evidence.cleared(), [ACCOUNT]); // flags gone
    assert_eq!(*masters.exempted.lock().unwrap(), [NAME]); // exempt while anything lingers
    assert!(result.landed());
    assert!(!ban_block(&f.save(NAME)).0); // and the save no longer bans them
    f.assert_no_rcon_ban();
}

#[tokio::test(flavor = "multi_thread")]
async fn lifting_a_player_with_no_save_still_lands() {
    let f = Fixture::new().await;
    f.seed(permanent_ban("NeverJoined", "alt")).await;

    let result = f.default_service().lift("NeverJoined", None).await;

    assert!(result.landed());
    assert!(f.bans().is_empty());
}

#[tokio::test(flavor = "multi_thread")]
async fn a_ban_still_in_force_is_left_alone() {
    let f = Fixture::new().await;
    f.write_save(NAME);
    f.seed(temp_ban(Utc::now() + TimeDelta::days(1), Some(ACCOUNT)))
        .await;
    let service = f.default_service();
    service.hard_enforce(NAME, None, true, true).await;

    let lifted = service.process_expired().await;

    assert!(lifted.is_empty());
    assert_eq!(f.bans().len(), 1);
    assert!(ban_block(&f.save(NAME)).0);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_permanent_ban_is_never_swept_away() {
    let f = Fixture::new().await;
    f.seed(BanRecord {
        unique_id: Some(ACCOUNT.into()),
        at: Some(Utc::now() - TimeDelta::days(400)),
        ..permanent_ban(NAME, "cheating")
    })
    .await;

    assert!(f.default_service().process_expired().await.is_empty());
    assert_eq!(f.bans().len(), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn the_sweep_kicks_a_banned_player_who_is_online_and_spares_a_protected_one() {
    // Evader is banned and online; Owner is banned too, but a master, so automation never
    // touches them: not a kick, not their save.
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
    f.write_save(NAME);
    f.write_save("Owner");
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
            &vec![permanent_ban(NAME, "cheating"), permanent_ban("Owner", "x")],
        )
        .await
        .unwrap();
    let rcon = f.rcon();
    rcon.refresh_rosters().await;
    let service = f.build_on(rcon, Arc::new(Protecting), Arc::default());

    assert_eq!(service.enforce_sweep().await, 1);

    let commands = f.commands();
    assert!(commands.contains(&format!("Kick {NAME}")), "{commands:?}");
    assert!(
        !commands
            .iter()
            .any(|c| c.ends_with("Owner") || c.ends_with("Innocent")),
        "{commands:?}"
    );
    assert!(ban_block(&f.save(NAME)).0);
    assert!(!ban_block(&f.save("Owner")).0);
    f.assert_no_rcon_ban();
}
