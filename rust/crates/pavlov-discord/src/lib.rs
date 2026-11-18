//! The Discord side of the bot (C# `Host/Discord`).
//!
//! DISCORD-AGNOSTIC BY DESIGN. Commands, access and the boards work over plain values: a
//! [`caller::Caller`] (who is asking, with whatever guild membership could be read) and an
//! [`pavlov_core::embed::Embed`] reply. The gateway adapter is the only module that knows
//! Discord's wire types, so everything else is testable with no gateway and no token, which
//! is how the C# suite tested it too (with fake users and members).

#![forbid(unsafe_code)]

pub mod access;
pub mod autopost;
pub mod barred;
pub mod boards;
pub mod caller;
pub mod command;
pub mod command_log;
pub mod component;
pub mod direct_message_alerts;
pub mod dispatcher;
pub mod guild_directory;
pub mod interaction;
pub mod option_name;
pub mod player_board;
pub mod player_count;
pub mod post;
pub mod prefixes;
pub mod rcon_failure;
pub mod rcon_menu;
pub mod runner;
pub mod slash;
pub mod staff_log;
