//! Port of `OwnerActionsTests.cs`: what `/configure`'s owner panel does, and that each action
//! says what happened instead of pretending.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use chrono::{TimeDelta, TimeZone, Utc};
use common::file_store;
use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::evasion::AccountRecord;
use pavlov_core::moderation::{BanRecord, StaffTier};
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::SystemClock;
use pavlov_host::factions::RosterService;
use pavlov_host::factions::whitelist_backup::{WhitelistBackup, is_roster_file_name};
use pavlov_host::firewall::{Firewall, FirewallResult};
use pavlov_host::logs::IpTrackingService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::owner_actions::{LiftBan, OwnerActionParts, OwnerActions};
use pavlov_host::moderation::{CommandBlacklist, MasterNames, StoredMasterNames};
use pavlov_storage::datasets::{IP_FLAGS, KNOWN_PLAYERS, TEMP_BANS, USER_BLACKLIST, USER_UNBARRED};

const OWNER_ID: u64 = 444_000_000_000_000_001;
const USER: &str = "1014251293159731310";

struct Rig {
    dir: tempfile::TempDir,
    ledgers: PathBuf,
    store: Arc<SerializedStore>,
    tracking: Arc<IpTrackingService>,
    actions: OwnerActions,
}

fn tracking(store: &Arc<SerializedStore>) -> Arc<IpTrackingService> {
    Arc::new(IpTrackingService::new(
        Arc::clone(store),
        Arc::new(MetricsRegistry::default()),
        Arc::new(SystemClock),
    ))
}

impl Rig {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let ledgers = dir.path().join("modsave");
        std::fs::create_dir_all(&ledgers).unwrap();
        let store = file_store(&dir.path().join("data"));
        let tracking = tracking(&store);
        let blacklist = Arc::new(CommandBlacklist::new(
            Arc::clone(&store),
            Arc::new(|id| id == OWNER_ID),
            &[],
        ));
        let actions = OwnerActions::new(
            Arc::clone(&store),
            Arc::clone(&tracking),
            OwnerActionParts {
                ledger_directory: Some(ledgers.clone()),
                blacklist: Some(blacklist),
                ..OwnerActionParts::default()
            },
        );
        Self {
            dir,
            ledgers,
            store,
            tracking,
            actions,
        }
    }

    fn with(&self, parts: OwnerActionParts) -> OwnerActions {
        OwnerActions::new(Arc::clone(&self.store), Arc::clone(&self.tracking), parts)
    }

    fn ledgers_only(&self) -> OwnerActionParts {
        OwnerActionParts {
            ledger_directory: Some(self.ledgers.clone()),
            ..OwnerActionParts::default()
        }
    }

    async fn seed_accounts(&self, accounts: Vec<AccountRecord>) {
        let map: IgnoreCaseMap<AccountRecord> =
            accounts.into_iter().map(|a| (a.id.clone(), a)).collect();
        self.store.write(KNOWN_PLAYERS, &map).await.unwrap();
    }

    fn ids(&self, key: &str) -> Vec<String> {
        self.store.read(key, Vec::new()).unwrap()
    }

    fn stored_bans(&self) -> Vec<BanRecord> {
        self.store.read(TEMP_BANS, Vec::new()).unwrap()
    }
}

fn account(id: &str, confirmed: &[&str], guessed: &[&str], name: &str) -> AccountRecord {
    AccountRecord {
        confirmed_ips: confirmed.iter().map(|s| (*s).to_owned()).collect(),
        guessed_ips: guessed.iter().map(|s| (*s).to_owned()).collect(),
        names: vec![name.to_owned()],
        ..AccountRecord::empty(id)
    }
}

// ---- blacklisting ----

#[tokio::test]
async fn an_address_and_a_name_are_told_apart_without_being_asked() {
    let r = Rig::new();
    r.actions.blacklist("203.0.113.9").await;
    r.actions.blacklist("CheaterMcGee").await;

    let flags = r.tracking.load_flags();
    assert!(flags.manual_ips.contains("203.0.113.9"));
    assert!(flags.names.contains("CheaterMcGee"));
    assert!(!flags.manual_ips.contains("CheaterMcGee"));
}

