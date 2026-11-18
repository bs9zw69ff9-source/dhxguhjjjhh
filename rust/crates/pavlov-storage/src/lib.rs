//! Where the bot's data lives (C# `Host/Storage`, `Host/Events/SqliteEventStore.cs`,
//! `Host/Economy/LedgerFileStore.cs`).
//!
//! THE STORED SHAPE IS A LIVE CONTRACT. The JSON the Node bot wrote, and the C# bot after it,
//! is what is on the server today: camelCase keys, timestamps in epoch milliseconds, the dataset
//! names in [`datasets`]. A renamed dataset reads as empty rather than failing - a bot that
//! silently forgets every ban - so none of it is a detail to tidy up.
//!
//! Linux-only by design, like the C# host: the game files belong to the user the game runs as,
//! and keeping them that way needs Unix ownership.

#![forbid(unsafe_code)]

pub mod atomic_file;
pub mod datasets;
mod event_store;
mod file_backend;
pub mod game_files;
pub mod installs;
pub mod ledger_store;
pub mod mod_save_sync;
pub mod ownership;
mod sqlite_backend;
pub mod whitelist;

pub use event_store::{EventGrouping, EventStore, NullEventStore, SqliteEventStore};
pub use file_backend::FileKeyValueBackend;
pub use sqlite_backend::SqliteKeyValueBackend;
