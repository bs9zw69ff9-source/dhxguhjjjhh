//! The slash commands (C# `Discord/Commands`). Each module holds one command's pure parts -
//! what it renders and decides - alongside the command itself, so the rendering is pinned by
//! tests without a gateway.

pub mod arrest;
pub mod check_ban;
pub mod counts;
pub mod errors;
pub mod license;
pub mod membership_reply;
pub mod players;
pub mod provision_server;
pub mod rank_change;
pub mod set_roles;
pub mod subclass;
pub mod test_alert;
pub mod whitelist;
pub mod whitelist_command;
