//! Keeping every install's ModSave tree identical without losing money (C# `Core/Sync`).
//!
//! The planner decides from modified times alone; the content guards in [`ledger_file`] read
//! the handful of files a candidate actually touches. Everything here is pure, so the whole
//! decision is testable without a disk.

pub mod ledger_file;
mod planner;
mod player_save;

pub use planner::{DEFAULT_TOLERANCE_MS, ModSaveCopy, ModSaveSnapshot, candidates};
pub use player_save::{MONEY, PlayerSave, UPDATED, UPDATED_FORMAT};
