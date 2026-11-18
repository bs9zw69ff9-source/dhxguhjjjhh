//! Port of `BanFirewallTests.cs`: banned players' addresses denied at the firewall and lifted
//! when the ban ends, and the addresses that must never be denied are not.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use common::file_store;
use futures_util::future::BoxFuture;
use pavlov_config::{RconOptions, Secret};
use pavlov_core::data::SerializedStore;
use pavlov_core::evasion::AccountRecord;
use pavlov_core::moderation::BanRecord;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::SystemClock;
use pavlov_host::firewall::{Firewall, FirewallResult};
use pavlov_host::logs::IpTrackingService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::ban_firewall::MAX_CHANGES_PER_PASS;
use pavlov_host::moderation::{BanFirewall, BanFirewallPass, BanService, StoredMasterNames};
use pavlov_host::registry::RconRegistry;
use pavlov_storage::datasets::{KNOWN_PLAYERS, TEMP_BANS};

const BANNED_IP: &str = "203.0.113.9";
const OTHER_IP: &str = "198.51.100.7";

#[derive(Default)]
struct RecordingFirewall {
    denied: Mutex<Vec<String>>,
    undenied: Mutex<Vec<String>>,
    fail: AtomicBool,
}

impl RecordingFirewall {
    fn result(&self, ok_text: &str) -> FirewallResult {
        let fail = self.fail.load(Ordering::SeqCst);
        FirewallResult {
            ok: !fail,
            detail: if fail {
                "ufw is not installed".into()
            } else {
                ok_text.into()
            },
        }
    }

    fn denied(&self) -> Vec<String> {
        self.denied.lock().unwrap().clone()
    }

    fn undenied(&self) -> Vec<String> {
        self.undenied.lock().unwrap().clone()
    }
}

impl Firewall for RecordingFirewall {
    fn deny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult> {
        self.denied.lock().unwrap().push(ip.to_owned());
        let result = self.result("Rule inserted");
        Box::pin(async move { result })
    }
    fn undeny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult> {
        self.undenied.lock().unwrap().push(ip.to_owned());
        let result = self.result("Rule deleted");
        Box::pin(async move { result })
    }
    fn status(&self) -> BoxFuture<'_, FirewallResult> {
        Box::pin(async {
            FirewallResult {
                ok: true,
                detail: String::new(),
            }
        })
    }
}

struct Fixture {
    _dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    tracking: Arc<IpTrackingService>,
    masters: Arc<StoredMasterNames>,
    bans: Arc<BanService>,
    firewall: Arc<RecordingFirewall>,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = file_store(dir.path());
        let metrics = Arc::new(MetricsRegistry::default());
        let rcon = Arc::new(RconRegistry::with_system_clock(
            &[RconOptions::new(
                "server1",
                "127.0.0.1",
                1,
                Secret::new("x"),
            )],
            Duration::from_secs(60),
            Arc::clone(&metrics),
        ));
        let masters = Arc::new(
            StoredMasterNames::new(["OwnerAlt"], Arc::clone(&store), Arc::new(SystemClock))
                .unwrap(),
        );
        let tracking = Arc::new(IpTrackingService::new(
            Arc::clone(&store),
            metrics,
            Arc::new(SystemClock),
        ));
        let bans = Arc::new(BanService::new(
            rcon,
            Arc::clone(&store),
            Arc::clone(&masters) as _,
            Arc::new(SystemClock),
        ));
        Self {
            _dir: dir,
            store,
            tracking,
            masters,
            bans,
            firewall: Arc::default(),
        }
    }

    fn firewall_with(&self, enabled: bool, never_block: &[&str]) -> BanFirewall {
        BanFirewall::new(
            Arc::clone(&self.bans),
            Arc::clone(&self.tracking),
            Arc::clone(&self.masters),
            Arc::clone(&self.firewall) as _,
            Arc::clone(&self.store),
            enabled,
            &never_block
                .iter()
                .map(|s| (*s).to_owned())
                .collect::<Vec<_>>(),
            Arc::new(SystemClock),
        )
    }

    fn firewall(&self) -> BanFirewall {
        self.firewall_with(true, &[])
    }

    async fn seen(&self, id: &str, name: &str, ips: &[&str]) {
        let mut accounts: IgnoreCaseMap<AccountRecord> = self
            .store
            .read(KNOWN_PLAYERS, IgnoreCaseMap::default())
            .unwrap();
        accounts.insert(
            id,
            AccountRecord {
                confirmed_ips: ips.iter().map(|s| (*s).to_owned()).collect(),
                names: vec![name.to_owned()],
                ..AccountRecord::empty(id)
            },
        );
        self.store.write(KNOWN_PLAYERS, &accounts).await.unwrap();
    }

    async fn ban(&self, name: &str, id: Option<&str>, length: Option<TimeDelta>) {
        let now = Utc::now();
        let record = BanRecord {
            unique_id: id.map(str::to_owned),
            at: Some(now),
            permanent: length.is_none(),
            expires: length.map(|l| now + l),
            ..BanRecord::for_player(name)
        };
        self.store
            .update(TEMP_BANS, Vec::new, |mut bans: Vec<BanRecord>| {
                bans.push(record);
                Some(bans)
            })
            .await
            .unwrap();
    }

    async fn unban(&self, name: &str) {
        self.store
            .update(TEMP_BANS, Vec::new, |mut bans: Vec<BanRecord>| {
                bans.retain(|b| b.player_id != name);
                Some(bans)
            })
            .await
            .unwrap();
    }
}

