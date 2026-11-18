//! Everything the features read from the environment (C# `Configuration/FeatureOptions.cs`).
//!
//! Separate from `BotOptions` because these settings share one property: an absent one turns
//! a FEATURE off rather than being a misconfiguration. A bot with no webhooks and no VPN keys
//! is a valid bot that simply does less, so none of these fail validation; each says so in the
//! startup summary ([`FeatureOptions::describe`]) instead.

use std::time::Duration;

use pavlov_core::factions::FactionRegistry;
use pavlov_core::monitoring::MonitorSettings;
use pavlov_core::security::{OwnerGuard, Secret};
use pavlov_core::servers::installs::{self, DEFAULT_BASE};
use pavlov_core::servers::units::{default_units, is_plausible_unit_name, parse_units};
use pavlov_core::servers::{DetectedServer, HostProbe, crash_recovery, units_for};
use pavlov_core::text::case::IgnoreCaseSet;
use pavlov_core::text::number::{fixed, group_thousands};
use pavlov_core::vpn::{VpnKeys, VpnThresholds};

use crate::Config;
use crate::values::{
    days, flag, list, minutes, money, non_negative, optional_flag, positive, positive_milliseconds,
    seconds, snowflake, snowflakes, text,
};

/// The plugin settings the removed plugin system read, reported at startup if still set.
const PLUGIN_SETTINGS: [&str; 4] = [
    "PLUGIN_DIR",
    "PLUGINS_ENABLED",
    "PLUGINS_DISABLED",
    "PLUGINS_ALLOW_ROOT",
];

/// The feature settings. Field docs carry the C# reasoning where it decides behaviour.
#[derive(Debug, Clone)]
pub struct FeatureOptions {
    /// Where the game writes its per-player balance files (`MODSAVE_PATH`). None disables the economy.
    pub ledger_directory: Option<String>,
    /// `Pavlov.log` paths, comma separated (`PAVLOV_LOGS`). None means auto-detect.
    pub log_paths: Option<String>,
    /// Pavlov's `Stats.log` files (`STATS_LOGS`). None derives them from the log paths.
    pub stats_log_paths: Option<String>,
    /// Where the game keeps its whitelist rosters (`FACTION_ROLES_PATH`). None disables the
    /// faction commands.
    pub roster_directory: Option<String>,
    /// systemd units for the game servers, in server order: `PAVLOV_UNITS`, else the units
    /// systemd runs each install with, else the defaults. Validated, because these become
    /// arguments to a privileged command.
    pub pavlov_units: Vec<String>,
    /// Force `sudo` for systemctl on or off (`PAVLOV_SYSTEMCTL_SUDO`). None detects whether the
    /// bot is already root.
    pub systemctl_sudo: Option<bool>,
    /// The first of [`ban_file_paths`](Self::ban_file_paths), for anything that names one file.
    pub ban_file_path: Option<String>,
    /// Every install's `Config/blacklist.txt`, so a ban or unban reaches all of them; exactly
    /// `BLACKLIST_PATH` when that is set; empty with `BLACKLIST_SYNC=false`, which is how a
    /// second bot sharing an install keeps its hands off the file.
    pub ban_file_paths: Vec<String>,
    /// Every Pavlov install root on the box.
    pub install_roots: Vec<String>,
    /// A retired `MODSAVE_BLACKLIST_PATH`, so startup can say it is ignored. Decides nothing.
    pub ignored_ban_file_path: Option<String>,
    /// Keep the ModSave tree identical across installs. Opt-OUT (`MODSAVE_SYNC=off`): a
    /// multi-install box that is not syncing is the surprising, money-losing state.
    pub mod_save_sync: bool,
    /// Extra path fragments never mirrored (`MODSAVE_SYNC_SKIP_EXTRA`).
    pub mod_save_sync_skip_extra: Vec<String>,
    /// Plugin settings still present, which nothing reads since plugins were removed.
    pub ignored_plugin_settings: Vec<String>,

    pub join_webhook: Option<Secret>,
    pub kill_webhook: Option<Secret>,
    pub rcon_webhook: Option<Secret>,
    /// The address-bearing connection feed. Private channels only.
    pub connect_webhook: Option<Secret>,
    /// Staff actions as they happen; names the staffer, the target and the reason.
    pub staff_webhook: Option<Secret>,

