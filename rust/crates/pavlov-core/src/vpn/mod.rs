//! VPN and proxy screening (`Core/Vpn` and the provider keys from `Host/Vpn`): what the
//! detectors said, the consensus and its separate permission to ban, the merged record, the
//! verdict wording, address classification, and Sentinel's reply. The HTTP detectors
//! themselves are `pavlov-services`'.

mod consensus;
pub mod ip_addresses;
mod keys;
mod merge;
mod reading;
pub mod sentinel;
mod thresholds;
pub mod verdict;

pub use consensus::VpnConsensus;
pub use keys::VpnKeys;
pub use merge::{AsnValue, GeoLocation, VpnMerge, VpnRecord};
pub use reading::{DetectorReading, TierOutcome, VpnDecision};
pub use thresholds::VpnThresholds;
pub use verdict::{VpnStance, VpnSummary};
