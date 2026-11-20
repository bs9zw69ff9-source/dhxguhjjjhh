//! Port of `EvasionResponderTests.cs`: what happens when a join matches a standing flag. The
//! RCON registry points at port 1, so nothing is enforced: the assertions are about the RECORD
//! and the decision.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use common::{FakeHttp, file_store};
use pavlov_config::{RconOptions, Secret};
use pavlov_core::data::SerializedStore;
use pavlov_core::evasion::{AccountRecord, FlagMatch, FlagVerdict, PendingFlag};
use pavlov_core::moderation::BanRecord;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::SystemClock;
use pavlov_host::feeds::FeedWebhooks;
use pavlov_host::logs::{FlaggedJoin, IpTrackingService, LogLine};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::evasion::EVASION_REASON;
use pavlov_host::moderation::{
    AuditLog, AutoBanOutcome, BanService, EvasionResponder, MasterNames, StoredMasterNames,
};
use pavlov_host::registry::RconRegistry;
use pavlov_storage::datasets::{AUTOBAN_EXEMPT, KNOWN_PLAYERS, TEMP_BANS};
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::ledger_store::LedgerFileStore;

struct Fixture {
    dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    tracking: Arc<IpTrackingService>,
    masters: Arc<StoredMasterNames>,
    responder: Arc<EvasionResponder>,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = file_store(dir.path());
        let metrics = Arc::new(MetricsRegistry::default());
        let mut options = RconOptions::new("server1", "127.0.0.1", 1, Secret::new("x"));
        options.max_attempts = 1;
        options.command_timeout = Duration::from_millis(200);
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
        let modsave = dir.path().join("ModSave");
        std::fs::create_dir_all(&modsave).unwrap();
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
        let feeds = Arc::new(FeedWebhooks::new(
            Arc::new(FakeHttp::canned(204, "")),
            Arc::clone(&metrics),
            clock.clone(),
        ));
        let responder = EvasionResponder::attach(
            Arc::clone(&tracking),
            bans,
            Arc::clone(&masters) as _,
            Arc::clone(&store),
            Arc::new(AuditLog::new(Arc::clone(&store), clock.clone())),
            feeds,
            metrics,
            clock,
            None,
        );
        Self {
            dir,
            store,
            tracking,
            masters,
            responder,
        }
    }

    fn bans(&self) -> Vec<BanRecord> {
        self.store.read(TEMP_BANS, Vec::new()).unwrap()
    }

    fn save_path(&self, player: &str) -> std::path::PathBuf {
        self.dir
            .path()
            .join("ModSave")
            .join(format!("{player}.json"))
    }

    /// The game made their save: they have joined before.
    fn joined(&self, player: &str) {
        std::fs::write(
            self.save_path(player),
            format!("{{\n\t\"name\": \"{player}\",\n\t\"money\": 0\n}}\n"),
        )
        .unwrap();
    }

    fn save_ban(&self, player: &str) -> serde_json::Value {
        let text = std::fs::read_to_string(self.save_path(player)).unwrap();
        serde_json::from_str::<serde_json::Value>(&text).unwrap()["ban"].clone()
    }

    async fn seed_bans(&self, bans: Vec<BanRecord>) {
        self.store.write(TEMP_BANS, &bans).await.unwrap();
    }

    async fn seed_accounts(&self, accounts: &[(&str, &[&str], &str)]) {
        let map: IgnoreCaseMap<AccountRecord> = accounts
            .iter()
            .map(|(id, ips, name)| {
                (
                    (*id).to_owned(),
                    AccountRecord {
                        confirmed_ips: ips.iter().map(|s| (*s).to_owned()).collect(),
                        names: vec![(*name).to_owned()],
                        ..AccountRecord::empty(*id)
                    },
                )
            })
            .collect();
        self.store.write(KNOWN_PLAYERS, &map).await.unwrap();
    }
}

fn join(name: &str, account: &str) -> FlaggedJoin {
    FlaggedJoin {
        account_id: account.into(),
        name: Some(name.into()),
        ip: Some("203.0.113.9".into()),
        verdict: FlagVerdict {
            matched: FlagMatch::Ip,
            detail: Some("blacklisted ip 203.0.113.9".into()),
            manual: false,
        },
        at: Utc::now(),
    }
}

fn evader() -> FlaggedJoin {
    join("Evader", "76561198000000001")
}

fn temp_ban(at: TimeDelta, expires: TimeDelta) -> BanRecord {
    BanRecord {
        unique_id: Some("76561198000000001".into()),
        reason: Some("Griefing".into()),
        moderator: Some("SomeMod".into()),
        at: Some(Utc::now() + at),
        expires: Some(Utc::now() + expires),
        permanent: false,
        duration_label: Some("2d".into()),
        ..BanRecord::for_player("Evader")
    }
}

