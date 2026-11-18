//! The serenity adapter (C# `DiscordGateway` and the `Gateway*` classes): the ONLY crate that
//! knows Discord's wire types.
//!
//! Everything that decides something (the command runner, the component router, the
//! registration scopes, the staff-guild cache) lives in `pavlov-discord` behind seams and is
//! tested there. This crate converts serenity's interactions into those plain values, and
//! implements the seams over Discord's REST API. Its conversions are tested by serialising the
//! builders; the live gateway itself can only be checked against Discord (see DEPLOY.md).

#![forbid(unsafe_code)]

pub mod client;
pub mod convert;
pub mod handler;
pub mod respond;
pub mod rest;

pub use client::{DiscordClient, Shutdown};
pub use handler::{Role, Suggest, Wiring};
