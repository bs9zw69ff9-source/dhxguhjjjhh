//! Standing up a new Pavlov server: what it is, whether it is safe, and every file it writes
//! (C# `Core/Provisioning`).
//!
//! The dangerous parts of provisioning are text and alignment, so that is what lives here, pure:
//! the files must match what the game reads, the RCON password must survive being written
//! unquoted into two different file formats, and the slot arithmetic must REFUSE a layout it
//! cannot align rather than bind a unit to the wrong server. The privileged OS work is the host
//! crate's.

pub mod env_file;
pub mod ports;
mod slots;
mod spec;
pub mod text;
pub mod validation;

pub use slots::{ServerLayout, SlotPlan, plan, target_slot};
pub use spec::{MapEntry, ServerProvisionSpec};