#[tokio::test]
async fn blacklisting_an_address_names_the_accounts_it_already_matches() {
    let r = Rig::new();
    r.seed_accounts(vec![
        account("76561198000000001", &["203.0.113.9"], &[], "Alice"),
        account("76561198000000002", &["203.0.113.9"], &[], "Bob"),
        account("76561198000000003", &["198.51.100.1"], &[], "Carol"),
    ])
    .await;

    let result = r.actions.blacklist("203.0.113.9").await;

    assert!(result.ok);
    assert_eq!(result.lines.len(), 2);
    assert!(result.lines.iter().any(|l| l.contains("Alice")));
    assert!(!result.lines.iter().any(|l| l.contains("Carol")));
}

#[tokio::test]
async fn blacklisting_is_idempotent() {
    let r = Rig::new();
    r.actions.blacklist("203.0.113.9").await;
    r.actions.blacklist("203.0.113.9").await;
    assert_eq!(r.tracking.load_flags().manual_ips.len(), 1);
}

#[tokio::test]
async fn an_empty_target_is_refused_rather_than_flagging_nothing() {
    let r = Rig::new();
    assert!(!r.actions.blacklist("   ").await.ok);
    assert!(r.tracking.load_flags().manual_ips.is_empty());
}

// ---- the firewall ----

#[derive(Default)]
struct FakeFirewall {
    denied: Mutex<Vec<String>>,
    undenied: Mutex<Vec<String>>,
    fail: bool,
}

impl FakeFirewall {
    fn answer(&self) -> FirewallResult {
        FirewallResult {
            ok: !self.fail,
            detail: if self.fail { "ufw not found" } else { "ok" }.into(),
        }
    }
}

impl Firewall for FakeFirewall {
    fn deny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult> {
        self.denied.lock().unwrap().push(ip.to_owned());
        let answer = self.answer();
        async move { answer }.boxed()
    }
    fn undeny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult> {
        self.undenied.lock().unwrap().push(ip.to_owned());
        let answer = self.answer();
        async move { answer }.boxed()
    }
    fn status(&self) -> BoxFuture<'_, FirewallResult> {
        let answer = self.answer();
        async move { answer }.boxed()
    }
}

fn with_firewall(r: &Rig, firewall: &Arc<FakeFirewall>) -> OwnerActions {
    r.with(OwnerActionParts {
        firewall: Some(Arc::clone(firewall) as _),
        ..r.ledgers_only()
    })
}

#[tokio::test]
async fn blacklisting_an_address_also_denies_it_at_the_firewall() {
    let r = Rig::new();
    let firewall = Arc::new(FakeFirewall::default());
    let result = with_firewall(&r, &firewall).blacklist("203.0.113.9").await;

    assert_eq!(*firewall.denied.lock().unwrap(), ["203.0.113.9"]);
    assert!(r.tracking.load_flags().manual_ips.contains("203.0.113.9"));
    assert!(result.detail.to_lowercase().contains("firewall"));
}

#[tokio::test]
async fn blacklisting_a_username_never_touches_the_firewall() {
    let r = Rig::new();
    let firewall = Arc::new(FakeFirewall::default());
    with_firewall(&r, &firewall).blacklist("CheaterMcGee").await;
    assert!(firewall.denied.lock().unwrap().is_empty());
}

#[tokio::test]
async fn clearing_a_manual_address_removes_the_firewall_rule() {
    let r = Rig::new();
    let firewall = Arc::new(FakeFirewall::default());
    let actions = with_firewall(&r, &firewall);
    actions.blacklist("203.0.113.9").await;
    actions.clear_address("203.0.113.9").await;

    assert_eq!(*firewall.undenied.lock().unwrap(), ["203.0.113.9"]);
    assert!(!r.tracking.load_flags().manual_ips.contains("203.0.113.9"));
}

