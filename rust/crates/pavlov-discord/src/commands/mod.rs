//! The slash commands (C# `Discord/Commands`). Each module holds one command's pure parts -
//! what it renders and decides - alongside the command itself, so the rendering is pinned by
//! tests without a gateway.

pub mod arrest;
pub mod check_ban;
