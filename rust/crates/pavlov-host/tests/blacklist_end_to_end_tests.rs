//! Port of `BlacklistEndToEndTests.cs`: an owner's blacklist entry, a join from the game log,
//! and the ban written into the player's save plus the kick that reaches the wire. No RCON
//! `Ban` is sent (owner, 2026-11-19: bans live in the save).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use common::{FakeHttp, file_store};
use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::BanRecord;
use pavlov_core::time::SystemClock;
use pavlov_host::feeds::FeedWebhooks;
use pavlov_host::logs::{IpTrackingService, LogLine};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::owner_actions::{OwnerActionParts, OwnerActions};
use pavlov_host::moderation::{AuditLog, BanService, EvasionResponder, StoredMasterNames};
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;
use pavlov_storage::datasets::TEMP_BANS;
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::ledger_store::LedgerFileStore;

const LOG: &str = "server1.log";
const ADDRESS: &str = "203.0.113.5";
const ACCEPT: &str =
    "[2026.09.29-12.00.00:000]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777";
const OTHER_ACCEPT: &str =
    "[2026.09.29-12.00.00:200]LogNet: NotifyAcceptingConnection accepted from: 198.51.100.8:7777";
const LOGIN: &str =
    "[2026.09.29-12.00.01:000]LogNet: Login request: ?Name=Evader userId: EOS:0002abc";
const CLOSE: &str = "[2026.09.29-12.40.00:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777";

struct Rig {
    dir: tempfile::TempDir,
    server: FakeRconServer,
    store: Arc<SerializedStore>,
    tracking: Arc<IpTrackingService>,
    actions: OwnerActions,
    _responder: Arc<EvasionResponder>,
}

impl Rig {
    async fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = file_store(dir.path());
        let metrics = Arc::new(MetricsRegistry::default());
        let server = FakeRconServer::start().await.unwrap();
        let mut options = server.options();
        options.name = "server1".into();
        options.command_spacing = Duration::ZERO;
        options.read_cache_duration = Duration::ZERO;
        options.max_attempts = 1;
        let rcon = Arc::new(RconRegistry::with_system_clock(
            &[options],
            Duration::from_secs(60),
            Arc::clone(&metrics),
        ));
        let clock = Arc::new(SystemClock);
        let masters = Arc::new(
            StoredMasterNames::new(["OwnerAlt"], Arc::clone(&store), clock.clone()).unwrap(),
        );
        let tracking = Arc::new(IpTrackingService::new(
            Arc::clone(&store),
            Arc::clone(&metrics),
            clock.clone(),
        ));
        // Evader has played before, so the game has made their save.
        let modsave = dir.path().join("ModSave");
        std::fs::create_dir_all(&modsave).unwrap();
        std::fs::write(
            modsave.join("Evader.json"),
            "{\n\t\"name\": \"Evader\",\n\t\"money\": 0\n}\n",
        )
        .unwrap();
        let bans = Arc::new(
            BanService::new(
                rcon,
                Arc::clone(&store),
                Arc::clone(&masters) as _,
                clock.clone(),
            )
            .with_saves(Arc::new(LedgerFileStore::new(
                Some(modsave),
                Arc::new(GameFileGuard::none()),
            ))),
        );
        let responder = EvasionResponder::attach(
            Arc::clone(&tracking),
            bans,
            masters,
            Arc::clone(&store),
            Arc::new(AuditLog::new(Arc::clone(&store), clock.clone())),
            Arc::new(FeedWebhooks::new(
                Arc::new(FakeHttp::canned(204, "")),
                Arc::clone(&metrics),
                clock.clone(),
            )),
            metrics,
            clock,
            None,
        );
        let actions = OwnerActions::new(
            Arc::clone(&store),
            Arc::clone(&tracking),
            OwnerActionParts::default(),
        );
        Self {
            dir,
            server,
            store,
            tracking,
            actions,
            _responder: responder,
        }
    }

    async fn feed(&self, lines: &[&str]) {
        for line in lines {
            self.tracking
                .ingest(&LogLine {
                    file: LOG.into(),
                    text: (*line).to_owned(),
                })
                .await;
        }
        // The responder runs off the ingest path; give it a moment.
        tokio::time::sleep(Duration::from_millis(300)).await;
    }

    fn bans(&self) -> Vec<BanRecord> {
        self.store.read(TEMP_BANS, Vec::new()).unwrap()
    }

    fn the_ban(&self) -> BanRecord {
        let bans = self.bans();
        assert_eq!(bans.len(), 1, "{bans:?}");
        bans.into_iter().next().unwrap()
    }

    fn banned_in_save(&self) -> bool {
        let text = std::fs::read_to_string(self.dir.path().join("ModSave/Evader.json")).unwrap();
        let save: serde_json::Value = serde_json::from_str(&text).unwrap();
        save["ban"]["banned"].as_bool() == Some(true)
    }

    /// Banned in their save and kicked, with no RCON ban.
    fn assert_removed(&self) {
        let commands = self.server.commands();
        assert!(self.banned_in_save(), "the ban is not in their save");
        assert!(commands.contains(&"Kick Evader".to_owned()), "{commands:?}");
        assert!(
            !commands.iter().any(|c| c.starts_with("Ban ")),
            "{commands:?}"
        );
    }
}

#[tokio::test]
async fn a_blacklisted_address_is_banned_when_it_joins() {
    let r = Rig::new().await;
    assert!(r.actions.blacklist(ADDRESS).await.ok);

    r.feed(&[ACCEPT, LOGIN]).await;

    r.assert_removed();
    let ban = r.the_ban();
    assert_eq!(ban.player_id, "Evader");
    assert!(ban.permanent);
    assert!(
        ban.reason
            .unwrap()
            .contains(&format!("blacklisted ip {ADDRESS}"))
    );
}

#[tokio::test]
async fn on_a_busy_server_a_blacklisted_address_is_banned_on_the_line_that_confirms_it() {
    // Two accepts before the login: the address is not certain at login, so nothing is done
    // until the close line names the account and the address together.
    let r = Rig::new().await;
    r.actions.blacklist(ADDRESS).await;

    r.feed(&[ACCEPT, OTHER_ACCEPT, LOGIN]).await;
    assert!(!r.banned_in_save());
    assert!(!r.server.commands().contains(&"Kick Evader".to_owned()));

    r.feed(&[CLOSE]).await;

    r.assert_removed();
    assert_eq!(r.the_ban().player_id, "Evader");
}

#[tokio::test]
async fn a_blacklisted_username_is_banned_when_it_joins() {
    let r = Rig::new().await;
    assert!(r.actions.blacklist("Evader").await.ok);

    r.feed(&[LOGIN]).await;

    r.assert_removed();
    assert!(
        r.the_ban()
            .reason
            .unwrap()
            .contains("blacklisted username Evader")
    );
}

#[tokio::test]
async fn somebody_not_on_the_blacklist_is_left_alone() {
    let r = Rig::new().await;
    r.actions.blacklist("198.51.100.99").await;
    r.actions.blacklist("SomebodyElse").await;

    r.feed(&[ACCEPT, LOGIN, CLOSE]).await;

    assert!(!r.server.commands().iter().any(|c| c.starts_with("Kick ")));
    assert!(!r.banned_in_save());
    assert!(r.bans().is_empty());
}