#[tokio::test]
async fn a_firewall_failure_does_not_fail_the_blacklist() {
    let r = Rig::new();
    let firewall = Arc::new(FakeFirewall {
        fail: true,
        ..FakeFirewall::default()
    });
    let result = with_firewall(&r, &firewall).blacklist("203.0.113.9").await;

    assert!(result.ok);
    assert!(r.tracking.load_flags().manual_ips.contains("203.0.113.9"));
    assert!(result.detail.to_lowercase().contains("did not apply"));
}

// ---- alts ----

#[tokio::test]
async fn alts_come_from_confirmed_addresses_only() {
    let r = Rig::new();
    r.seed_accounts(vec![
        account("1", &["203.0.113.9"], &["198.51.100.1"], "Alice"),
        account("2", &["203.0.113.9"], &[], "RealAlt"),
        account("3", &[], &["198.51.100.1"], "Innocent"),
    ])
    .await;

    let result = r.actions.alts("Alice");
    assert_eq!(result.lines.len(), 1);
    assert!(result.lines[0].contains("RealAlt"));
}

#[tokio::test]
async fn an_account_with_no_confirmed_address_has_no_alts() {
    let r = Rig::new();
    r.seed_accounts(vec![
        account("1", &[], &[], "Alice"),
        account("2", &[], &[], "Bob"),
    ])
    .await;
    assert!(r.actions.alts("Alice").lines.is_empty());
}

// ---- the command bar list ----

#[tokio::test]
async fn barring_writes_the_node_array_format() {
    let r = Rig::new();
    r.actions.bar_user(USER).await;
    assert_eq!(r.ids(USER_BLACKLIST), [USER]);
}

#[tokio::test]
async fn a_mention_is_accepted_as_well_as_a_bare_id() {
    let r = Rig::new();
    r.actions.bar_user(&format!("<@{USER}>")).await;
    assert!(r.ids(USER_BLACKLIST).contains(&USER.to_owned()));
}

#[tokio::test]
async fn something_that_is_not_an_id_is_refused() {
    let r = Rig::new();
    assert!(!r.actions.bar_user("CheaterMcGee").await.ok);
    assert!(r.ids(USER_BLACKLIST).is_empty());
}

#[tokio::test]
async fn unbarring_lifts_the_bar() {
    let r = Rig::new();
    r.actions.bar_user(USER).await;
    let result = r.actions.unbar_user(USER).await;

    assert!(result.ok);
    assert!(result.detail.contains("may use bot commands again"));
    assert!(r.ids(USER_BLACKLIST).is_empty());
}

#[tokio::test]
async fn barring_clears_a_stale_unbar_override() {
    let r = Rig::new();
    r.store
        .write(USER_UNBARRED, &vec![USER.to_owned()])
        .await
        .unwrap();
    r.actions.bar_user(USER).await;

    assert!(r.ids(USER_UNBARRED).is_empty());
    assert!(r.ids(USER_BLACKLIST).contains(&USER.to_owned()));
}

#[tokio::test]
async fn barring_an_owner_is_refused_and_writes_nothing() {
    let r = Rig::new();
    let result = r.actions.bar_user("444000000000000001").await;

    assert!(!result.ok);
    assert!(result.detail.to_lowercase().contains("owner"));
    assert_eq!(r.store.read_raw(USER_BLACKLIST).unwrap(), None);
}

#[tokio::test]
async fn the_bar_list_shows_who_is_barred() {
    let r = Rig::new();
    r.actions.bar_user(USER).await;
    let result = r.actions.barred_users();
    assert_eq!(result.lines.len(), 1);
    assert!(result.lines[0].contains(USER));
}

#[tokio::test]
async fn without_the_list_the_actions_say_so_rather_than_pretend() {
    let r = Rig::new();
    let bare = r.with(r.ledgers_only());
    assert!(!bare.bar_user(USER).await.ok);
    assert_eq!(r.store.read_raw(USER_BLACKLIST).unwrap(), None);
}

// ---- the ignore list ----

#[tokio::test]
async fn ignoring_survives_a_restart() {
    let r = Rig::new();
    r.actions.ignore("OwnerAlt").await;

    let fresh = tracking(&r.store);
    assert!(!fresh.is_untracked("OwnerAlt"));
    OwnerActions::new(Arc::clone(&r.store), Arc::clone(&fresh), r.ledgers_only())
        .restore_ignore_list();
    assert!(fresh.is_untracked("OwnerAlt"));
}

