//! What staff are told about a player before they meet them (`Core/Intelligence`).

mod addresses;
mod profile;
mod risk;

pub use addresses::Addresses;
pub use profile::{
    PlayerProfile, ProfileActivity, ProfileAssociation, ProfileCombat, ProfileEconomy,
    ProfileFaction, ProfileIdentity, ProfileModeration, ProfileNetwork, ProfileRedaction,
    ProfileVisibility,
};
pub use risk::{
    RiskAssessment, RiskBand, RiskConfidence, RiskRecommendation, RiskScorer, RiskSignal,
    RiskSignalKind,
};
