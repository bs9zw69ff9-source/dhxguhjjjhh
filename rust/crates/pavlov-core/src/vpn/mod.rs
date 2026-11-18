//! VPN and proxy screening (`Core/Vpn`). For now only what configuration fills in: the
//! provider keys and the vote thresholds. The detectors and verdicts arrive in M2 and M6.

mod keys;
mod thresholds;

pub use keys::VpnKeys;
pub use thresholds::VpnThresholds;
