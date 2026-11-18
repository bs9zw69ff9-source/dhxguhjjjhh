//! Pavlov's RCON protocol (C# `PavlovBot.Rcon`).
//!
//! NOT Source RCON. Pavlov's is a text protocol over one TCP stream:
//!
//! 1. the server sends `Password: `
//! 2. the client sends the lowercase hex MD5 of the password
//! 3. the server replies `Authenticated=1\r\n` (or `=0`)
//! 4. commands are UTF-8 lines; replies are JSON (or, on some failures, bare text) ending in CRLF
//!
//! [`RconClient`] adds what the Node bot lacked: a PERSISTENT session (Pavlov services RCON on
//! the game thread, so every connection is work taken from the tick), READ COALESCING, and
//! JITTERED RETRY that never re-sends a state-changing command the server may already have run.

#![forbid(unsafe_code)]

pub mod backoff;
mod client;
mod connection;
mod error;
pub mod reply;
mod roster;
#[cfg(feature = "testing")]
pub mod testing;

pub use client::RconClient;
pub use connection::ExchangeProgress;
pub use error::RconError;
pub use pavlov_config::RconOptions;
pub use roster::{OnlineRoster, ServerLifecycle};