#[tokio::test]
async fn unignoring_takes_effect_immediately() {
    let r = Rig::new();
    r.actions.ignore("OwnerAlt").await;
    r.actions.unignore("OwnerAlt").await;

    assert!(!r.tracking.is_untracked("OwnerAlt"));
    assert!(r.actions.ignored_names().lines.is_empty());
}

// ---- whitelist snapshots ----

fn with_rosters(r: &Rig) -> (OwnerActions, PathBuf) {
    let rosters = r.dir.path().join("FactionRoles");
    std::fs::create_dir_all(&rosters).unwrap();
    let service = Arc::new(RosterService::new(
        Some(rosters.clone()),
        r.dir.path().join("roster_bak"),
    ));
    let backup = Arc::new(WhitelistBackup::new(
        Arc::clone(&r.store),
        service,
        Arc::new(SystemClock),
    ));
    let actions = r.with(OwnerActionParts {
        whitelists: Some(backup),
        ..r.ledgers_only()
    });
    (actions, rosters)
}

fn lines(path: &std::path::Path) -> Vec<String> {
    std::fs::read_to_string(path)
        .unwrap()
        .lines()
        .map(str::to_owned)
        .collect()
}

#[tokio::test]
async fn a_snapshot_holds_the_roster_files_and_restores_them() {
    let r = Rig::new();
    let (actions, rosters) = with_rosters(&r);
    std::fs::write(rosters.join("ncr_trooper.txt"), "Alice\nBob\n").unwrap();
    std::fs::write(rosters.join("handmade.txt"), "Carol\n").unwrap();

    let saved = actions.save_whitelists().await;
    assert!(saved.ok, "{}", saved.detail);
    assert!(saved.detail.contains("**3** name(s)"), "{}", saved.detail);

    for entry in std::fs::read_dir(&rosters).unwrap() {
        std::fs::remove_file(entry.unwrap().path()).unwrap();
    }
    let loaded = actions.load_whitelists().await;

    assert!(loaded.ok, "{}", loaded.detail);
    assert_eq!(lines(&rosters.join("ncr_trooper.txt")), ["Alice", "Bob"]);
    assert_eq!(lines(&rosters.join("handmade.txt")), ["Carol"]);
}

#[tokio::test]
async fn empty_rosters_never_replace_a_populated_snapshot() {
    let r = Rig::new();
    let (actions, rosters) = with_rosters(&r);
    std::fs::write(rosters.join("ncr_trooper.txt"), "Alice\n").unwrap();
    assert!(actions.save_whitelists().await.ok);

    std::fs::write(rosters.join("ncr_trooper.txt"), "").unwrap();
    assert!(!actions.save_whitelists().await.ok);

    assert!(actions.load_whitelists().await.ok);
    assert_eq!(lines(&rosters.join("ncr_trooper.txt")), ["Alice"]);
}

#[tokio::test]
async fn restoring_with_no_snapshot_is_refused_and_touches_nothing() {
    let r = Rig::new();
    let (actions, rosters) = with_rosters(&r);
    std::fs::write(rosters.join("ncr_trooper.txt"), "Alice\n").unwrap();

    assert!(!actions.load_whitelists().await.ok);
    assert_eq!(lines(&rosters.join("ncr_trooper.txt")), ["Alice"]);
}

#[tokio::test]
async fn a_node_snapshot_on_disk_loads() {
    let r = Rig::new();
    let (actions, rosters) = with_rosters(&r);
    std::fs::write(
        r.dir.path().join("data").join("faction_backup.json"),
        r#"{"savedAt":1700000000000,"files":{"legion_recruit.txt":["Dave"]}}"#,
    )
    .unwrap();

    let result = actions.load_whitelists().await;
    assert!(result.ok, "{}", result.detail);
    assert_eq!(lines(&rosters.join("legion_recruit.txt")), ["Dave"]);
}

