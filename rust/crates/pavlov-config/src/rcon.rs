//! One game server's RCON settings (C# `PavlovBot.Rcon/RconOptions.cs`).
//!
//! Here rather than in `pavlov-rcon` because configuration builds them and the RCON crate
//! depends on configuration, not the other way round; `pavlov-rcon` re-exports it.

use std::time::Duration;

use pavlov_core::security::Secret;

/// How to reach and pace one server.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RconOptions {
    /// Label used in logs and metrics: `RCON_HOST_2` is "server2".
    pub name: String,
    pub host: String,
    /// As configured: 0 when missing or unparseable, so `BotOptions::validate` can name the
    /// setting instead of the failure surfacing as a refused connection.
    pub port: i32,
    pub password: Secret,
    /// How long a single command may take before it is abandoned.
    pub command_timeout: Duration,
    /// Attempts per command, including the first.
    pub max_attempts: u32,
    /// Minimum gap between commands on one connection. The Pavlov RCON docs advise 100 ms:
    /// the server can silently discard a command that arrives too close behind the last one.
    pub command_spacing: Duration,
    /// How long an idempotent read is reused, so several timers asking for the same roster
    /// seconds apart cost one round trip. Zero disables coalescing.
    pub read_cache_duration: Duration,
    /// Drop and rebuild the connection after this long with no traffic. Pavlov documents no
    /// idle timeout, so rather than discover one as a mysterious failure, recycle first.
    pub idle_timeout: Duration,
}

impl RconOptions {
    /// The C# defaults for everything but the address and the password.
    #[must_use]
    pub fn new(
        name: impl Into<String>,
        host: impl Into<String>,
        port: i32,
        password: Secret,
    ) -> Self {
        Self {
            name: name.into(),
            host: host.into(),
            port,
            password,
            command_timeout: Duration::from_secs(3),
            max_attempts: 3,
            command_spacing: Duration::from_millis(100),
            read_cache_duration: Duration::from_millis(2500),
            idle_timeout: Duration::from_secs(5 * 60),
        }
    }
}