fn sorted(mut v: Vec<String>) -> Vec<String> {
    v.sort();
    v
}

#[tokio::test]
async fn a_banned_players_addresses_are_denied() {
    let f = Fixture::new();
    f.seen("acct1", "Cheater", &[BANNED_IP, OTHER_IP]).await;
    f.ban("Cheater", Some("acct1"), None).await;

    let pass = f.firewall().sync().await;

    assert_eq!(pass.denied, 2);
    assert_eq!(sorted(f.firewall.denied()), [OTHER_IP, BANNED_IP]);
    assert_eq!(f.firewall().managed()[BANNED_IP].player, "Cheater");
}

#[tokio::test]
async fn a_temp_ban_is_denied_too() {
    let f = Fixture::new();
    f.seen("acct1", "Cheater", &[BANNED_IP]).await;
    f.ban("Cheater", Some("acct1"), Some(TimeDelta::days(2)))
        .await;

    f.firewall().sync().await;

    assert_eq!(f.firewall.denied(), [BANNED_IP]);
}

#[tokio::test]
async fn the_rule_is_lifted_when_the_ban_ends() {
    let f = Fixture::new();
    f.seen("acct1", "Cheater", &[BANNED_IP]).await;
    f.ban("Cheater", Some("acct1"), None).await;
    f.firewall().sync().await;

    f.unban("Cheater").await;
    let pass = f.firewall().sync().await;

    assert_eq!(pass.lifted, 1);
    assert_eq!(f.firewall.undenied(), [BANNED_IP]);
    assert!(f.firewall().managed().is_empty());
}

#[tokio::test]
async fn an_expired_temp_ban_is_lifted() {
    let f = Fixture::new();
    f.seen("acct1", "Cheater", &[BANNED_IP]).await;
    f.ban("Cheater", Some("acct1"), Some(TimeDelta::days(1)))
        .await;
    f.firewall().sync().await;

    // Served: rewrite the record as already expired.
    f.store
        .update(TEMP_BANS, Vec::new, |bans: Vec<BanRecord>| {
            Some(
                bans.into_iter()
                    .map(|b| BanRecord {
                        expires: Some(Utc::now() - TimeDelta::minutes(1)),
                        ..b
                    })
                    .collect(),
            )
        })
        .await
        .unwrap();
    f.firewall().sync().await;

    assert_eq!(f.firewall.undenied(), [BANNED_IP]);
}

#[tokio::test]
async fn a_second_pass_changes_nothing() {
    let f = Fixture::new();
    f.seen("acct1", "Cheater", &[BANNED_IP]).await;
    f.ban("Cheater", Some("acct1"), None).await;
    f.firewall().sync().await;

    let pass = f.firewall().sync().await;

    assert_eq!(pass, BanFirewallPass::default());
    assert_eq!(f.firewall.denied().len(), 1);
}

#[tokio::test]
async fn an_address_learned_after_the_ban_is_denied_on_the_next_pass() {
    let f = Fixture::new();
    f.ban("Cheater", Some("acct1"), None).await;
    f.firewall().sync().await;
    assert!(f.firewall.denied().is_empty());

    f.seen("acct1", "Cheater", &[BANNED_IP]).await;
    f.firewall().sync().await;

    assert_eq!(f.firewall.denied(), [BANNED_IP]);
}

