//! `PlayerIntelligenceService` over seeded datasets: it joins what other services own, the
//! risk signals follow from them, and the moderation view drops every address. (The C# has no
//! test file for the service itself.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::intelligence::{ProfileVisibility, RiskSignalKind};
use pavlov_core::time::SystemClock;
use pavlov_host::intelligence::{IntelligenceSources, PlayerIntelligenceService};
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::moderation::bans::{BanService, MasterNames};
use pavlov_host::moderation::warnings::WarningService;
use pavlov_host::registry::RconRegistry;

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
    fn exempt<'a>(
        &'a self,
        _: &'a str,
        _: Option<Duration>,
    ) -> futures_util::future::BoxFuture<'a, ()> {
        Box::pin(async {})
    }
}

fn service(seed: &[(&str, String)]) -> PlayerIntelligenceService {
    let backend = Arc::new(MemoryBackend::default());
    for (key, json) in seed {
        backend.seed(key, json);
    }
    let store = Arc::new(SerializedStore::new(backend));
    let clock = Arc::new(SystemClock);
    let metrics = Arc::new(MetricsRegistry::default());
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[],
        Duration::from_secs(60),
        Arc::clone(&metrics),
    ));
    let audit = Arc::new(AuditLog::new(Arc::clone(&store), clock.clone()));
    PlayerIntelligenceService::new(IntelligenceSources {
        tracking: Arc::new(IpTrackingService::new(
            Arc::clone(&store),
            metrics,
            clock.clone(),
        )),
        bans: Arc::new(BanService::new(
            Arc::clone(&rcon),
            Arc::clone(&store),
            Arc::new(NoMasters),
            clock.clone(),
        )),
        warnings: Arc::new(WarningService::new(
            Arc::clone(&store),
            audit,
            clock.clone(),
        )),
        rcon,
        store,
        clock,
        rosters: None,
        members: None,
        balances: None,
        payroll: None,
        vpn: None,
        kill_stats: None,
    })
}

fn ms(hours_ago: i64) -> i64 {
    (Utc::now() - TimeDelta::hours(hours_ago)).timestamp_millis()
}

fn accounts() -> String {
    format!(
        r#"{{
        "acct-alice": {{"id":"acct-alice","confirmedIps":["198.51.100.7"],"guessedIps":[],"names":["Alice","OldAlice"],"firstSeen":{},"lastSeen":{}}},
        "acct-bob": {{"id":"acct-bob","confirmedIps":["198.51.100.7"],"guessedIps":[],"names":["Bob"],"firstSeen":{},"lastSeen":{}}}
        }}"#,
        ms(2),
        ms(1),
        ms(500),
        ms(3)
    )
}

#[test]
fn a_stranger_is_unknown_rather_than_a_row_of_zeroes() {
    let profile = service(&[]).profile("Nobody");
    assert!(!profile.known());
    assert!(profile.risk.signals.is_empty());
}

#[test]
fn the_profile_joins_playtime_names_and_a_banned_associate() {
    let bans = format!(
        r#"[{{"playerId":"Bob","reason":"cheating","permanent":true,"at":{}}}]"#,
        ms(10)
    );
    let playtime = r#"{"Alice":{"player":"Alice","minutes":95}}"#.to_owned();
    let profile = service(&[
        ("known_players", accounts()),
        ("tempbans", bans),
        ("playtime", playtime),
    ])
    .profile("Alice");

    assert!(profile.known());
    assert_eq!(profile.identity.account_id.as_deref(), Some("acct-alice"));
    assert_eq!(profile.identity.previous_names, ["OldAlice"]);
    assert_eq!(profile.activity.playtime_minutes, 95);
    assert!(
        profile
            .associations
            .iter()
            .any(|a| a.banned && a.account_id == "acct-bob")
    );
    let kinds: Vec<RiskSignalKind> = profile.risk.signals.iter().map(|s| s.kind).collect();
    assert!(
        kinds.contains(&RiskSignalKind::SharedAddressWithBanned),
        "{kinds:?}"
    );
    assert!(
        kinds.contains(&RiskSignalKind::BrandNewAccount),
        "first seen two hours ago"
    );
    assert!(!kinds.contains(&RiskSignalKind::BannedAccount));
}

#[test]
fn a_banned_player_says_so_with_the_reason() {
    let bans = format!(
        r#"[{{"playerId":"alice","reason":"cheating","permanent":true,"at":{}}}]"#,
        ms(10)
    );
    let profile = service(&[("known_players", accounts()), ("tempbans", bans)]).profile("Alice");
    assert_eq!(profile.moderation.active_ban.as_deref(), Some("cheating"));
    assert!(profile.moderation.ban_is_permanent);
    assert!(
        profile
            .risk
            .signals
            .iter()
            .any(|s| s.kind == RiskSignalKind::BannedAccount)
    );
}

#[test]
fn the_moderation_view_never_carries_an_address() {
    let bans = format!(
        r#"[{{"playerId":"Bob","reason":"x","permanent":true,"at":{}}}]"#,
        ms(10)
    );
    let profile = service(&[("known_players", accounts()), ("tempbans", bans)])
        .profile_for("Alice", ProfileVisibility::Moderation);
    let json = serde_json::to_string(&profile).unwrap();
    assert!(!json.contains("198.51.100.7"), "{json}");
    assert!(profile.associations.is_empty());
    assert!(profile.redacted);
}

#[test]
fn kicks_and_recent_actions_come_from_the_mod_log() {
    let log = format!(
        r#"[{{"action":"kick","moderator":"mod1","player":"Alice","at":{}}},{{"action":"warn","moderator":"mod2","player":"alice","at":{}}},null]"#,
        ms(5),
        ms(1)
    );
    let profile = service(&[("modlog", log)]).profile("Alice");
    assert_eq!(profile.moderation.kicks, 1);
    assert_eq!(profile.moderation.actions, ["warn by mod2", "kick by mod1"]);
}