#[tokio::test]
async fn a_snapshot_entry_that_is_a_path_is_not_written() {
    let r = Rig::new();
    let (actions, _) = with_rosters(&r);
    std::fs::write(
        r.dir.path().join("data").join("faction_backup.json"),
        r#"{"savedAt":1700000000000,"files":{"../../escape.txt":["x"],"ok.txt":["y"]}}"#,
    )
    .unwrap();

    let result = actions.load_whitelists().await;
    assert!(
        result.detail.contains("not a roster file name"),
        "{}",
        result.detail
    );
    assert!(!r.dir.path().join("..").join("escape.txt").exists());
}

#[test]
fn only_plain_txt_names_are_roster_files() {
    for (file, ok) in [
        ("ncr_trooper.txt", true),
        ("../x.txt", false),
        ("/etc/x.txt", false),
        ("sub/x.txt", false),
        ("x.json", false),
        ("", false),
    ] {
        assert_eq!(is_roster_file_name(file), ok, "{file:?}");
    }
}

// ---- destructive wipes ----

#[test]
fn wiping_money_deletes_ledgers_and_says_how_many() {
    let r = Rig::new();
    std::fs::write(r.ledgers.join("player1.txt"), "500").unwrap();
    std::fs::write(r.ledgers.join("player2.txt"), "900").unwrap();

    let result = r.actions.wipe_money();

    assert!(result.ok);
    assert!(result.detail.contains("**2**"));
    assert_eq!(std::fs::read_dir(&r.ledgers).unwrap().count(), 0);
}

#[test]
fn an_unconfigured_ledger_path_is_refused_not_reported_as_a_successful_wipe() {
    let r = Rig::new();
    let result = r.with(OwnerActionParts::default()).wipe_money();
    assert!(!result.ok);
    assert!(result.detail.contains("MODSAVE_PATH"));
}

#[tokio::test]
async fn wiping_player_data_leaves_bans_alone() {
    let r = Rig::new();
    r.seed_accounts(vec![account("1", &["203.0.113.9"], &[], "Alice")])
        .await;
    r.actions.blacklist("CheaterMcGee").await;

    r.actions.wipe_player_data().await;

    let accounts: IgnoreCaseMap<AccountRecord> =
        r.store.read(KNOWN_PLAYERS, IgnoreCaseMap::new()).unwrap();
    assert!(accounts.is_empty());
    assert!(r.tracking.load_flags().names.contains("CheaterMcGee"));
}

fn ban(player: &str, permanent: bool, tier: Option<StaffTier>) -> BanRecord {
    BanRecord {
        permanent,
        tier,
        expires: (!permanent).then(|| Utc::now() + TimeDelta::days(1)),
        ..BanRecord::for_player(player)
    }
}

#[tokio::test]
async fn clearing_temporary_bans_keeps_permanent_ones() {
    let r = Rig::new();
    r.store
        .write(
            TEMP_BANS,
            &vec![ban("temp", false, None), ban("perm", true, None)],
        )
        .await
        .unwrap();

    let result = r.actions.clear_temp_bans().await;

    let remaining = r.stored_bans();
    assert_eq!(remaining.len(), 1);
    assert_eq!(remaining[0].player_id, "perm");
    assert!(result.detail.contains("**1**"));
}

#[tokio::test]
async fn bulk_clears_keep_master_owner_bans() {
    let r = Rig::new();
    r.store
        .write(
            TEMP_BANS,
            &vec![
                ban("masterTemp", false, Some(StaffTier::MasterOwner)),
                ban("masterPerm", true, Some(StaffTier::MasterOwner)),
                ban("ownerPerm", true, Some(StaffTier::SuperOwner)),
                ban("modTemp", false, Some(StaffTier::Mod)),
            ],
        )
        .await
        .unwrap();

    let temp = r.actions.clear_temp_bans().await;
    let ids = |r: &Rig| {
        r.stored_bans()
            .into_iter()
            .map(|b| b.player_id)
            .collect::<Vec<_>>()
    };
    assert_eq!(ids(&r), ["masterTemp", "masterPerm", "ownerPerm"]);
    assert!(temp.detail.contains("Master Owner"));

    r.actions.clear_all_bans().await;
    assert_eq!(ids(&r), ["masterTemp", "masterPerm"]);
}

