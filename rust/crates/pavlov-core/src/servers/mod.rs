//! The game servers as data: unit names, install locations, the crash-recovery limits, and
//! the master server's public list.
//!
//! In the C# these sit in the Host layer beside the code that runs `systemctl` and reads the
//! disk. The PURE parts live here because configuration (`pavlov-config`), which sits below
//! the host crate, has to resolve the unit list and the ban-file paths at startup. Everything
//! that touches the machine stays behind [`HostProbe`].

pub mod crash_recovery;
mod detection;
pub mod installs;
pub mod listing;
pub mod units;

pub use detection::{DetectedServer, units_for};
pub use installs::{HostProbe, InstallDiscovery, InstallSource};