#[tokio::test]
async fn a_flagged_join_is_banned_and_recorded() {
    let f = Fixture::new();
    assert_eq!(f.responder.respond(&evader()).await, AutoBanOutcome::Banned);

    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert_eq!(bans[0].player_id, "Evader");
    assert!(bans[0].permanent);
    assert_eq!(bans[0].moderator.as_deref(), Some("auto"));
    assert_eq!(bans[0].reason.as_deref(), Some(EVASION_REASON));
}

#[tokio::test]
async fn an_evader_with_a_save_gets_ban_evasion_written_permanently() {
    // Owner, 2026-11-20: an account tied by EOS id or address to a banned one is written as
    // "Ban Evasion", with the date blank for permanent.
    let f = Fixture::new();
    f.joined("Evader");

    f.responder.respond(&evader()).await;

    assert_eq!(
        f.save_ban("Evader"),
        serde_json::json!({"banned": true, "reason": "Ban Evasion", "unban_date": ""})
    );
}

#[tokio::test]
async fn an_evader_with_no_save_is_recorded_and_written_once_the_save_exists() {
    let f = Fixture::new();
    f.responder.respond(&evader()).await;
    assert!(
        !f.save_path("Evader").exists(),
        "the bot never creates a save"
    );
    assert_eq!(f.bans()[0].reason.as_deref(), Some(EVASION_REASON));

    f.joined("Evader"); // they came back and the game made the file
    let bans = BanService::new(
        Arc::new(RconRegistry::with_system_clock(
            &[],
            Duration::from_secs(60),
            Arc::new(MetricsRegistry::default()),
        )),
        Arc::clone(&f.store),
        Arc::clone(&f.masters) as _,
        Arc::new(SystemClock),
    )
    .with_saves(Arc::new(LedgerFileStore::new(
        Some(f.dir.path().join("ModSave")),
        Arc::new(GameFileGuard::none()),
    )));
    bans.enforce_sweep().await;

    assert_eq!(f.save_ban("Evader")["reason"], "Ban Evasion");
    assert_eq!(f.save_ban("Evader")["unban_date"], "");
}

