//! What the bot cannot run without (C# `Configuration/BotOptions.cs`).
//!
//! Binding is explicit rather than generic because the keys are flat names shared with the
//! Node bot, and every "missing" case has its own right answer: an absent metrics port
//! disables the endpoint, an absent RCON password is fatal, an absent interval takes the
//! default.

use std::path::{Path, PathBuf};
use std::time::Duration;

use pavlov_core::security::Secret;
use pavlov_core::text::case::eq_ignore_case;

use crate::values::{flag, integer, milliseconds, positive_milliseconds, text};
use crate::{Config, RconOptions};

/// Highest `RCON_HOST_n` index looked for.
const MAX_SERVERS: u32 = 9;

/// Everything the host needs, read once at startup and then immutable.
#[derive(Debug, Clone)]
pub struct BotOptions {
    pub discord_token: Secret,
    /// Guild to register commands in: instant, where global registration takes up to an hour.
    pub guild_id: Option<u64>,
    /// The guild whose roles decide access when the interaction is not in it (a DM, or a
    /// server the bot was carried into as a user app). Unset falls back to `guild_id`, then to
    /// the only guild the bot is in; a bot in several with none named refuses to pick one,
    /// because "roles from whichever server we share" is a privilege escalation.
    pub home_guild_id: Option<u64>,
    /// Register as a user-installable app. FORCES GLOBAL REGISTRATION and ignores `guild_id`:
    /// Discord does not combine the two, and both would show every command twice.
    pub user_app: bool,
    /// Commands this bot does not run, by name, leading slashes removed.
    pub disabled_commands: Vec<String>,
    /// The whitelist bot: a second Discord application, enabled only with both halves.
    pub faction_token: Option<Secret>,
    pub faction_client_id: Option<u64>,
    pub servers: Vec<RconOptions>,
    pub monitoring: MonitoringOptions,
    /// Where the datasets live.
    pub data_directory: PathBuf,
    pub rcon_health_interval: Duration,
    pub player_cache_interval: Duration,
    pub supervisor_interval: Duration,
}

impl BotOptions {
    /// Read the options. `working_directory` is where `DATA_DIR` defaults to `data/` under.
    #[must_use]
    pub fn bind(config: &Config, working_directory: &Path) -> Self {
        let read_cache = milliseconds(config, "RCON_READ_CACHE_MS", Duration::from_millis(2500));

        let servers = (1..=MAX_SERVERS)
            .filter_map(|i| {
                // Gaps are allowed: 1 and 3 without 2 is a decommissioned server 2.
                let host = text(config, &format!("RCON_HOST_{i}"))?;
                let port = integer::<i32>(config.get(&format!("RCON_PORT_{i}"))).unwrap_or(0);
                // NOT trimmed: a password is what it is, and the .env parser already trims
                // an unquoted value the way dotenv does.
                let password = config
                    .get(&format!("RCON_PASSWORD_{i}"))
                    .unwrap_or_default();
                let mut server =
                    RconOptions::new(format!("server{i}"), host, port, Secret::new(password));
                server.read_cache_duration = read_cache;
                Some(server)
            })
            .collect();

        Self {
            discord_token: Secret::new(config.get("DISCORD_TOKEN").unwrap_or_default().trim()),
            guild_id: integer(config.get("GUILD_ID")),
            home_guild_id: integer(config.get("HOME_GUILD_ID")),
            user_app: flag(config, "USER_APP"),
            /* Leading slashes trimmed, because that is how people write a command name, and a
            list that silently failed on "/arrest" would be worse than one that rejected it. */
            disabled_commands: config
                .get("COMMANDS_DISABLED")
                .unwrap_or_default()
                .split(',')
                .map(|c| c.trim().trim_start_matches('/').trim())
                .filter(|c| !c.is_empty())
                .map(str::to_owned)
                .collect(),
            faction_token: text(config, "FACTION_BOT_TOKEN").map(Secret::new),
            faction_client_id: integer(config.get("FACTION_CLIENT_ID")),
            servers,
            monitoring: MonitoringOptions::bind(config),
            data_directory: text(config, "DATA_DIR")
                .map_or_else(|| working_directory.join("data"), PathBuf::from),
            rcon_health_interval: positive_milliseconds(
                config,
                "RCON_HEALTH_INTERVAL_MS",
                Duration::from_secs(60),
            ),
            player_cache_interval: Duration::from_secs(60),
            supervisor_interval: Duration::from_secs(60),
        }
    }

