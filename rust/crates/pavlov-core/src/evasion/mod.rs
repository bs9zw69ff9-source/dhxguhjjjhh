//! Ban-evasion correlation (`Core/Evasion`): who a joining player might be, and why.

mod models;

pub use models::{
    BanNetwork, BanRecord, EvasionMatch, EvasionReason, EvasionResult, FlagHit, JoinContext,
    RegistryFlags, SignalKind,
};
