//! Faction ladders and the rules the roster files cannot express (`Core/Factions`).
//!
//! POLICE ONLY. The C# also carried the fallout ladders as its default set; the owner dropped
//! the fallout bot (2026-11-17), so the built-in set here is the GWPD. A custom set loaded from
//! `FACTIONS_PATH` is still supported, through [`FactionSet::of`].

mod activity;
mod definition;
mod membership;
mod registry;

pub use activity::{FactionActivity, FactionReport, InactiveMember, MemberActivity};
pub use definition::{FactionDefinition, FactionSet};
pub use membership::{MembershipDecision, MembershipOutcome, MembershipRules};
pub use registry::FactionRegistry;