    pub mod_log_channel: Option<u64>,
    /// The name stamped on every embed (`BOT_NAME`).
    pub bot_name: Option<String>,
    /// `THEME`: `police`, the only skin. `fallout` is refused at startup.
    pub theme_name: Option<String>,
    /// Bans, unbans and automatic bans. Falls back to the mod log.
    pub ban_log_channel: Option<u64>,
    /// Arrests, warrants, sentence releases and rank suspensions.
    pub police_log_channel: Option<u64>,
    /// Overrides the police log for arrest bookings only.
    pub arrest_channel: Option<u64>,
    /// Every slash command anyone runs, refused and failed ones included.
    pub command_log_channel: Option<u64>,
    pub leaderboard_channel: Option<u64>,
    pub arrest_board_channel: Option<u64>,
    pub kd_board_channel: Option<u64>,
    pub warrant_board_channel: Option<u64>,
    /// The live player board (`PLAYERLIST_CHANNEL`, the Node bot's name). It PUBLISHES NAMES.
    pub player_board_channel: Option<u64>,

    /// Paid to each on-duty member per period. Zero disables payroll.
    pub payroll_amount: i64,
    /// How often wages are paid. One period per run, never a backlog.
    pub payroll_interval: Duration,
    /// How long timeline events are kept; ZERO SWITCHES THE TIMELINE OFF, as documented. (The
    /// C# read 0 as "unset" and kept 90 days, so the documented off switch never worked.)
    pub event_retention_days: u32,
    /// Which faction draws a wage. Its former name, NYPD, still resolves to the GWPD.
    pub payroll_faction: String,
    /// Restart a crashed server automatically. Off unless asked for.
    pub crash_recovery: bool,
    /// A JSON file of ladders that are somebody's own. None means the built-in set.
    pub factions_path: Option<String>,
    /// A built-in faction set by name (`FACTION_SET`).
    pub faction_set_name: Option<String>,
    /// Paths this bot must never WRITE to, for a second bot sharing one game server.
    pub ignored_paths: Vec<String>,
    /// Voice channels renamed to each server's live player count, in server order.
    pub player_count_channels: Vec<u64>,
    /// A voice channel for the platform-wide total.
    pub shack_total_channel: Option<u64>,
    /// The capacity shown when a server has never reported one. Shack servers are 24.
    pub default_server_capacity: u32,

    pub verify_channel: Option<u64>,
    pub verify_staff_channel: Option<u64>,
    /// Without it, approval records the link and grants nothing.
    pub verified_role: Option<u64>,
    pub menu_panel_channel: Option<u64>,
    pub menu_role_staff: Option<u64>,
    pub menu_role_high_staff: Option<u64>,
    pub menu_role_blacklist: Option<u64>,

    pub vpn_keys: VpnKeys,
    pub vpn_thresholds: VpnThresholds,
    /// Whether a VPN verdict may issue an automatic PERMANENT ban. THE ONE SWITCH THAT STOPS
    /// THE BOT BANNING PEOPLE BY ITSELF on a probabilistic third-party verdict. Off still
    /// screens and reports; it only withholds the consequence.
    pub vpn_auto_ban: bool,
    pub vpn_cache_ttl: Duration,
    /// Geoapify: where an address is, never whether it is a VPN, so not one of the voters.
    pub geoapify_key: Option<Secret>,

    /// Who is messaged about evasion, alts and VPNs. Unset falls back to the owners.
    pub security_dm_ids: Vec<u64>,
    pub owners: Vec<u64>,
    pub super_owners: Vec<u64>,
    /// Users barred from every command (`BLACKLIST_IDS`), split on spaces as well as commas
    /// as the Node bot split it.
    pub barred_user_ids: Vec<u64>,
    /// In-game names that must never be banned by any path, split on spaces and commas.
    pub master_names: Vec<String>,

    /// The Pavlov build the public server browser asks about. None uses the built-in default.
    pub pavlov_version: Option<String>,
    pub log_poll_interval: Duration,
    /// How often the boards redraw - also the delay before the first one after a restart.
    pub leaderboard_interval: Duration,
    pub ban_sweep_interval: Duration,
    pub ban_expiry_interval: Duration,
    pub ban_reconcile_interval: Duration,

    pub monitoring_enabled: bool,
    pub monitor_alert_channel: Option<u64>,
    pub monitor_alert_role: Option<u64>,
    /// One live board instead of a stream of alerts. On by default.
    pub monitor_board_enabled: bool,
    pub monitor_settings: MonitorSettings,

