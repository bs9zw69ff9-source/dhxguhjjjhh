//! `FeatureOptions` beyond what the C# suite pins: the deliberate fixes, the unit list a
//! machine's systemd decides, and the startup summary.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::io;
use std::time::Duration;

use common::{EmptyBox, config, features};
use pavlov_config::FeatureOptions;
use pavlov_core::servers::{DetectedServer, HostProbe};

/// A box whose systemd runs two GWPD servers under its own unit names.
struct TwoServers;

impl HostProbe for TwoServers {
    fn systemd_servers(&self) -> Vec<DetectedServer> {
        ["gwpd-main", "gwpd-events"]
            .iter()
            .enumerate()
            .map(|(i, unit)| DetectedServer {
                unit: (*unit).to_owned(),
                install: format!("/srv/pavlov{i}"),
            })
            .collect()
    }
    fn installs_beside(&self, _: &str, _: &str) -> io::Result<Vec<String>> {
        Ok(Vec::new())
    }
    fn directory_exists(&self, path: &str) -> bool {
        path == "/srv/rosters"
    }
}

/// The C# documented `EVENT_RETENTION_DAYS=0` as "the timeline off" and then read 0 as
/// unset, keeping 90 days: the off switch never worked. Here it does.
#[test]
fn zero_retention_switches_the_timeline_off() {
    assert_eq!(
        features(&[("EVENT_RETENTION_DAYS", "0")]).event_retention_days,
        0
    );
    assert_eq!(features(&[]).event_retention_days, 90);
    assert_eq!(
        features(&[("EVENT_RETENTION_DAYS", "-5")]).event_retention_days,
        90
    );
    assert!(
        features(&[("EVENT_RETENTION_DAYS", "0")])
            .describe(&EmptyBox)
            .contains(&"timeline: off (EVENT_RETENTION_DAYS=0)".to_owned())
    );
}

#[test]
fn the_units_and_installs_come_from_systemd_when_nothing_is_configured() {
    let options = FeatureOptions::bind(&config(&[]), &TwoServers);

    assert_eq!(options.install_roots, ["/srv/pavlov0", "/srv/pavlov1"]);
    assert_eq!(options.pavlov_units, ["gwpd-main", "gwpd-events"]);
    assert_eq!(
        options.ban_file_paths,
        [
            "/srv/pavlov0/Pavlov/Saved/Config/blacklist.txt",
            "/srv/pavlov1/Pavlov/Saved/Config/blacklist.txt"
        ]
    );
}

#[test]
fn configured_units_win_and_no_systemd_means_the_defaults() {
    let configured = FeatureOptions::bind(&config(&[("PAVLOV_UNITS", "a, b")]), &TwoServers);
    assert_eq!(configured.pavlov_units, ["a", "b"]);
    assert_eq!(
        features(&[]).pavlov_units,
        ["pavlovserver", "pavlovserver1", "pavlovserver2"]
    );
}

#[test]
fn the_old_vpn_ban_threshold_name_is_still_honoured() {
    assert_eq!(
        features(&[("VPN_SCREEN_BAN_MIN", "3")])
            .vpn_thresholds
            .ban_min,
        3
    );
    assert_eq!(
        features(&[("VPN_SCREEN_BAN_MIN", "3"), ("VPN_BAN_MIN", "4")])
            .vpn_thresholds
            .ban_min,
        4
    );
    assert_eq!(features(&[]).vpn_thresholds.ban_min, 2);
}

#[test]
fn the_monitor_settings_bind_from_their_keys() {
    let settings = features(&[
        ("MONITOR_CHECK_INTERVAL_SECONDS", "10"),
        ("MONITOR_PLAYER_DROP_PERCENT", "150"),
        ("MONITOR_LOG_INACTIVITY_MINUTES", "2"),
        ("MONITOR_RECOVERY_MESSAGES", "off"),
    ])
    .monitor_settings;

    assert_eq!(settings.check_interval, Duration::from_secs(10));
    assert!((settings.player_anomaly_drop_fraction - 1.0).abs() < f64::EPSILON); // clamped
    assert_eq!(settings.log_inactivity_threshold, Duration::from_secs(120));
    assert!(!settings.recovery_messages_enabled);
    assert_eq!(settings.failure_threshold, 3);
}