#[tokio::test]
async fn clearing_temporary_bans_keeps_every_field_of_the_ones_it_keeps() {
    let r = Rig::new();
    let at = Utc.with_ymd_and_hms(2026, 9, 1, 12, 0, 0).unwrap();
    let kept = BanRecord {
        unique_id: Some("acct1".into()),
        at: Some(at),
        ..ban("perm", true, Some(StaffTier::Admin))
    };
    r.store
        .write(TEMP_BANS, &vec![ban("temp", false, None), kept])
        .await
        .unwrap();

    r.actions.clear_temp_bans().await;

    let stored = r.stored_bans();
    assert_eq!(stored.len(), 1);
    assert_eq!(stored[0].unique_id.as_deref(), Some("acct1"));
    assert_eq!(stored[0].at, Some(at));
    assert_eq!(stored[0].tier, Some(StaffTier::Admin));
}

// ---- the never-ban list ----

fn with_protection(r: &Rig, lift: LiftBan) -> (OwnerActions, Arc<StoredMasterNames>) {
    let masters =
        Arc::new(StoredMasterNames::new([], Arc::clone(&r.store), Arc::new(SystemClock)).unwrap());
    let actions = r.with(OwnerActionParts {
        masters: Some(Arc::clone(&masters)),
        lift_ban: Some(lift),
        ..r.ledgers_only()
    });
    (actions, masters)
}

fn recording_lift() -> (LiftBan, Arc<Mutex<Vec<String>>>) {
    let lifted = Arc::new(Mutex::new(Vec::new()));
    let seen = Arc::clone(&lifted);
    let lift: LiftBan = Arc::new(move |player| {
        seen.lock().unwrap().push(player);
        async { Ok(()) }.boxed()
    });
    (lift, lifted)
}

#[tokio::test]
async fn protecting_a_player_also_lifts_what_is_already_on_them() {
    let r = Rig::new();
    let (lift, lifted) = recording_lift();
    let (actions, masters) = with_protection(&r, lift);

    let result = actions.protect_player("Holosight1").await;

    assert!(result.ok);
    assert!(masters.is_protected("Holosight1"));
    assert_eq!(*lifted.lock().unwrap(), ["Holosight1"]);
}

#[tokio::test]
async fn protection_survives_a_failed_lift_and_says_so() {
    let r = Rig::new();
    let failing: LiftBan = Arc::new(|_| async { Err("rcon down".to_owned()) }.boxed());
    let (actions, masters) = with_protection(&r, failing);

    let result = actions.protect_player("Holosight1").await;

    assert!(masters.is_protected("Holosight1"));
    assert!(result.detail.contains("could NOT be lifted"));
    assert!(result.detail.contains("/unban"));
}

#[tokio::test]
async fn protection_is_case_insensitive_and_reversible() {
    let r = Rig::new();
    let (lift, _) = recording_lift();
    let (actions, masters) = with_protection(&r, lift);

    actions.protect_player("  Holosight1  ").await;
    assert!(masters.is_protected("holosight1"));
    assert!(actions.protected_players().detail.contains("**1**"));

    actions.unprotect_player("HOLOSIGHT1").await;
    assert!(!masters.is_protected("Holosight1"));
    assert!(actions.protected_players().detail.contains("Nobody"));
}

// ---- an unreadable flag row ----

#[tokio::test]
async fn an_unreadable_flag_row_is_reported_as_unreadable_not_as_empty() {
    let r = Rig::new();
    r.store
        .write(IP_FLAGS, &vec!["76561198000000042"])
        .await
        .unwrap();

    let result = r.actions.view_blacklist();
    assert!(
        result.detail.to_lowercase().contains("could not be read"),
        "{}",
        result.detail
    );
    assert!(!result.detail.contains("Nothing is blacklisted"));
}

#[test]
fn a_genuinely_empty_flag_row_still_reads_as_empty() {
    let r = Rig::new();
    let result = r.actions.view_blacklist();
    assert!(result.detail.contains("Nothing is blacklisted"));
    assert!(!result.detail.to_lowercase().contains("could not be read"));
}