    /// Deny every address of every actively banned player at ufw. On by default.
    pub firewall_bans: bool,
    /// Addresses the ban firewall must never deny: a ufw deny blocks every port, SSH included.
    pub firewall_never_block: Vec<String>,
    /// Also deny a manually blacklisted address at ufw. On by default.
    pub firewall_blacklisted_ips: bool,
}

/// A probe that answers systemd from one reading, so every decision in one bind sees the
/// same machine.
struct OneReading<'a> {
    host: &'a dyn HostProbe,
    systemd: Vec<DetectedServer>,
}

impl HostProbe for OneReading<'_> {
    fn systemd_servers(&self) -> Vec<DetectedServer> {
        self.systemd.clone()
    }
    fn installs_beside(&self, parent: &str, prefix: &str) -> std::io::Result<Vec<String>> {
        self.host.installs_beside(parent, prefix)
    }
    fn directory_exists(&self, path: &str) -> bool {
        self.host.directory_exists(path)
    }
}

impl FeatureOptions {
    /// Read the settings. `host` answers the questions about the machine: which systemd
    /// units run Pavlov and which installs exist.
    #[must_use]
    pub fn bind(config: &Config, host: &dyn HostProbe) -> Self {
        let host = OneReading {
            host,
            systemd: host.systemd_servers(),
        };
        let roots = installs::discover(
            text(config, "PAVLOV_BASES").as_deref(),
            text(config, "PAVLOV_BASE_1").as_deref(),
            &host,
        )
        .roots;
        let ban_file_paths = ban_file_paths(config, &roots);

        let secret = |key: &str| text(config, key).map(Secret::new);

        Self {
            ledger_directory: text(config, "MODSAVE_PATH"),
            log_paths: text(config, "PAVLOV_LOGS"),
            stats_log_paths: text(config, "STATS_LOGS"),
            roster_directory: text(config, "FACTION_ROLES_PATH"),
            pavlov_units: pavlov_units(config, &roots, &host),
            systemctl_sudo: optional_flag(config, "PAVLOV_SYSTEMCTL_SUDO"),
            /* THE FILE THE SERVER ACTUALLY READS, Config/blacklist.txt. It used to default to
            a mod's ModSave/banlist.txt, which the server does not enforce, and
            MODSAVE_BLACKLIST_PATH kept that wrong file alive; it is reported, not read. */
            ban_file_path: ban_file_paths.first().cloned(),
            ban_file_paths,
            install_roots: roots,
            ignored_ban_file_path: text(config, "MODSAVE_BLACKLIST_PATH"),
            mod_save_sync: optional_flag(config, "MODSAVE_SYNC") != Some(false),
            mod_save_sync_skip_extra: list(config, "MODSAVE_SYNC_SKIP_EXTRA"),
            ignored_plugin_settings: PLUGIN_SETTINGS
                .into_iter()
                .filter(|key| text(config, key).is_some())
                .map(str::to_owned)
                .collect(),

            join_webhook: secret("JOIN_WEBHOOK_URL"),
            kill_webhook: secret("KILL_WEBHOOK_URL"),
            rcon_webhook: secret("RCON_WEBHOOK_URL"),
            connect_webhook: secret("CONNECT_WEBHOOK_URL"),
            staff_webhook: secret("STAFF_WEBHOOK_URL"),

            mod_log_channel: snowflake(config, "MOD_LOG_CHANNEL"),
            bot_name: text(config, "BOT_NAME"),
            theme_name: text(config, "THEME"),
            ban_log_channel: snowflake(config, "BAN_LOG_CHANNEL"),
            police_log_channel: snowflake(config, "POLICE_LOG_CHANNEL"),
            arrest_channel: snowflake(config, "ARREST_CHANNEL"),
            command_log_channel: snowflake(config, "COMMAND_LOG_CHANNEL"),
            leaderboard_channel: snowflake(config, "LEADERBOARD_CHANNEL"),
            arrest_board_channel: snowflake(config, "ARREST_LEADERBOARD_CHANNEL"),
            kd_board_channel: snowflake(config, "KD_LEADERBOARD_CHANNEL"),
            warrant_board_channel: snowflake(config, "WARRANT_BOARD_CHANNEL"),
            player_board_channel: snowflake(config, "PLAYERLIST_CHANNEL"),

            payroll_amount: money(config, "PAYROLL_AMOUNT"),
            payroll_interval: minutes(
                config,
                "PAYROLL_INTERVAL_MINUTES",
                Duration::from_secs(30 * 60),
            ),
            event_retention_days: non_negative(config, "EVENT_RETENTION_DAYS", 90),
            payroll_faction: text(config, "PAYROLL_FACTION")
                .unwrap_or_else(|| FactionRegistry::POLICE_NAME.to_owned()),
            crash_recovery: flag(config, "CRASH_RECOVERY"),
            factions_path: text(config, "FACTIONS_PATH"),
            faction_set_name: text(config, "FACTION_SET"),
            ignored_paths: list(config, "IGNORE_PATHS"),
            player_count_channels: snowflakes(config, "PLAYER_COUNT_CHANNELS", &[',']),
            shack_total_channel: snowflake(config, "SHACK_TOTAL_CHANNEL"),
            default_server_capacity: positive(config, "SERVER_MAX_PLAYERS", 24),

            verify_channel: snowflake(config, "VERIFY_CHANNEL"),
            verify_staff_channel: snowflake(config, "VERIFY_STAFF_CHANNEL"),
            verified_role: snowflake(config, "VERIFIED_ROLE"),
            menu_panel_channel: snowflake(config, "MENU_PANEL_CHANNEL"),
            menu_role_staff: snowflake(config, "MENU_ROLE_STAFF"),
            menu_role_high_staff: snowflake(config, "MENU_ROLE_HIGHSTAFF"),
            menu_role_blacklist: snowflake(config, "MENU_ROLE_BLACKLIST"),

            vpn_keys: VpnKeys {
                ip_hub: secret("IPHUB_API_KEY"),
                ipqs: secret("IPQS_API_KEY"),
                proxy_check: secret("PROXYCHECK_API_KEY"),
                ipapi_is: secret("IPAPIIS_KEY"),
                sentinel: secret("SENTINEL_API_KEY"),
            },
            vpn_thresholds: VpnThresholds {
                screen_min: positive(config, "VPN_SCREEN_MIN", 1),
                confirm_min: positive(config, "VPN_CONFIRM_MIN", 1),
                // VPN_SCREEN_BAN_MIN is the old name, still honoured so an existing .env is
                // not silently reset to the default.
                ban_min: positive(
                    config,
                    "VPN_BAN_MIN",
                    positive(config, "VPN_SCREEN_BAN_MIN", 2),
                ),
            },
            vpn_auto_ban: optional_flag(config, "VPN_AUTOBAN") != Some(false),
            vpn_cache_ttl: days(positive(config, "VPN_CACHE_TTL_DAYS", 30)),
            geoapify_key: secret("GEOAPIFY_API_KEY"),

            security_dm_ids: snowflakes(config, "SECURITY_DM_IDS", &[',']),
            owners: snowflakes(config, "OWNER_IDS", &[',']),
            super_owners: snowflakes(config, "SUPER_OWNER_IDS", &[',']),
            barred_user_ids: snowflakes(config, "BLACKLIST_IDS", &[',', ' ', '\t', '\r', '\n']),
            master_names: master_names(config),

            pavlov_version: text(config, "PAVLOV_VERSION"),
            log_poll_interval: Duration::from_millis(1500),
            leaderboard_interval: positive_milliseconds(
                config,
                "LEADERBOARD_INTERVAL_MS",
                Duration::from_secs(60),
            ),
            ban_sweep_interval: Duration::from_secs(30),
            ban_expiry_interval: Duration::from_secs(60),
            ban_reconcile_interval: Duration::from_secs(5 * 60),

            monitoring_enabled: optional_flag(config, "MONITORING") != Some(false),
            monitor_alert_channel: snowflake(config, "MONITOR_ALERT_CHANNEL"),
            monitor_alert_role: snowflake(config, "MONITOR_ALERT_ROLE"),
            monitor_board_enabled: optional_flag(config, "MONITOR_BOARD") != Some(false),
            monitor_settings: MonitorSettings {
                check_interval: seconds(positive(config, "MONITOR_CHECK_INTERVAL_SECONDS", 30)),
                failure_threshold: positive(config, "MONITOR_FAILURE_THRESHOLD", 3),
                recovery_threshold: positive(config, "MONITOR_RECOVERY_THRESHOLD", 2),
                rcon_timeout: seconds(positive(config, "MONITOR_RCON_TIMEOUT_SECONDS", 5)),
                retry_base_delay: seconds(positive(config, "MONITOR_RETRY_BASE_SECONDS", 2)),
                retry_max_delay: seconds(positive(config, "MONITOR_RETRY_MAX_SECONDS", 120)),
                latency_warn: Duration::from_millis(u64::from(positive(
                    config,
                    "MONITOR_LATENCY_WARN_MS",
                    250,
                ))),
                latency_critical: Duration::from_millis(u64::from(positive(
                    config,
                    "MONITOR_LATENCY_CRITICAL_MS",
                    750,
                ))),
                player_anomaly_drop_fraction: (f64::from(positive(
                    config,
                    "MONITOR_PLAYER_DROP_PERCENT",
                    80,
                )) / 100.0)
                    .clamp(0.0, 1.0),
                player_anomaly_window: seconds(positive(
                    config,
                    "MONITOR_PLAYER_WINDOW_SECONDS",
                    90,
                )),
                log_inactivity_threshold: seconds(
                    positive(config, "MONITOR_LOG_INACTIVITY_MINUTES", 5).saturating_mul(60),
                ),
                escalation_every: positive(config, "MONITOR_ESCALATION_EVERY", 20),
                recovery_messages_enabled: optional_flag(config, "MONITOR_RECOVERY_MESSAGES")
                    != Some(false),
            },

            firewall_bans: optional_flag(config, "FIREWALL_BANS") != Some(false),
            firewall_never_block: list(config, "FIREWALL_NEVER_BLOCK"),
            firewall_blacklisted_ips: optional_flag(config, "FIREWALL_BLACKLIST") != Some(false),
        }
    }

