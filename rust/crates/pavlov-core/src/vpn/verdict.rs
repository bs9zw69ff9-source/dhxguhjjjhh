//! One verdict, worded one way, for every surface that shows a screening (C#
//! `Core/Vpn/VpnVerdict.cs`).
//!
//! The same screening used to be rendered three different ways, all leading with detector
//! arithmetic. THE HEADLINE IS THE PRODUCT: it answers "is this person on a VPN" without
//! arithmetic, and the breakdown stays underneath for a moderator who has to show their
//! working. "LIKELY" IS NOT "CONFIRMED" AND NEITHER IS "CLEAN": collapsing them would make the
//! bot sound certain about something it is not, and that certainty ends up in a ban appeal.

use super::VpnRecord;

/// Where a screening landed: one of six answers and nothing in between.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VpnStance {
    /// No screening ran.
    NotChecked,
    /// A private or LAN address: nothing to look up.
    Local,
    /// Checks ran and none flagged it.
    Clean,
    /// Enough checks flagged it, and nothing could confirm either way.
    Likely,
    /// Flagged, and the confirming check DISAGREED.
    Disputed,
    /// Flagged, and the confirming check agreed.
    Confirmed,
}

/// The verdict as words.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VpnSummary {
    pub stance: VpnStance,
    /// The verdict alone, in caps. The only thing that must be read.
    pub headline: String,
    /// One sentence of ordinary English explaining the headline.
    pub plain: String,
    /// What the bot did about it, in the past tense.
    pub action: String,
}

impl VpnSummary {
    fn new(stance: VpnStance, headline: &str, plain: impl Into<String>, action: &str) -> Self {
        Self {
            stance,
            headline: headline.to_owned(),
            plain: plain.into(),
            action: action.to_owned(),
        }
    }

    /// This warrants a second look from a human.
    #[must_use]
    pub fn concerning(&self) -> bool {
        matches!(
            self.stance,
            VpnStance::Likely | VpnStance::Disputed | VpnStance::Confirmed
        )
    }
}

/// "2 of 3 checks", worded so it never reads as a fraction to be evaluated.
fn count(hits: u32, answered: u32, noun: &str) -> String {
    if answered > 0 {
        format!(
            "{hits} of {answered} {noun}{}",
            if answered == 1 { "" } else { "s" }
        )
    } else {
        format!("{hits} {noun}{}", if hits == 1 { "" } else { "s" })
    }
}

/// The verdict for a screening, or the not-checked one when there is none.
#[must_use]
pub fn of(record: Option<&VpnRecord>) -> VpnSummary {
    let Some(record) = record else {
        return VpnSummary::new(
            VpnStance::NotChecked,
            "NOT CHECKED",
            "No VPN screening ran for this address.",
            "No action.",
        );
    };

    if record.local {
        return VpnSummary::new(
            VpnStance::Local,
            "LOCAL ADDRESS",
            "This is a private or LAN address, so there is nothing to look up.",
            "No action.",
        );
    }

    let decision = &record.decision;
    let screens = count(record.screen_hits, record.screen_answered, "check");

    if !decision.flagged {
        let plain = if record.screen_answered > 0 {
            format!(
                "All {} check{} agree this is a normal connection.",
                record.screen_answered,
                if record.screen_answered == 1 { "" } else { "s" }
            )
        } else {
            "Nothing flagged this address.".to_owned()
        };
        return VpnSummary::new(
            VpnStance::Clean,
            "CLEAN",
            plain,
            "No action — nothing to act on.",
        );
    }

    let action = if decision.actionable {
        "AUTO-BANNED."
    } else {
        "Not banned — flagged, but below the threshold to act."
    };

    match decision.confirmed {
        Some(true) => VpnSummary::new(
            VpnStance::Confirmed,
            "VPN / PROXY — CONFIRMED",
            format!("{screens} flagged this address, and the confirming check agreed."),
            action,
        ),
        /* DISPUTED IS STILL A BAN WHEN IT IS ACTIONABLE: two screeners outvote the confirmer,
        and a card reading "not banned" beside a player just banned reads as a bug. */
        Some(false) => VpnSummary::new(
            VpnStance::Disputed,
            "VPN / PROXY — DISPUTED",
            format!("{screens} flagged this address, but the confirming check cleared it."),
            action,
        ),
        None => VpnSummary::new(
            VpnStance::Likely,
            "VPN / PROXY — LIKELY",
            format!(
                "{screens} flagged this address. {}",
                if record.confirm_answered > 0 {
                    "The confirming check gave no verdict, so this is unconfirmed."
                } else {
                    "No confirming check is configured, so this is unconfirmed."
                }
            ),
            action,
        ),
    }
}

/// The anonymising signals, named rather than counted: "VPN, datacenter". Only what a
/// detector positively asserted - a missing answer is never "no".
#[must_use]
pub fn signals(record: Option<&VpnRecord>) -> Vec<&'static str> {
    let Some(record) = record else {
        return Vec::new();
    };
    [
        (record.vpn, "VPN"),
        (record.proxy, "proxy"),
        (record.tor, "Tor"),
        (record.hosting, "datacenter"),
    ]
    .into_iter()
    .filter(|(value, _)| *value == Some(true))
    .map(|(_, name)| name)
    .collect()
}
