//! How many reputation providers must agree (C# `Core/Vpn/DetectorReading.cs`, `VpnThresholds`).

/// The vote thresholds.
///
/// `ban_min` is deliberately higher than `screen_min`: individual providers do produce false
/// positives on legitimate infrastructure (ipapi.is reports Cloudflare's 1.1.1.1 as
/// "vpn+abuser"), so a single source must never be enough to ban somebody. Two independent
/// providers agreeing is a different claim entirely.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VpnThresholds {
    /// Tier-1 hits needed to escalate to tier 2.
    pub screen_min: u32,
    /// Tier-2 hits needed to mark the verdict CONFIRMED (display only).
    pub confirm_min: u32,
    /// How many detectors, ACROSS BOTH TIERS, must flag an address before it may be banned.
    pub ban_min: u32,
}

impl Default for VpnThresholds {
    fn default() -> Self {
        Self {
            screen_min: 1,
            confirm_min: 1,
            ban_min: 2,
        }
    }
}

impl VpnThresholds {
    /// Each clamped to at least 1: a threshold of zero would flag every address.
    #[must_use]
    pub fn sanitised(self) -> Self {
        Self {
            screen_min: self.screen_min.max(1),
            confirm_min: self.confirm_min.max(1),
            ban_min: self.ban_min.max(1),
        }
    }
}