    /// Who actually receives a security alert: the explicit list, or the owners. DEDUPED: an
    /// id in both `OWNER_IDS` and `SUPER_OWNER_IDS` is an ordinary way to write "this person
    /// owns the bot" and must not mean two identical direct messages.
    #[must_use]
    pub fn security_alert_recipients(&self) -> Vec<u64> {
        let source: Vec<u64> = if self.security_dm_ids.is_empty() {
            self.super_owners
                .iter()
                .chain(&self.owners)
                .copied()
                .collect()
        } else {
            self.security_dm_ids.clone()
        };
        let mut recipients = Vec::new();
        for id in source {
            if !recipients.contains(&id) {
                recipients.push(id);
            }
        }
        recipients
    }

    /// Which features are on, one line each, printed at startup: a feature that is off for
    /// want of one variable is otherwise indistinguishable from one that is broken.
    #[must_use]
    pub fn describe(&self, host: &dyn HostProbe) -> Vec<String> {
        let channel = |id: Option<u64>, key: &str| {
            id.map_or_else(
                || format!("off ({key} not set)"),
                |id| format!("channel {id}"),
            )
        };
        let on_off = |set: bool| if set { "on" } else { "off" };
        let unset_or = |id: Option<u64>| id.map_or_else(|| "unset".to_owned(), |id| id.to_string());
        let recipients = self.security_alert_recipients();

        vec![
            format!(
                "economy: {}",
                self.ledger_directory
                    .as_deref()
                    .unwrap_or("off (MODSAVE_PATH not set)")
            ),
            format!(
                "timeline: {}",
                if self.event_retention_days == 0 {
                    "off (EVENT_RETENTION_DAYS=0)".to_owned()
                } else {
                    format!("keeping {} days", self.event_retention_days)
                }
            ),
            /* A PATH THAT DOES NOT EXIST IS REPORTED AS OFF. The roster service treats a
            missing directory like an unset one, so printing the path as though whitelists
            were on would describe a state the bot is not in. */
            format!(
                "whitelists: {}",
                match &self.roster_directory {
                    None => "off (FACTION_ROLES_PATH not set)".to_owned(),
                    Some(dir) if host.directory_exists(dir) => dir.clone(),
                    Some(dir) => format!(
                        "{dir} does not exist yet - the \"faction rosters\" line below says whether it was created"
                    ),
                }
            ),
            format!(
                "ignored paths: {}",
                if self.ignored_paths.is_empty() {
                    "none (IGNORE_PATHS not set) - this bot may write every file it is given"
                        .to_owned()
                } else {
                    self.ignored_paths.join(", ")
                }
            ),
            format!("systemd units: {}", self.pavlov_units.join(", ")),
            format!("join feed: {}", on_off(self.join_webhook.is_some())),
            format!("kill feed: {}", on_off(self.kill_webhook.is_some())),
            format!("rcon feed: {}", on_off(self.rcon_webhook.is_some())),
            format!(
                "cash leaderboard: {}",
                self.leaderboard_channel.map_or_else(
                    || "off (LEADERBOARD_CHANNEL not set)".to_owned(),
                    |id| format!(
                        "channel {id}, every {}s",
                        fixed(self.leaderboard_interval.as_secs_f64(), 0)
                    )
                )
            ),
            format!(
                "arrest board: {}",
                channel(self.arrest_board_channel, "ARREST_LEADERBOARD_CHANNEL")
            ),
            format!(
                "K/D board: {}",
                channel(self.kd_board_channel, "KD_LEADERBOARD_CHANNEL")
            ),
            format!(
                "warrant board: {}",
                channel(self.warrant_board_channel, "WARRANT_BOARD_CHANNEL")
            ),
            format!(
                "player board: {}",
                channel(self.player_board_channel, "PLAYERLIST_CHANNEL")
            ),
            format!(
                "payroll: {}",
                if self.payroll_amount <= 0 {
                    "off (PAYROLL_AMOUNT not set)".to_owned()
                } else {
                    format!(
                        "{} to on-duty {} every {}m",
                        group_thousands(self.payroll_amount),
                        self.payroll_faction,
                        fixed(self.payroll_interval.as_secs_f64() / 60.0, 0)
                    )
                }
            ),
            format!(
                "crash recovery: {}",
                if self.crash_recovery {
                    format!(
                        "on - a failed unit is restarted, up to {}x per {}m",
                        crash_recovery::MAX_ATTEMPTS,
                        fixed(crash_recovery::ATTEMPT_WINDOW.as_secs_f64() / 60.0, 0)
                    )
                } else {
                    "off (CRASH_RECOVERY not set)".to_owned()
                }
            ),
            format!("server monitor: {}", self.describe_monitor()),
            format!(
                "player-count channels: {}",
                if self.player_count_channels.is_empty() {
                    "off (PLAYER_COUNT_CHANNELS not set)".to_owned()
                } else {
                    format!("{} configured", self.player_count_channels.len())
                }
            ),
            format!(
                "shack total channel: {}",
                channel(self.shack_total_channel, "SHACK_TOTAL_CHANNEL")
            ),
            format!(
                "connect feed: {}",
                if self.connect_webhook.is_some() {
                    "on"
                } else {
                    "off (CONNECT_WEBHOOK_URL not set)"
                }
            ),
            format!(
                "staff feed: {}",
                if self.staff_webhook.is_some() {
                    "on"
                } else {
                    "off (STAFF_WEBHOOK_URL not set)"
                }
            ),
            format!(
                "staff log channels: {}",
                if self.mod_log_channel.is_none() && self.ban_log_channel.is_none() {
                    "off (MOD_LOG_CHANNEL / BAN_LOG_CHANNEL not set)".to_owned()
                } else {
                    format!(
                        "mod {}, ban {}",
                        unset_or(self.mod_log_channel),
                        unset_or(self.ban_log_channel)
                    )
                }
            ),
            format!(
                "command log: {}",
                channel(self.command_log_channel, "COMMAND_LOG_CHANNEL")
            ),
            format!("menu panel: {}", self.describe_menu_panel()),
            format!("verification: {}", self.describe_verification()),
            format!(
                "owners: {} configured, plus {} built-in master owners",
                self.owners.len() + self.super_owners.len(),
                OwnerGuard::master_owner_ids().len()
            ),
            format!(
                "command blacklist: {}, plus any barred from /configure",
                if self.barred_user_ids.is_empty() {
                    "none from BLACKLIST_IDS".to_owned()
                } else {
                    format!("{} from BLACKLIST_IDS", self.barred_user_ids.len())
                }
            ),
            format!(
                "security DMs: {}",
                if recipients.is_empty() {
                    "off (SECURITY_DM_IDS is unset and no owners are configured)".to_owned()
                } else {
                    format!(
                        "{} recipient(s){}",
                        recipients.len(),
                        if self.security_dm_ids.is_empty() {
                            " (the owners - SECURITY_DM_IDS is unset)"
                        } else {
                            " (SECURITY_DM_IDS)"
                        }
                    )
                }
            ),
            // Each name on its own, so a space that split two names is visible here.
            format!(
                "master names: {}",
                match OwnerGuard::with_built_in_masters(
                    self.master_names.iter().map(String::as_str)
                ) {
                    Ok(names) => names.iter().collect::<Vec<_>>().join(", "),
                    Err(error) => error.to_string(),
                }
            ),
        ]
    }

