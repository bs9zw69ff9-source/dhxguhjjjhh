//! The limits on restarting a crashed game server (from C# `Host/Servers/CrashRecovery.cs`).
//!
//! A server that keeps dying needs a human, and the most useful thing automation can do at
//! that point is stand back and be loud about it: at most [`MAX_ATTEMPTS`] restarts per unit
//! inside [`ATTEMPT_WINDOW`], then it stops and says so once. The restarting itself is
//! `pavlov-host`'s; the numbers are here so the startup summary can state them.

use std::time::Duration;

/// Restarts allowed per unit inside [`ATTEMPT_WINDOW`] before giving up.
pub const MAX_ATTEMPTS: u32 = 3;

/// The window [`MAX_ATTEMPTS`] is counted over.
pub const ATTEMPT_WINDOW: Duration = Duration::from_secs(30 * 60);
