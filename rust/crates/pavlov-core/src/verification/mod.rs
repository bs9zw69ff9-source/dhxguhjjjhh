//! One Discord account, one Pavlov name, no alts (`Core/Verification`).

mod rules;

pub use rules::{VerificationConflict, VerificationRecord, VerificationRules, VerificationVerdict};

/// The component-id prefix of the verification flow's buttons and modal (C#
/// `VerificationService.Id`). Pending requests are keyed by a short TOKEN rather than carrying
/// the name: a 64-character name plus the requester would not fit Discord's 100.
pub const COMPONENT_PREFIX: &str = "verify";