    fn describe_monitor(&self) -> String {
        if !self.monitoring_enabled {
            return "off (MONITORING=0)".to_owned();
        }
        let Some(channel) = self.monitor_alert_channel else {
            return "watching, but nothing is posted - set MONITOR_ALERT_CHANNEL for the live board"
                .to_owned();
        };
        format!(
            "{} channel {channel}{}, checked every {}s",
            if self.monitor_board_enabled {
                "live board in"
            } else {
                "per-event alerts to"
            },
            self.monitor_alert_role
                .map_or_else(String::new, |role| format!(", pinging role {role}")),
            fixed(self.monitor_settings.check_interval.as_secs_f64(), 0)
        )
    }

    fn describe_menu_panel(&self) -> String {
        match self.menu_panel_channel {
            None => "off (MENU_PANEL_CHANNEL not set)".to_owned(),
            /* NOT "nobody qualifies": /setrconroles stores a mapping too, and this only sees
            the environment, so an install configured through the command is not broken. */
            Some(channel)
                if self.menu_role_staff.is_none() && self.menu_role_high_staff.is_none() =>
            {
                format!(
                    "channel {channel} - no menu roles in .env; /setrconroles shows what is in force"
                )
            }
            Some(channel) => format!("channel {channel}"),
        }
    }

