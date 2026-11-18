//! The bot's configuration, ported from `dotnet/src/PavlovBot.Host/Configuration`.
//!
//! - [`dotenv`]: the `.env` grammar, exactly dotenv 16's.
//! - [`Config`]: the `.env` with the process environment over it, keys compared ignoring case.
//! - [`BotOptions`]: what the bot cannot run without, with [`BotOptions::validate`] reporting
//!   every problem at once.
//! - [`FeatureOptions`]: what turns features on and off, with a one-line-each startup summary.
//!
//! Secrets (tokens, passwords, API keys, webhook URLs) are held as [`Secret`], which never
//! prints its value.

mod bot_options;
pub mod dotenv;
mod env_file;
mod feature_options;
mod rcon;
mod source;
pub mod values;

pub use bot_options::{BotOptions, MonitoringOptions};
pub use env_file::env_file_too_open;
pub use feature_options::FeatureOptions;
pub use pavlov_core::security::Secret;
pub use rcon::RconOptions;
pub use source::{Config, ConfigError};
