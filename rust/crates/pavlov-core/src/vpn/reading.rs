//! What the detectors said (C# `Core/Vpn/DetectorReading.cs`).

use serde::{Deserialize, Serialize};

/// What one detector said about one address.
///
/// EVERY OPTIONAL HERE MEANS "DID NOT ANSWER", never "no". Conflating the two was the most
/// productive source of bugs this subsystem had: a detector that was down read as clean, an
/// exhausted quota read as innocence, and a body encoding booleans as 0/1 read as no verdict.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DetectorReading {
    pub name: String,
    pub tier: i32,
    /// Whether this detector considers the address anonymising. A reading exists only when
    /// the detector answered, and "answered without an opinion" is "not flagged".
    pub flagged: bool,
    /// Short human summary, e.g. "vpn+tor risk:82", for the audit trail.
    pub detail: Option<String>,
    pub vpn: Option<bool>,
    pub proxy: Option<bool>,
    pub tor: Option<bool>,
    pub relay: Option<bool>,
    pub abuser: Option<bool>,
    /// Datacenter / non-residential. NOT a hit on its own: honest players sit behind carrier
    /// and cloud ranges.
    pub hosting: Option<bool>,
    pub residential: Option<bool>,
    pub mobile: Option<bool>,
    /// 0-100 risk, where the provider publishes one.
    pub threat_score: Option<f64>,
    /// The CONSUMER BRAND ("NordVPN"), not the underlying network.
    pub provider: Option<String>,
    pub asn: Option<String>,
    pub organization: Option<String>,
    pub isp: Option<String>,
    pub country: Option<String>,
    pub country_code: Option<String>,
    pub region: Option<String>,
    pub city: Option<String>,
    /// IPHub's raw block value, kept for stored records.
    pub ip_hub_block: Option<i32>,
}

/// One tier's outcome.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct TierOutcome {
    /// Detectors that answered AND flagged.
    pub hits: u32,
    /// Detectors that returned a verdict - kept apart from the count run, so a total outage
    /// is distinguishable from a unanimous all-clear.
    pub answered: u32,
    /// Detectors that errored, timed out or were rate limited.
    pub failed: u32,
    pub readings: Vec<DetectorReading>,
}

impl TierOutcome {
    /// A tier from the counts alone, with no readings.
    #[must_use]
    pub fn counts(hits: u32, answered: u32, failed: u32) -> Self {
        Self {
            hits,
            answered,
            failed,
            readings: Vec::new(),
        }
    }

    /// A tier from each detector's result; `None` is a detector that did not answer.
    #[must_use]
    pub fn from_results(results: impl IntoIterator<Item = Option<DetectorReading>>) -> Self {
        let mut attempted = 0_u32;
        let readings: Vec<DetectorReading> = results
            .into_iter()
            .inspect(|_| attempted += 1)
            .flatten()
            .collect();
        let answered = u32::try_from(readings.len()).unwrap_or(u32::MAX);
        Self {
            hits: u32::try_from(readings.iter().filter(|r| r.flagged).count()).unwrap_or(u32::MAX),
            answered,
            failed: attempted.saturating_sub(answered),
            readings,
        }
    }
}

/// The verdict, and separately the permission to ban.
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct VpnDecision {
    /// The regular checks reached the screening threshold.
    pub flagged: bool,
    /// The CONFIRMATION verdict, for display: agreed, disputed, or `None` inconclusive.
    pub confirmed: Option<bool>,
    /// The separate, explicit "may we auto-ban" decision.
    pub actionable: bool,
    /// Why, in words that go straight into the log.
    pub reason: String,
}

impl VpnDecision {
    #[must_use]
    pub fn new(
        flagged: bool,
        confirmed: Option<bool>,
        actionable: bool,
        reason: impl Into<String>,
    ) -> Self {
        Self {
            flagged,
            confirmed,
            actionable,
            reason: reason.into(),
        }
    }

    /// The verdict word shown to staff.
    #[must_use]
    pub fn label(&self) -> &'static str {
        match (self.flagged, self.confirmed) {
            (false, _) => "CLEAN",
            (true, Some(true)) => "CONFIRMED",
            (true, Some(false)) => "DISPUTED",
            (true, None) => "FLAGGED (inconclusive)",
        }
    }
}

/// Written with its `label`, as the C# writes every public property, so a stored record keeps
/// its shape.
impl Serialize for VpnDecision {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        use serde::ser::SerializeStruct;
        let mut s = serializer.serialize_struct("VpnDecision", 5)?;
        s.serialize_field("flagged", &self.flagged)?;
        s.serialize_field("confirmed", &self.confirmed)?;
        s.serialize_field("actionable", &self.actionable)?;
        s.serialize_field("reason", &self.reason)?;
        s.serialize_field("label", self.label())?;
        s.end()
    }
}
