//! Ban-evasion correlation (`Core/Evasion`): who a joining player might be, and why.

mod ip_registry;
mod models;
mod scorer;

pub use ip_registry::{
    AccountRecord, FlagMatch, FlagMatching, FlagOutcome, FlagSet, FlagVerdict, PendingFlag,
};
pub use models::{
    BanNetwork, BanRecord, EvasionMatch, EvasionReason, EvasionResult, FlagHit, JoinContext,
    RegistryFlags, SignalKind,
};
pub use scorer::EvasionScorer;
