//! API keys for the reputation providers (C# `Host/Vpn/Detectors.cs`, `VpnKeys`).

use crate::security::Secret;

/// Every key is optional and the whole subsystem is a no-op with none set. ipapi.is and
/// proxycheck work keyless, which is why a bot with no configuration at all still screens -
/// at a quota low enough that a busy server needs a key.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct VpnKeys {
    pub ip_hub: Option<Secret>,
    pub ipqs: Option<Secret>,
    pub proxy_check: Option<Secret>,
    pub ipapi_is: Option<Secret>,
    pub sentinel: Option<Secret>,
}

impl VpnKeys {
    /// The keys that are set, for scrubbing out of any provider error before it is logged.
    #[must_use]
    pub fn secrets(&self) -> Vec<&str> {
        [
            &self.ip_hub,
            &self.ipqs,
            &self.proxy_check,
            &self.ipapi_is,
            &self.sentinel,
        ]
        .into_iter()
        .flatten()
        .map(Secret::expose)
        .filter(|k| !k.is_empty())
        .collect()
    }
}
