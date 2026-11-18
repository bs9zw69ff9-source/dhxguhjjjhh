//! The bot's work on the box and on its game servers (C# `PavlovBot.Host`, everything but
//! `Discord/`).
//!
//! ONE CRATE, NOT TWO. The plan split this into a host crate and a services crate, but the C#
//! types reference each other across those lines (map rotation needs the RCON registry, which
//! needs the Pavlov.log confirmations and the metrics), so splitting would mean trait seams that
//! exist only to satisfy a crate boundary.
//!
//! LINUX ONLY, by design: `systemctl`, `ufw` and Linux paths. Every external command is an argv
//! array through [`process::CommandRunner`] - never a shell - so tests assert the exact command
//! lines without running anything privileged.

#![forbid(unsafe_code)]

pub mod analytics;
pub mod confirmations;
pub mod crash_recovery;
pub mod economy;
pub mod factions;
pub mod feeds;
pub mod firewall;
pub mod handlers;
pub mod health;
pub mod http;
pub mod intelligence;
pub mod kill_stats;
pub mod layout_gate;
pub mod logs;
pub mod master_list;
pub mod metrics;
pub mod moderation;
pub mod monitoring;
pub mod monitoring_server;
pub mod notice;
pub mod preserved;
pub mod process;
pub mod provisioner;
pub mod queue;
pub mod recent_errors;
pub mod registry;
pub mod server_labels;
pub mod service_control;
pub mod services;
pub mod supervisor;
pub mod timeline;
pub mod vpn;