    /// True when both halves of the whitelist bot are set. One without the other is a
    /// mistake, not a mode.
    #[must_use]
    pub fn faction_bot_enabled(&self) -> bool {
        self.faction_token.as_ref().is_some_and(|t| !t.is_blank())
            && self.faction_client_id.is_some()
    }

    /// Every configuration problem, not just the first: fixing a bot's environment one crash at
    /// a time, with a restart between each, is how a five-minute deploy becomes an hour.
    ///
    /// NO SERVERS IS NOT A PROBLEM. It used to be, and that made deleting the last server a
    /// trap: the bot could not start with none, and `/provisionserver` - the way to make one -
    /// needs the bot running.
    #[must_use]
    pub fn validate(&self) -> Vec<String> {
        let mut problems = Vec::new();

        if self.discord_token.is_blank() {
            problems.push("DISCORD_TOKEN is not set - the bot cannot log in.".to_owned());
        }

        for server in &self.servers {
            let index = server.name.replace("server", "");
            if !(1..=65535).contains(&server.port) {
                problems.push(format!(
                    "RCON_PORT_{index} is missing or not a valid port (got \"{}\").",
                    server.port
                ));
            }
            if server.password.expose().is_empty() {
                problems.push(format!(
                    "RCON_PASSWORD_{index} is not set - the RCON handshake will fail."
                ));
            }
        }

        problems.extend(self.monitoring.validate());
        problems
    }
}

/// The health and metrics endpoint.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MonitoringOptions {
    /// `None` is NOT CONFIGURED - no listener, no attack surface. `Some(-1)` is set but not a
    /// number. A tri-state on purpose: the Node bot read a configured port of 0 as "disabled"
    /// because its check was a truthiness test, and the endpoint silently never came up.
    pub port: Option<i32>,
    /// Defaults to loopback. These endpoints expose service names, error messages and internal
    /// state; on a game host with a public interface, 0.0.0.0 would publish that to the
    /// internet. Widening it has to be a decision somebody made.
    pub host: String,
    /// Optional bearer token, for when the endpoint must be exposed.
    pub token: Option<Secret>,
}

impl MonitoringOptions {
    #[must_use]
    pub fn bind(config: &Config) -> Self {
        let port = text(config, "METRICS_PORT").map(|raw| integer(Some(&raw)).unwrap_or(-1));
        Self {
            port,
            host: text(config, "METRICS_HOST").unwrap_or_else(|| "127.0.0.1".to_owned()),
            token: text(config, "METRICS_TOKEN").map(Secret::new),
        }
    }

    /// Whether the endpoint should listen at all.
    #[must_use]
    pub fn enabled(&self) -> bool {
        self.port.is_some()
    }

    #[must_use]
    pub fn validate(&self) -> Vec<String> {
        let Some(port) = self.port else {
            return Vec::new();
        };
        let mut problems = Vec::new();

        if !(1..=65535).contains(&port) {
            problems.push(format!(
                "METRICS_PORT is set but is not a usable port (got \"{port}\"). \
                 Leave it unset to disable the endpoint. Port 0 (ephemeral) is not supported: \
                 the health check and the watchdog need to know where to find it."
            ));
        }

        if self.host != "127.0.0.1"
            && !eq_ignore_case(&self.host, "localhost")
            && self.token.is_none()
        {
            problems.push(format!(
                "METRICS_HOST is \"{}\" (not loopback) but METRICS_TOKEN is not set - \
                 the health report would be readable by anyone who can reach the port.",
                self.host
            ));
        }
        problems
    }
}