#[tokio::test]
async fn non_public_addresses_are_never_denied() {
    // Loopback above all: RCON runs over it.
    for ip in [
        "127.0.0.1",
        "10.1.2.3",
        "172.20.0.5",
        "192.168.1.10",
        "100.64.3.4",
        "169.254.1.1",
        "::1",
        "fe80::1",
        "fd00::5",
    ] {
        let f = Fixture::new();
        f.seen("acct1", "Cheater", &[ip]).await;
        f.ban("Cheater", Some("acct1"), None).await;

        f.firewall().sync().await;

        assert!(f.firewall.denied().is_empty(), "{ip}");
    }
}

#[tokio::test]
async fn an_address_a_master_name_uses_is_never_denied() {
    let f = Fixture::new();
    f.seen("owner", "OwnerAlt", &[BANNED_IP]).await;
    f.seen("acct1", "Cheater", &[BANNED_IP, OTHER_IP]).await;
    f.ban("Cheater", Some("acct1"), None).await;

    f.firewall().sync().await;

    assert_eq!(f.firewall.denied(), [OTHER_IP]);
}

#[tokio::test]
async fn never_block_addresses_are_never_denied() {
    let f = Fixture::new();
    f.seen("acct1", "Cheater", &[BANNED_IP, OTHER_IP]).await;
    f.ban("Cheater", Some("acct1"), None).await;

    f.firewall_with(true, &[BANNED_IP, "not-an-address"])
        .sync()
        .await;

    assert_eq!(f.firewall.denied(), [OTHER_IP]);
}

#[tokio::test]
async fn a_manual_blacklist_rule_is_never_touched() {
    let f = Fixture::new();
    f.tracking.flag_address_manually(BANNED_IP).await;
    f.seen("acct1", "Cheater", &[BANNED_IP]).await;
    f.ban("Cheater", Some("acct1"), None).await;

    f.firewall().sync().await;
    f.unban("Cheater").await;
    f.firewall().sync().await;

    assert!(f.firewall.denied().is_empty());
    assert!(f.firewall.undenied().is_empty());
}

#[tokio::test]
async fn a_protected_players_ban_is_not_denied() {
    let f = Fixture::new();
    f.masters.protect("Cheater").await.unwrap();
    f.seen("acct1", "Cheater", &[BANNED_IP]).await;
    f.ban("Cheater", Some("acct1"), None).await;

    f.firewall().sync().await;

    assert!(f.firewall.denied().is_empty());
}

#[tokio::test]
async fn turning_it_off_lifts_every_rule_it_added() {
    let f = Fixture::new();
    f.seen("acct1", "Cheater", &[BANNED_IP]).await;
    f.ban("Cheater", Some("acct1"), None).await;
    f.firewall().sync().await;

    f.firewall_with(false, &[]).sync().await;

    assert_eq!(f.firewall.undenied(), [BANNED_IP]);
    assert!(f.firewall().managed().is_empty());
}

#[tokio::test]
async fn a_failed_deny_is_not_recorded_and_waits_before_retrying() {
    let f = Fixture::new();
    f.seen("acct1", "Cheater", &[BANNED_IP]).await;
    f.ban("Cheater", Some("acct1"), None).await;
    f.firewall.fail.store(true, Ordering::SeqCst);
    let firewall = f.firewall();

    let first = firewall.sync().await;
    let second = firewall.sync().await;

    assert_eq!(first.failed, 1);
    assert_eq!(second, BanFirewallPass::default()); // backing off, not hammering ufw
    assert_eq!(f.firewall.denied().len(), 1);
    assert!(firewall.managed().is_empty());
}

#[tokio::test]
async fn a_flood_of_bans_is_spread_over_passes() {
    let f = Fixture::new();
    for i in 0..MAX_CHANGES_PER_PASS + 5 {
        f.seen(
            &format!("acct{i}"),
            &format!("Player{i}"),
            &[&format!("203.0.113.{}", i + 1)],
        )
        .await;
        f.ban(&format!("Player{i}"), Some(&format!("acct{i}")), None)
            .await;
    }

    let first = f.firewall().sync().await;
    let second = f.firewall().sync().await;

    assert_eq!(first.denied, MAX_CHANGES_PER_PASS);
    assert_eq!(first.deferred, 5);
    assert_eq!(second.denied, 5);
}
