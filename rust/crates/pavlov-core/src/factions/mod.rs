//! Faction ladders and the rules the roster files cannot express (`Core/Factions`).
//!
//! POLICE ONLY. The owner dropped the fallout bot and its ladders (2026-11-17), in the C# as
//! well, so the built-in set is the GWPD. A custom set loaded from `FACTIONS_PATH` is still
//! supported, through [`FactionSet::of`].

mod activity;
mod definition;
mod membership;
mod registry;
#[cfg(feature = "testing")]
pub mod testing;

pub use activity::{FactionActivity, FactionReport, InactiveMember, MemberActivity};
pub use definition::{FactionDefinition, FactionSet};
pub use membership::{MembershipDecision, MembershipOutcome, MembershipRules};
pub use registry::FactionRegistry;