#[tokio::test]
async fn a_master_account_is_never_auto_banned() {
    let f = Fixture::new();
    assert_eq!(
        f.responder.respond(&join("OwnerAlt", "1")).await,
        AutoBanOutcome::Master
    );
    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn master_protection_is_case_insensitive() {
    let f = Fixture::new();
    assert_eq!(
        f.responder.respond(&join("owneralt", "1")).await,
        AutoBanOutcome::Master
    );
    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn somebody_who_just_served_a_ban_is_not_rebanned() {
    let f = Fixture::new();
    f.masters.exempt("Served", None).await;
    assert_eq!(
        f.responder.respond(&join("Served", "1")).await,
        AutoBanOutcome::Exempt
    );
    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn an_active_temp_ban_is_not_silently_promoted_to_permanent() {
    let f = Fixture::new();
    f.seed_bans(vec![BanRecord {
        permanent: false,
        expires: Some(Utc::now() + TimeDelta::hours(24)),
        moderator: Some("Dana".into()),
        reason: Some("Cheating".into()),
        at: Some(Utc::now()),
        ..BanRecord::for_player("Evader")
    }])
    .await;

    assert_eq!(
        f.responder.respond(&evader()).await,
        AutoBanOutcome::EnforcedExisting
    );

    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert!(!bans[0].permanent);
    assert_eq!(bans[0].moderator.as_deref(), Some("Dana"));
    assert_eq!(bans[0].reason.as_deref(), Some("Cheating"));
}

#[tokio::test]
async fn a_join_with_no_known_name_is_not_recorded_as_a_ban() {
    let f = Fixture::new();
    let nameless = FlaggedJoin {
        name: None,
        ..join("x", "76561198000000009")
    };
    assert_eq!(f.responder.respond(&nameless).await, AutoBanOutcome::NoName);
    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn an_auto_ban_carries_no_staff_tier() {
    let f = Fixture::new();
    f.responder.respond(&evader()).await;
    assert_eq!(f.bans()[0].tier, None);
}

#[tokio::test]
async fn the_new_account_is_flagged_so_the_next_alt_is_caught() {
    let f = Fixture::new();
    f.seed_accounts(&[("76561198000000042", &["203.0.113.9"], "Evader")])
        .await;
    f.responder
        .respond(&join("Evader", "76561198000000042"))
        .await;
    assert!(f.tracking.load_flags().ids.contains("76561198000000042"));
}

#[tokio::test]
async fn a_never_ban_player_is_not_banned_and_no_record_is_written() {
    let f = Fixture::new();
    f.masters.protect("Evader").await.unwrap();
    assert_eq!(
        f.responder.respond(&evader()).await,
        AutoBanOutcome::Protected
    );
    assert!(f.bans().is_empty());
    assert!(f.tracking.load_flags().ids.is_empty());
}

#[tokio::test]
async fn an_account_with_no_confirmed_address_is_still_flagged_immediately() {
    let f = Fixture::new();
    f.responder
        .respond(&join("Evader", "76561198000000099"))
        .await;

    assert!(f.tracking.load_flags().ids.contains("76561198000000099"));
    let pending: IgnoreCaseMap<PendingFlag> = f
        .store
        .read(
            &format!("{AUTOBAN_EXEMPT}_pending"),
            IgnoreCaseMap::default(),
        )
        .unwrap();
    assert!(pending.is_empty());
}

#[tokio::test]
async fn an_auto_ban_flags_no_addresses() {
    // THE FEEDBACK LOOP: flagging every confirmed address wrote bans nobody had judged.
    let f = Fixture::new();
    f.seed_accounts(&[(
        "76561198000000042",
        &["203.0.113.9", "198.51.100.7"],
        "Evader",
    )])
    .await;
    f.responder
        .respond(&join("Evader", "76561198000000042"))
        .await;

    let flags = f.tracking.load_flags();
    assert!(flags.ips.is_empty());
    assert!(flags.manual_ips.is_empty());
}

#[tokio::test]
async fn an_auto_ban_does_not_flag_accounts_sharing_the_address() {
    let f = Fixture::new();
    f.seed_accounts(&[
        ("76561198000000042", &["203.0.113.9"], "Evader"),
        ("76561198000000077", &["203.0.113.9"], "Housemate"),
    ])
    .await;
    f.responder
        .respond(&join("Evader", "76561198000000042"))
        .await;
    assert!(!f.tracking.load_flags().ids.contains("76561198000000077"));
}

#[tokio::test]
async fn a_served_ban_is_lifted_rather_than_escalated_to_permanent() {
    let f = Fixture::new();
    f.seed_bans(vec![temp_ban(TimeDelta::days(-3), TimeDelta::minutes(-5))])
        .await;

    assert_eq!(f.responder.respond(&evader()).await, AutoBanOutcome::Served);
    assert!(f.bans().is_empty());
}

#[tokio::test]
async fn an_unexpired_temp_ban_is_enforced_without_becoming_permanent() {
    let f = Fixture::new();
    f.seed_bans(vec![temp_ban(TimeDelta::hours(-1), TimeDelta::days(1))])
        .await;

    assert_eq!(
        f.responder.respond(&evader()).await,
        AutoBanOutcome::EnforcedExisting
    );
    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert!(!bans[0].permanent);
    assert_eq!(bans[0].moderator.as_deref(), Some("SomeMod"));
}

#[tokio::test]
async fn a_served_ban_caught_by_an_owner_set_name_flag_is_still_banned() {
    // A username flag is typed by an owner: no expiry to have served.
    let f = Fixture::new();
    f.seed_bans(vec![temp_ban(TimeDelta::days(-3), TimeDelta::minutes(-5))])
        .await;
    let by_name = FlaggedJoin {
        verdict: FlagVerdict {
            matched: FlagMatch::Name,
            detail: Some("blacklisted username Evader".into()),
            manual: true,
        },
        ..evader()
    };

    assert_eq!(f.responder.respond(&by_name).await, AutoBanOutcome::Banned);
    let bans = f.bans();
    assert_eq!(bans.len(), 1);
    assert!(bans[0].permanent);
}

#[tokio::test]
async fn constructing_the_responder_subscribes_to_flagged() {
    // The defect itself: the tracker raised Flagged and nothing listened. Proven end to end.
    let f = Fixture::new();
    assert_eq!(f.tracking.flagged.len(), 1);

    f.tracking.flag_address_manually("203.0.113.5").await;
    f.tracking
        .ingest(&LogLine::new(
            "server1.log",
            "[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Evader userId: EOS:0002abc",
        ))
        .await;
    f.tracking
        .ingest(&LogLine::new(
            "server1.log",
            "[2026.07.15-12.30.00:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777",
        ))
        .await;

    assert_eq!(
        f.bans().len(),
        1,
        "the manual block reached the responder and banned them"
    );
}
