//! The reputation providers (C# `Vpn/Detectors.cs`). Tier 1 screens every address; tier 2
//! (IPQS) confirms only what tier 1 flagged, because its quota is the scarcest.
//!
//! EVERY FAILURE HAS A REASON: a rejected key, a spent quota, an HTML page and a body with no
//! verdict are four different fixes, and `/vpncheck` has to be able to say which.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use futures_util::future::BoxFuture;
use pavlov_core::text::number::fixed;
use pavlov_core::vpn::{AsnValue, DetectorReading, VpnKeys, VpnMerge, sentinel};
use serde_json::Value;

use super::json_client::{FetchOptions, JsonFetch, ResilientJsonClient, json};
use crate::http::escape_data_string;

/// A reading, or why there is none.
#[derive(Debug, Clone, PartialEq)]
pub struct DetectorOutcome {
    pub reading: Option<DetectorReading>,
    pub failure: Option<String>,
}

impl DetectorOutcome {
    #[must_use]
    pub fn answered(reading: DetectorReading) -> Self {
        Self {
            reading: Some(reading),
            failure: None,
        }
    }

    #[must_use]
    pub fn none(why: impl Into<String>) -> Self {
        Self {
            reading: None,
            failure: Some(why.into()),
        }
    }

    /// The request worked; the body had nothing usable. What a spent keyless quota looks like.
    #[must_use]
    pub fn unusable(what: &str) -> Self {
        Self::none(format!("answered, but {what}"))
    }
}

/// One provider.
pub trait VpnDetector: Send + Sync {
    fn name(&self) -> &'static str;
    fn tier(&self) -> i32;
    /// Configured: keyed providers are off without a key.
    fn enabled(&self) -> bool;
    fn lookup<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, DetectorOutcome>;
}

fn asn(text: Option<String>) -> Option<String> {
    VpnMerge::normalise_asn(text.as_deref().map(AsnValue::Text))
}

fn reading(name: &str, tier: i32, flagged: bool, detail: String) -> DetectorReading {
    DetectorReading {
        name: name.to_owned(),
        tier,
        flagged,
        detail: Some(detail),
        ..DetectorReading::default()
    }
}

fn key(secret: Option<&pavlov_core::security::Secret>) -> Option<&str> {
    secret
        .map(pavlov_core::security::Secret::expose)
        .filter(|k| !k.is_empty())
}

/// The document, or why there is none.
fn document(fetch: JsonFetch) -> Result<Value, String> {
    fetch
        .document
        .ok_or_else(|| fetch.failure.unwrap_or_else(|| "no verdict".into()))
}

macro_rules! detector_common {
    () => {
        fn options<'a>(&'a self, headers: Vec<(&'a str, String)>) -> FetchOptions<'a> {
            FetchOptions {
                headers,
                secrets: self.keys.secrets(),
                ..FetchOptions::default()
            }
        }
    };
}

/// IPHub: `block` 0 residential, 1 non-residential, 2 unknown.
#[derive(Debug, Clone)]
pub struct IpHubDetector {
    client: Arc<ResilientJsonClient>,
    keys: VpnKeys,
}

impl IpHubDetector {
    #[must_use]
    pub fn new(client: Arc<ResilientJsonClient>, keys: VpnKeys) -> Self {
        Self { client, keys }
    }
    detector_common!();

    async fn run(&self, ip: &str) -> DetectorOutcome {
        let url = format!("https://v2.api.iphub.info/ip/{}", escape_data_string(ip));
        let headers = vec![(
            "X-Key",
            key(self.keys.ip_hub.as_ref())
                .unwrap_or_default()
                .to_owned(),
        )];
        let root = match document(
            self.client
                .fetch(&url, self.name(), self.options(headers))
                .await,
        ) {
            Ok(root) => root,
            Err(why) => return DetectorOutcome::none(why),
        };
        let root = Some(&root);
        let Some(block) = json::int(root, "block") else {
            return DetectorOutcome::unusable("the response had no `block` field");
        };
        DetectorOutcome::answered(DetectorReading {
            ip_hub_block: Some(block),
            // Block 2 is UNKNOWN and stays None in both directions.
            hosting: match block {
                1 => Some(true),
                0 => Some(false),
                _ => None,
            },
            residential: match block {
                0 => Some(true),
                1 => Some(false),
                _ => None,
            },
            isp: json::text(root, "isp"),
            organization: json::text(root, "isp"),
            asn: asn(json::text(root, "asn")),
            country: json::text(root, "countryName"),
            country_code: json::text(root, "countryCode"),
            ..reading(
                self.name(),
                self.tier(),
                block == 1,
                format!("block:{block}"),
            )
        })
    }
}

impl VpnDetector for IpHubDetector {
    fn name(&self) -> &'static str {
        "iphub"
    }
    fn tier(&self) -> i32 {
        1
    }
    fn enabled(&self) -> bool {
        key(self.keys.ip_hub.as_ref()).is_some()
    }
    fn lookup<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, DetectorOutcome> {
        Box::pin(self.run(ip))
    }
}

