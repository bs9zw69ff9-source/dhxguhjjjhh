//! Who is on the servers, and whether that answer can be relied on (C#
//! `Host/Rcon/IOnlineRoster.cs`, `IServerLifecycle.cs`).
//!
//! TRAITS BECAUSE OF WHAT DEPENDS ON THEM. Payroll pays real currency to whoever this says is
//! online, and the ledger guard refuses to overwrite the save of a player who may be in game.
//! Depending on the registry directly would make "the roster is an hour stale" - the single
//! most important case to test - impossible to construct.

use futures_util::future::BoxFuture;

/// Who is online, with a separate answer to "can this be acted on".
pub trait OnlineRoster: Send + Sync {
    /// Every player name on any server, deduplicated - INCLUDING a server whose roster is stale.
    /// The conservative answer for a GUARD.
    fn online(&self) -> Vec<String>;

    /// Players on servers whose roster is FRESH - the only list that may be PAID against.
    /// Defaults to [`online`](Self::online) for an implementation with no notion of staleness.
    fn confirmed_online(&self) -> Vec<String> {
        self.online()
    }

    /// Whether at least one server's roster is recent enough to act on. False means "decide
    /// nothing from [`online`](Self::online)" - not "nobody is online".
    fn is_trustworthy(&self) -> bool;
}

/// Lets health tell "stopped on purpose" apart from "not answering".
pub trait ServerLifecycle: Send + Sync {
    /// Whether the server behind this RCON name was stopped deliberately.
    fn is_intentionally_stopped<'a>(&'a self, rcon_server_name: &'a str) -> BoxFuture<'a, bool>;
}