    fn describe_verification(&self) -> String {
        match (self.verify_channel, self.verify_staff_channel) {
            (Some(panel), Some(staff)) => format!(
                "panel in {panel}, requests to {staff}{}",
                self.verified_role.map_or_else(
                    || " - NO VERIFIED_ROLE, approval grants nothing".to_owned(),
                    |role| format!(", grants role {role}")
                )
            ),
            _ => "off (needs VERIFY_CHANNEL and VERIFY_STAFF_CHANNEL)".to_owned(),
        }
    }
}

/// None when `BLACKLIST_SYNC=false`; exactly `BLACKLIST_PATH` when set; otherwise one per
/// install, never empty.
fn ban_file_paths(config: &Config, roots: &[String]) -> Vec<String> {
    if optional_flag(config, "BLACKLIST_SYNC") == Some(false) {
        return Vec::new();
    }
    if let Some(explicit) = text(config, "BLACKLIST_PATH") {
        return vec![explicit];
    }

    let mut paths: Vec<String> = Vec::new();
    for path in roots.iter().map(|root| installs::blacklist_path(root)) {
        if !path.trim().is_empty() && !paths.contains(&path) {
            paths.push(path);
        }
    }
    if paths.is_empty() {
        vec![installs::blacklist_path(DEFAULT_BASE)]
    } else {
        paths
    }
}