/// ipapi.is: works keyless.
#[derive(Debug, Clone)]
pub struct IpapiIsDetector {
    client: Arc<ResilientJsonClient>,
    keys: VpnKeys,
}

impl IpapiIsDetector {
    #[must_use]
    pub fn new(client: Arc<ResilientJsonClient>, keys: VpnKeys) -> Self {
        Self { client, keys }
    }
    detector_common!();

    async fn run(&self, ip: &str) -> DetectorOutcome {
        let key =
            key(self.keys.ipapi_is.as_ref()).map_or_else(String::new, |k| format!("&key={k}"));
        let url = format!("https://api.ipapi.is/?q={}{key}", escape_data_string(ip));
        let headers = vec![("Accept", "application/json".to_owned())];
        let root = match document(
            self.client
                .fetch(&url, self.name(), self.options(headers))
                .await,
        ) {
            Ok(root) => root,
            Err(why) => return DetectorOutcome::none(why),
        };
        let root = Some(&root);
        if json::boolean(root, "is_vpn").is_none() {
            return DetectorOutcome::unusable(
                "the response had no `is_vpn` field - usually the free quota being spent",
            );
        }
        let location = json::member(root, "location");
        let asn_block = json::member(root, "asn");
        let company = json::member(root, "company");

        /* Anonymising signals only. is_datacenter alone is NOT a hit - plenty of legitimate
        players sit behind carrier and cloud ranges - but it is reported for the merge. */
        let hits: Vec<&str> = [
            ("vpn", "is_vpn"),
            ("proxy", "is_proxy"),
            ("tor", "is_tor"),
            ("abuser", "is_abuser"),
        ]
        .into_iter()
        .filter(|(_, field)| json::boolean(root, field) == Some(true))
        .map(|(name, _)| name)
        .collect();
        let datacenter = json::boolean(root, "is_datacenter");
        let company_type = json::text(company, "type");
        let detail = format!(
            "{}{}",
            if hits.is_empty() {
                "clean".to_owned()
            } else {
                hits.join("+")
            },
            if datacenter == Some(true) {
                " (datacenter)"
            } else {
                ""
            }
        );
        let org = json::text(asn_block, "org").or_else(|| json::text(company, "name"));
        DetectorOutcome::answered(DetectorReading {
            vpn: json::boolean(root, "is_vpn"),
            proxy: json::boolean(root, "is_proxy"),
            tor: json::boolean(root, "is_tor"),
            abuser: json::boolean(root, "is_abuser"),
            hosting: datacenter,
            mobile: json::boolean(root, "is_mobile"),
            residential: company_type.map(|t| t == "isp"),
            provider: json::text(company, "name"),
            asn: asn(json::text(asn_block, "asn")),
            organization: org.clone(),
            isp: org,
            country: json::text(location, "country"),
            country_code: json::text(location, "country_code"),
            region: json::text(location, "state").or_else(|| json::text(location, "region")),
            city: json::text(location, "city"),
            ..reading(self.name(), self.tier(), !hits.is_empty(), detail)
        })
    }
}

impl VpnDetector for IpapiIsDetector {
    fn name(&self) -> &'static str {
        "ipapi.is"
    }
    fn tier(&self) -> i32 {
        1
    }
    fn enabled(&self) -> bool {
        true
    }
    fn lookup<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, DetectorOutcome> {
        Box::pin(self.run(ip))
    }
}

/// proxycheck.io: works keyless (100 a day).
#[derive(Debug, Clone)]
pub struct ProxyCheckDetector {
    client: Arc<ResilientJsonClient>,
    keys: VpnKeys,
}

impl ProxyCheckDetector {
    #[must_use]
    pub fn new(client: Arc<ResilientJsonClient>, keys: VpnKeys) -> Self {
        Self { client, keys }
    }
    detector_common!();

