//! One address's merged intelligence (C# `Core/Vpn/VpnConsensus.cs`: `VpnRecord`, `VpnMerge`,
//! `GeoLocation`).

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use super::{DetectorReading, TierOutcome, VpnDecision};
use crate::json::epoch_ms;

/// The cached record for one address. Cached effectively forever, so its shape is a stored
/// contract: camelCase, timestamps in epoch milliseconds.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct VpnRecord {
    pub ip: String,
    pub decision: VpnDecision,
    pub vpn: Option<bool>,
    pub proxy: Option<bool>,
    pub tor: Option<bool>,
    pub hosting: Option<bool>,
    pub residential: Option<bool>,
    pub mobile: Option<bool>,
    pub threat_score: Option<f64>,
    pub provider: Option<String>,
    pub asn: Option<String>,
    pub organization: Option<String>,
    pub isp: Option<String>,
    pub country: Option<String>,
    pub country_code: Option<String>,
    pub region: Option<String>,
    pub city: Option<String>,
    /// Approximate coordinates from the geolocator. NOT part of [`VpnMerge::SCHEMA`]: bumping
    /// the schema for a map would re-screen every known address and spend a day's quota on
    /// something cosmetic. City-level at best - never where somebody lives.
    pub latitude: Option<f64>,
    pub longitude: Option<f64>,
    pub screen_hits: u32,
    pub screen_answered: u32,
    pub confirm_hits: u32,
    pub confirm_answered: u32,
    /// Which providers contributed, for the audit trail.
    pub sources: Vec<String>,
    pub detectors: Vec<DetectorReading>,
    #[serde(with = "epoch_ms")]
    pub checked_at: DateTime<Utc>,
    /// Bumped whenever the merge changes shape or a detector is added: a cache written by an
    /// older merge is otherwise never re-screened by the new one.
    pub schema: u32,
    /// A private or unroutable address, which no provider can check.
    pub local: bool,
}

impl Default for VpnRecord {
    fn default() -> Self {
        Self {
            ip: String::new(),
            decision: VpnDecision::default(),
            vpn: None,
            proxy: None,
            tor: None,
            hosting: None,
            residential: None,
            mobile: None,
            threat_score: None,
            provider: None,
            asn: None,
            organization: None,
            isp: None,
            country: None,
            country_code: None,
            region: None,
            city: None,
            latitude: None,
            longitude: None,
            screen_hits: 0,
            screen_answered: 0,
            confirm_hits: 0,
            confirm_answered: 0,
            sources: Vec::new(),
            detectors: Vec::new(),
            checked_at: DateTime::UNIX_EPOCH,
            schema: VpnMerge::SCHEMA,
            local: false,
        }
    }
}

/// Whois geolocation: city-level, and it beats a detector's country.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct GeoLocation {
    pub city: Option<String>,
    pub region: Option<String>,
    pub country: Option<String>,
    pub country_code: Option<String>,
    pub zip: Option<String>,
    /// Who provides the connection, as whois reports it.
    pub isp: Option<String>,
    pub organization: Option<String>,
    pub asn: Option<String>,
    pub timezone: Option<String>,
    pub latitude: Option<f64>,
    pub longitude: Option<f64>,
}

fn present(value: &Option<String>) -> Option<&str> {
    value.as_deref().filter(|v| !v.is_empty())
}

impl GeoLocation {
    /// "Toronto, Ontario, Canada M5V  -  Bell Canada  -  TZ America/Toronto".
    #[must_use]
    pub fn full_description(&self) -> Option<String> {
        let mut place = [&self.city, &self.region, &self.country]
            .into_iter()
            .filter_map(present)
            .collect::<Vec<_>>()
            .join(", ");
        if let Some(zip) = present(&self.zip) {
            place = format!("{place} {zip}").trim().to_owned();
        }
        let timezone = present(&self.timezone).map(|tz| format!("TZ {tz}"));
        let parts: Vec<String> = [
            if place.is_empty() {
                present(&self.country).map(str::to_owned)
            } else {
                Some(place)
            },
            present(&self.isp).map(str::to_owned),
            timezone,
        ]
        .into_iter()
        .flatten()
        .filter(|p| !p.is_empty())
        .collect();
        (!parts.is_empty()).then(|| parts.join("  -  "))
    }
}

/// How an ASN arrived: providers send it as text or as a number.
#[derive(Debug, Clone, Copy)]
pub enum AsnValue<'a> {
    Text(&'a str),
    Integer(i64),
    Real(f64),
}

/// Merging disagreeing providers into one record, choosing a strategy per field:
/// - ANY-TRUE for anonymising signals: one provider knowing an address is Tor is enough;
/// - PREFERENCE ORDER for classifications, so a direct classifier outranks a coarse proxy;
/// - FIRST NON-NULL for descriptive fields.
#[derive(Debug)]
pub struct VpnMerge;

impl VpnMerge {
    /// See [`VpnRecord::schema`]. Bump when the merge changes.
    pub const SCHEMA: u32 = 3;

    /// IPQS's connection type and ipapi.is's datacenter flag are direct classifications;
    /// IPHub's block==1 only means "non-residential", so it is the last resort.
    const HOSTING_PREFERENCE: [&str; 5] = ["ipqs", "ipapi.is", "sentinel", "proxycheck", "iphub"];
    /// Sentinel is LAST: it only ever asserts residential=false, so ahead of a real classifier
    /// its one-sided answer would decide a question it cannot answer.
    const RESIDENTIAL_PREFERENCE: [&str; 5] =
        ["ipqs", "proxycheck", "ipapi.is", "iphub", "sentinel"];
    const THREAT_SCORE_PREFERENCE: [&str; 3] = ["ipqs", "sentinel", "proxycheck"];
    const ORGANIZATION_PREFERENCE: [&str; 4] = ["ipqs", "proxycheck", "ipapi.is", "sentinel"];