/// `PAVLOV_UNITS` when set; otherwise the unit systemd runs each install with, in install
/// order; the defaults when systemd has no Pavlov units. Detected rather than defaulted: a
/// fixed list was only right for a box laid out exactly that way, and a wrong unit list
/// restarts the wrong server.
fn pavlov_units(config: &Config, roots: &[String], host: &dyn HostProbe) -> Vec<String> {
    if let Some(configured) = text(config, "PAVLOV_UNITS") {
        return parse_units(Some(&configured));
    }
    let detected = host.systemd_servers();
    if detected.is_empty() {
        return default_units();
    }
    let units: Vec<String> = units_for(roots, &detected)
        .into_iter()
        .filter(|u| is_plausible_unit_name(u))
        .collect();
    if units.is_empty() {
        default_units()
    } else {
        units
    }
}

/// `MASTER_NAMES`, split on spaces and tabs as well as commas: "UserOne UserTwo" read as one
/// name granted a player who does not exist and protected nobody. First spelling kept.
fn master_names(config: &Config) -> Vec<String> {
    let names: IgnoreCaseSet = config
        .get("MASTER_NAMES")
        .unwrap_or_default()
        .split([',', ' ', '\t'])
        .map(str::trim)
        .filter(|n| !n.is_empty())
        .collect();
    names.iter().map(str::to_owned).collect()
}