    async fn run(&self, ip: &str) -> DetectorOutcome {
        let key =
            key(self.keys.proxy_check.as_ref()).map_or_else(String::new, |k| format!("&key={k}"));
        let url = format!(
            "https://proxycheck.io/v2/{}?vpn=1&risk=1&asn=1{key}",
            escape_data_string(ip)
        );
        let headers = vec![("Accept", "application/json".to_owned())];
        let root = match document(
            self.client
                .fetch(&url, self.name(), self.options(headers))
                .await,
        ) {
            Ok(root) => root,
            Err(why) => return DetectorOutcome::none(why),
        };
        let root = Some(&root);
        let status = json::text(root, "status");
        if !matches!(status.as_deref(), Some("ok" | "warning")) {
            return DetectorOutcome::unusable(&format!(
                "status was `{}` - usually the keyless 100/day quota being spent",
                status.as_deref().unwrap_or("missing")
            ));
        }
        let Some(record) = json::member(root, ip) else {
            return DetectorOutcome::unusable(&format!("the response carried no record for {ip}"));
        };
        let record = Some(record);
        // VPN | Business | Residential | Public Proxy | Compromised Server | ...
        let kind = json::text(record, "type")
            .unwrap_or_default()
            .to_lowercase();
        let proxy = json::text(record, "proxy")
            .unwrap_or_default()
            .to_lowercase();
        let risk = json::number(record, "risk");
        let operator = json::member(record, "operator").and_then(|op| json::text(Some(op), "name"));
        let mut detail = format!("proxy:{proxy}");
        if !kind.is_empty() {
            detail.push_str(&format!(" type:{kind}"));
        }
        if let Some(r) = risk {
            detail.push_str(&format!(" risk:{}", fixed(r, 0)));
        }
        DetectorOutcome::answered(DetectorReading {
            // Only what the type actually says; an unrecognised type stays unknown.
            vpn: (kind == "vpn").then_some(true),
            tor: kind.contains("tor").then_some(true),
            hosting: (kind == "business" || kind.contains("hosting") || kind.contains("server"))
                .then_some(true),
            residential: (kind == "residential").then_some(true),
            threat_score: risk,
            // operator.name is the consumer brand; provider is the underlying network.
            provider: operator.or_else(|| json::text(record, "provider")),
            asn: asn(json::text(record, "asn")),
            organization: json::text(record, "organisation")
                .or_else(|| json::text(record, "provider")),
            isp: json::text(record, "provider").or_else(|| json::text(record, "organisation")),
            country: json::text(record, "country"),
            country_code: json::text(record, "isocode"),
            region: json::text(record, "region"),
            city: json::text(record, "city"),
            ..reading(self.name(), self.tier(), proxy == "yes", detail)
        })
    }
}

impl VpnDetector for ProxyCheckDetector {
    fn name(&self) -> &'static str {
        "proxycheck"
    }
    fn tier(&self) -> i32 {
        1
    }
    fn enabled(&self) -> bool {
        true
    }
    fn lookup<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, DetectorOutcome> {
        Box::pin(self.run(ip))
    }
}

/// Sentinel: its body shape has moved more than once, so an unrecognised one is logged once,
/// loudly, with the values.
#[derive(Debug)]
pub struct SentinelDetector {
    client: Arc<ResilientJsonClient>,
    keys: VpnKeys,
    shape_warned: AtomicBool,
}

impl SentinelDetector {
    #[must_use]
    pub fn new(client: Arc<ResilientJsonClient>, keys: VpnKeys) -> Self {
        Self {
            client,
            keys,
            shape_warned: AtomicBool::new(false),
        }
    }
    detector_common!();

    async fn run(&self, ip: &str) -> DetectorOutcome {
        let url = format!("https://sntlhq.com/v1/lookup/{}", escape_data_string(ip));
        let headers = vec![
            (
                "Authorization",
                format!(
                    "Bearer {}",
                    key(self.keys.sentinel.as_ref()).unwrap_or_default()
                ),
            ),
            ("Accept", "application/json".to_owned()),
        ];
        let root = match document(
            self.client
                .fetch(&url, self.name(), self.options(headers))
                .await,
        ) {
            Ok(root) => root,
            Err(why) => return DetectorOutcome::none(why),
        };
        match sentinel::parse(&root) {
            Ok(reading) => DetectorOutcome::answered(reading),
            Err(unrecognised) => {
                if !self.shape_warned.swap(true, Ordering::Relaxed) {
                    tracing::warn!(detail = %unrecognised, "sentinel body not recognised");
                }
                DetectorOutcome::unusable(&unrecognised)
            }
        }
    }
}

