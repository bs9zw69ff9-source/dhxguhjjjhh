//! VPN and proxy screening against third-party providers, and where an address is (C#
//! `Host/Vpn`). The decisions themselves are pure and live in `pavlov_core::vpn`.

pub mod detectors;
pub mod geo;
pub mod json_client;
pub mod screening;
pub mod static_map;

pub use detectors::{DetectorOutcome, VpnDetector};
pub use geo::{GeoLocator, GeoLocatorChain};
pub use json_client::{FetchOptions, JsonFetch, ResilientJsonClient};
pub use screening::{DetectorProbe, VpnScreeningService};
pub use static_map::StaticMap;
