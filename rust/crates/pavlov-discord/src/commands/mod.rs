//! The slash commands (C# `Discord/Commands`). Each module holds one command's pure parts -
//! what it renders and decides - alongside the command itself, so the rendering is pinned by
//! tests without a gateway.

pub mod admin;
pub mod alts;
pub mod analytics;
pub mod arrest;
pub mod bans;
pub mod check_ban;
pub mod config;
pub mod counts;
pub mod delete_server;
pub mod economy;
pub mod errors;
pub mod faction_stats;
pub mod feeds;
pub mod guild_invites;
pub mod license;
pub mod membership_reply;
pub mod monitor;
pub mod owner;
pub mod players;
pub mod police;
pub mod provision_server;
pub mod rank_change;
pub mod rotate_map;
pub mod server_info;
pub mod server_switch;
pub mod set_roles;
pub mod staff;
pub mod stats;
pub mod subclass;
pub mod test_alert;
pub mod tester;
pub mod utility;
pub mod warn;
pub mod whitelist;
pub mod whitelist_command;