#[test]
fn leftover_plugin_settings_are_named_so_startup_can_say_they_are_ignored() {
    let options = features(&[("PLUGIN_DIR", "/srv/plugins"), ("PLUGINS_ENABLED", " ")]);
    assert_eq!(options.ignored_plugin_settings, ["PLUGIN_DIR"]);
}

#[test]
fn webhooks_and_keys_never_print() {
    let options = features(&[
        (
            "JOIN_WEBHOOK_URL",
            "https://discord.com/api/webhooks/1/secret-token",
        ),
        ("IPHUB_API_KEY", "iphub-secret"),
        ("GEOAPIFY_API_KEY", "geo-secret"),
    ]);
    let printed = format!("{options:?}");

    for secret in ["secret-token", "iphub-secret", "geo-secret"] {
        assert!(!printed.contains(secret), "{secret} leaked");
    }
    assert_eq!(options.vpn_keys.secrets(), ["iphub-secret"]);
}

#[test]
fn the_startup_summary_says_what_is_on_and_why_the_rest_is_off() {
    let options = FeatureOptions::bind(
        &config(&[
            ("FACTION_ROLES_PATH", "/srv/rosters"),
            ("PAYROLL_AMOUNT", "1250"),
            ("LEADERBOARD_CHANNEL", "42"),
            ("CRASH_RECOVERY", "yes"),
            ("MONITOR_ALERT_CHANNEL", "7"),
            ("OWNER_IDS", "1,2"),
            ("MASTER_NAMES", "Holosight1"),
        ]),
        &TwoServers,
    );
    let lines = options.describe(&TwoServers);

    for expected in [
        "economy: off (MODSAVE_PATH not set)",
        "timeline: keeping 90 days",
        "whitelists: /srv/rosters",
        "systemd units: gwpd-main, gwpd-events",
        "join feed: off",
        "cash leaderboard: channel 42, every 60s",
        "payroll: 1,250 to on-duty GWPD every 30m",
        "crash recovery: on - a failed unit is restarted, up to 3x per 30m",
        "server monitor: live board in channel 7, checked every 30s",
        "verification: off (needs VERIFY_CHANNEL and VERIFY_STAFF_CHANNEL)",
        "owners: 2 configured, plus 2 built-in master owners",
        "security DMs: 2 recipient(s) (the owners - SECURITY_DM_IDS is unset)",
        "master names: Holosight1, fki6",
    ] {
        assert!(
            lines.iter().any(|l| l == expected),
            "missing {expected:?} in {lines:#?}"
        );
    }
}

#[test]
fn a_roster_directory_that_does_not_exist_is_reported_as_not_there() {
    let lines = features(&[("FACTION_ROLES_PATH", "/nowhere")]).describe(&EmptyBox);
    assert!(
        lines
            .iter()
            .any(|l| l.starts_with("whitelists: /nowhere does not exist yet"))
    );
}

/// The `.env.example` the repository ships parses cleanly: every `KEY=` line becomes a
/// setting, no comment leaks in as one, and both option sets bind from it.
#[test]
fn the_shipped_env_example_parses() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../.env.example");
    let contents = std::fs::read_to_string(&path).unwrap();

    let assignments = contents
        .lines()
        .filter(|line| {
            line.split_once('=').is_some_and(|(key, _)| {
                !key.is_empty()
                    && key
                        .chars()
                        .all(|c| c.is_ascii_uppercase() || c.is_ascii_digit() || c == '_')
            })
        })
        .count();
    let parsed = pavlov_config::dotenv::parse(&contents);

    assert_eq!(parsed.len(), assignments);
    let bot = pavlov_config::BotOptions::bind(&parsed, std::path::Path::new("/srv/bot"));
    assert!(bot.validate().iter().any(|p| p.contains("DISCORD_TOKEN")));
    let features = FeatureOptions::bind(&parsed, &EmptyBox);
    assert_eq!(features.payroll_faction, "GWPD");
    assert_eq!(features.event_retention_days, 90);
}