impl VpnDetector for SentinelDetector {
    fn name(&self) -> &'static str {
        "sentinel"
    }
    fn tier(&self) -> i32 {
        1
    }
    fn enabled(&self) -> bool {
        key(self.keys.sentinel.as_ref()).is_some()
    }
    fn lookup<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, DetectorOutcome> {
        Box::pin(self.run(ip))
    }
}

/// IPQualityScore: the confirming tier.
#[derive(Debug, Clone)]
pub struct IpqsDetector {
    client: Arc<ResilientJsonClient>,
    keys: VpnKeys,
}

impl IpqsDetector {
    #[must_use]
    pub fn new(client: Arc<ResilientJsonClient>, keys: VpnKeys) -> Self {
        Self { client, keys }
    }
    detector_common!();

    async fn run(&self, ip: &str) -> DetectorOutcome {
        let url = format!(
            "https://www.ipqualityscore.com/api/json/ip/{}/{}?strictness=1",
            key(self.keys.ipqs.as_ref()).unwrap_or_default(),
            escape_data_string(ip)
        );
        let root = match document(
            self.client
                .fetch(&url, self.name(), self.options(Vec::new()))
                .await,
        ) {
            Ok(root) => root,
            Err(why) => return DetectorOutcome::none(why),
        };
        let root = Some(&root);
        if json::boolean(root, "success") != Some(true) {
            return DetectorOutcome::unusable(&json::text(root, "message").unwrap_or_else(|| {
                "`success` was not true - usually an invalid key or a spent quota".into()
            }));
        }
        let vpn = json::boolean(root, "vpn").unwrap_or(false);
        let proxy = json::boolean(root, "proxy").unwrap_or(false);
        let tor = json::boolean(root, "tor").unwrap_or(false);
        let fraud = json::number(root, "fraud_score");
        /* connection_type is the most direct residential/mobile/datacenter signal anybody
        gives, trusted only when present. NEVER `host`: that is the address's hostname, truthy
        on nearly every address, and would mark the whole player base as datacenter. */
        let connection = json::text(root, "connection_type")
            .unwrap_or_default()
            .to_lowercase();
        let has_type = !connection.is_empty();
        DetectorOutcome::answered(DetectorReading {
            vpn: Some(vpn),
            proxy: Some(proxy),
            tor: Some(tor),
            // With a type, an explicit false: "not mobile" is not "nobody knows".
            hosting: has_type.then(|| connection.contains("data center")),
            residential: has_type.then(|| connection.contains("residential")),
            mobile: if has_type {
                Some(json::boolean(root, "mobile") == Some(true) || connection.contains("mobile"))
            } else {
                json::boolean(root, "mobile")
            },
            threat_score: fraud,
            asn: asn(json::text(root, "ASN")),
            organization: json::text(root, "organization").or_else(|| json::text(root, "ISP")),
            isp: json::text(root, "ISP"),
            country: json::text(root, "country_code"),
            country_code: json::text(root, "country_code"),
            region: json::text(root, "region"),
            city: json::text(root, "city"),
            ..reading(
                self.name(),
                self.tier(),
                vpn || proxy || tor,
                format!(
                    "vpn:{vpn} proxy:{proxy} tor:{tor} fraud:{}",
                    fraud.map_or_else(|| "?".to_owned(), |f| fixed(f, 0))
                ),
            )
        })
    }
}

impl VpnDetector for IpqsDetector {
    fn name(&self) -> &'static str {
        "ipqs"
    }
    fn tier(&self) -> i32 {
        2
    }
    fn enabled(&self) -> bool {
        key(self.keys.ipqs.as_ref()).is_some()
    }
    fn lookup<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, DetectorOutcome> {
        Box::pin(self.run(ip))
    }
}

/// Every provider, in the C# registration order.
#[must_use]
pub fn all(client: &Arc<ResilientJsonClient>, keys: &VpnKeys) -> Vec<Arc<dyn VpnDetector>> {
    vec![
        Arc::new(IpHubDetector::new(Arc::clone(client), keys.clone())),
        Arc::new(IpapiIsDetector::new(Arc::clone(client), keys.clone())),
        Arc::new(ProxyCheckDetector::new(Arc::clone(client), keys.clone())),
        Arc::new(SentinelDetector::new(Arc::clone(client), keys.clone())),
        Arc::new(IpqsDetector::new(Arc::clone(client), keys.clone())),
    ]
}
