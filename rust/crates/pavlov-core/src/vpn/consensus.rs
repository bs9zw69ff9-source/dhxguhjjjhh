//! Turning what the detectors said into a verdict, and separately into permission to ban (C#
//! `Core/Vpn/VpnConsensus.cs`).
//!
//! Two stacked tiers keep accuracy high without burning quota: TIER 1, the regular check, on
//! every not-yet-checked address; TIER 2, the final confirmation with a tiny quota, ONLY on a
//! screen hit. A clean screen ends the check.
//!
//! THE VERDICT AND THE PERMISSION TO BAN ARE SEPARATE, and separating them was a bug fix: as one
//! tri-state, an inconclusive `null` read as permission to ban and banned players on a single
//! screening hit.
//!
//! PERMISSION TO BAN IS A HEAD COUNT ACROSS BOTH TIERS: at least `ban_min` detectors flagged
//! the address. The confirmer used to hold a VETO, which on a 35-lookups-a-day key silently
//! became "auto-ban is off" whenever its quota ran out. It still names the verdict confirmed
//! or disputed; it no longer overrules a consensus by itself.

use super::{TierOutcome, VpnDecision, VpnThresholds};

/// Namespace, mirroring the C# static class.
#[derive(Debug)]
pub struct VpnConsensus;

impl VpnConsensus {
    /// `screen` is `None` when detection is disabled; `confirm` is `None` when the screen came
    /// back clean. `configured_confirmers` tells "all confirmers failed" apart from "none
    /// configured", which need different fixes.
    #[must_use]
    pub fn decide(
        screen: Option<&TierOutcome>,
        confirm: Option<&TierOutcome>,
        configured_confirmers: u32,
        thresholds: VpnThresholds,
    ) -> VpnDecision {
        let limits = thresholds.sanitised();

        let Some(screen) = screen else {
            return VpnDecision::new(false, None, false, "detection disabled");
        };

        /* An outage must NEVER be cached as clean: the caller treats this as "do not cache,
        retry on the next connection", or a five-minute provider blip would whitelist every
        address that connected during it. */
        if screen.answered == 0 {
            return VpnDecision::new(
                false,
                None,
                false,
                "every regular check failed - no verdict",
            );
        }

        if screen.hits < limits.screen_min {
            return VpnDecision::new(false, None, false, "all regular checks clean");
        }

        let hits = screen.hits + confirm.map_or(0, |c| c.hits);
        let answered = screen.answered + confirm.map_or(0, |c| c.answered);
        let actionable = hits >= limits.ban_min;
        let count = format!(
            "{hits} of {answered} detector(s) flagged it, {} required -> {}",
            limits.ban_min,
            if actionable {
                "actionable"
            } else {
                "NOT actionable"
            }
        );

        if let Some(confirm) = confirm.filter(|c| c.answered > 0) {
            let confirmed = confirm.hits >= limits.confirm_min;
            /* confirm.hits, NOT a literal 0: "disputed" only means the hits fell short of the
            configurable confirm_min, and this is the justification a moderator reads behind
            an automatic ban. Understating the evidence is the wrong way to be wrong. */
            let verdict = if confirmed {
                format!(
                    "confirmation agreed ({}/{})",
                    confirm.hits, confirm.answered
                )
            } else {
                format!(
                    "confirmation disputed it ({}/{}, {} required)",
                    confirm.hits, confirm.answered, limits.confirm_min
                )
            };
            return VpnDecision::new(
                true,
                Some(confirmed),
                actionable,
                format!("{verdict}; {count}"),
            );
        }

        // Nothing could confirm: that only removes a voice from the count.
        let why = if configured_confirmers > 0 {
            "all confirmers failed"
        } else {
            "none configured"
        };
        VpnDecision::new(
            true,
            None,
            actionable,
            format!("no confirmation available ({why}); {count}"),
        )
    }

    /// Whether this configuration could ever reach the ban threshold. Worth saying at
    /// startup: one detector with a ban threshold of two never bans anybody, and looks
    /// identical to one that never sees a VPN. Optimistic: it counts detectors, not quotas.
    #[must_use]
    pub fn can_ever_auto_ban(
        configured_screeners: u32,
        configured_confirmers: u32,
        thresholds: VpnThresholds,
    ) -> bool {
        configured_screeners + configured_confirmers >= thresholds.sanitised().ban_min
    }
}