    /// Merge both tiers' readings, with whois geolocation over a detector's.
    #[must_use]
    pub fn merge(
        ip: &str,
        decision: VpnDecision,
        screen: Option<&TierOutcome>,
        confirm: Option<&TierOutcome>,
        geo: Option<&GeoLocation>,
        checked_at: DateTime<Utc>,
    ) -> VpnRecord {
        let all: Vec<DetectorReading> = screen
            .into_iter()
            .chain(confirm)
            .flat_map(|tier| tier.readings.iter().cloned())
            .collect();

        let hosting = preferring(&all, &Self::HOSTING_PREFERENCE, |r| r.hosting);
        let residential = preferring(&all, &Self::RESIDENTIAL_PREFERENCE, |r| r.residential);
        /* The consumer brand is what staff want to read, and only proxycheck's operator name
        carries it; everybody else knows the underlying network. */
        let provider = all
            .iter()
            .find(|r| r.name == "proxycheck" && r.provider.is_some())
            .and_then(|r| r.provider.clone())
            .or_else(|| first(&all, |r| r.provider.as_ref()));
        let geo_field = |f: fn(&GeoLocation) -> &Option<String>| geo.and_then(|g| f(g).clone());

        VpnRecord {
            ip: ip.to_owned(),
            // A flagged address with no explicit vpn signal is still reported as one.
            vpn: any_true(&all, |r| r.vpn).or_else(|| decision.flagged.then_some(true)),
            proxy: any_true(&all, |r| r.proxy),
            tor: any_true(&all, |r| r.tor),
            hosting,
            // A hosting address is by definition not residential.
            residential: if hosting == Some(true) {
                Some(false)
            } else {
                residential
            },
            mobile: any_true(&all, |r| r.mobile),
            threat_score: preferring(&all, &Self::THREAT_SCORE_PREFERENCE, |r| r.threat_score),
            provider,
            asn: first(&all, |r| r.asn.as_ref()).or_else(|| geo_field(|g| &g.asn)),
            organization: preferring_text(&all, &Self::ORGANIZATION_PREFERENCE, |r| {
                r.organization.as_ref()
            })
            .or_else(|| geo_field(|g| &g.organization)),
            isp: geo_field(|g| &g.isp).or_else(|| first(&all, |r| r.isp.as_ref())),
            country: geo_field(|g| &g.country).or_else(|| first(&all, |r| r.country.as_ref())),
            country_code: geo_field(|g| &g.country_code)
                .or_else(|| first(&all, |r| r.country_code.as_ref())),
            region: geo_field(|g| &g.region).or_else(|| first(&all, |r| r.region.as_ref())),
            city: geo_field(|g| &g.city).or_else(|| first(&all, |r| r.city.as_ref())),
            latitude: geo.and_then(|g| g.latitude),
            longitude: geo.and_then(|g| g.longitude),
            screen_hits: screen.map_or(0, |t| t.hits),
            screen_answered: screen.map_or(0, |t| t.answered),
            confirm_hits: confirm.map_or(0, |t| t.hits),
            confirm_answered: confirm.map_or(0, |t| t.answered),
            sources: all.iter().map(|r| r.name.clone()).collect(),
            detectors: all,
            checked_at,
            schema: Self::SCHEMA,
            local: false,
            decision,
        }
    }

    /// "AS1234", accepted with or without the prefix, as text or as a number.
    #[must_use]
    pub fn normalise_asn(raw: Option<AsnValue<'_>>) -> Option<String> {
        let text = match raw? {
            AsnValue::Text(s) => s.trim().to_owned(),
            AsnValue::Integer(i) => i.to_string(),
            AsnValue::Real(d) => (d.trunc() as i64).to_string(),
        };
        let digits = match text.get(..2) {
            Some(prefix) if prefix.eq_ignore_ascii_case("AS") => &text[2..],
            _ => text.as_str(),
        };
        (!digits.is_empty()).then(|| format!("AS{digits}"))
    }
}

/// True when any provider says true, false when any says false, `None` when nobody knows.
/// Asymmetric on purpose: the providers that have not seen an address used for anonymising
/// are not contradicting the one that has - they have no data.
fn any_true(
    readings: &[DetectorReading],
    field: impl Fn(&DetectorReading) -> Option<bool>,
) -> Option<bool> {
    let values: Vec<Option<bool>> = readings.iter().map(field).collect();
    if values.contains(&Some(true)) {
        Some(true)
    } else if values.contains(&Some(false)) {
        Some(false)
    } else {
        None
    }
}

fn first(
    readings: &[DetectorReading],
    field: impl Fn(&DetectorReading) -> Option<&String>,
) -> Option<String> {
    readings.iter().find_map(|r| field(r).cloned())
}

/// The first answer from a provider in `order`, then anybody's.
fn preferring<T: Copy>(
    readings: &[DetectorReading],
    order: &[&str],
    field: impl Fn(&DetectorReading) -> Option<T>,
) -> Option<T> {
    order
        .iter()
        .filter_map(|name| readings.iter().find(|r| r.name == *name))
        .find_map(&field)
        .or_else(|| readings.iter().find_map(&field))
}

/// [`preferring`] for text: an empty answer from a preferred provider does not count.
fn preferring_text(
    readings: &[DetectorReading],
    order: &[&str],
    field: impl Fn(&DetectorReading) -> Option<&String>,
) -> Option<String> {
    order
        .iter()
        .filter_map(|name| readings.iter().find(|r| r.name == *name))
        .find_map(|r| field(r).filter(|v| !v.is_empty()).cloned())
        .or_else(|| first(readings, field))
}
