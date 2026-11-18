//! Where an address is (C# `IGeoLocator`, `IpApiGeoLocator`, `GeoapifyGeoLocator`,
//! `GeoLocatorChain` in `Vpn/VpnScreeningService.cs`).
//!
//! ORDER IS POLICY: ip-api supplies the ISP and ASN the evasion scorer matches against banned
//! players' networks, and Geoapify supplies neither, so Geoapify is only the fallback.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::security::Secret;
use pavlov_core::vpn::{AsnValue, GeoLocation, VpnMerge};
use serde_json::Value;

use super::json_client::{FetchOptions, ResilientJsonClient, json};
use crate::http::escape_data_string;

/// A provider. `Err` is the provider failing (the chain moves on); `Ok(None)` is no answer.
pub trait GeoLocator: Send + Sync {
    fn locate<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, Result<Option<GeoLocation>, String>>;
}

/// ip-api.com: keyless, plain HTTP on the free tier.
#[derive(Debug, Clone)]
pub struct IpApiGeoLocator {
    client: Arc<ResilientJsonClient>,
}

impl IpApiGeoLocator {
    #[must_use]
    pub fn new(client: Arc<ResilientJsonClient>) -> Self {
        Self { client }
    }

    async fn run(&self, ip: &str) -> Option<GeoLocation> {
        const FIELDS: &str =
            "status,country,countryCode,region,regionName,city,zip,lat,lon,timezone,isp,org,as";
        let url = format!(
            "http://ip-api.com/json/{}?fields={FIELDS}",
            escape_data_string(ip)
        );
        let root = self
            .client
            .get(&url, "ip-api", FetchOptions::default())
            .await?;
        let root = Some(&root);
        if json::text(root, "status").as_deref() != Some("success") {
            return None;
        }
        // `as` is "AS15169 Google LLC": the ASN is the first token.
        let asn =
            json::text(root, "as").map(|a| a.split(' ').next().unwrap_or_default().to_owned());
        Some(GeoLocation {
            city: json::text(root, "city"),
            region: json::text(root, "regionName").or_else(|| json::text(root, "region")),
            country: json::text(root, "country"),
            country_code: json::text(root, "countryCode"),
            zip: json::text(root, "zip"),
            isp: json::text(root, "isp"),
            organization: json::text(root, "org"),
            asn: VpnMerge::normalise_asn(asn.as_deref().map(AsnValue::Text)),
            timezone: json::text(root, "timezone"),
            latitude: json::number(root, "lat"),
            longitude: json::number(root, "lon"),
        })
    }
}

impl GeoLocator for IpApiGeoLocator {
    fn locate<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, Result<Option<GeoLocation>, String>> {
        Box::pin(async move { Ok(self.run(ip).await) })
    }
}

/// Geoapify's IP lookup. Its key travels in the query string, so it is scrubbed from
/// everything the client logs.
#[derive(Debug, Clone)]
pub struct GeoapifyGeoLocator {
    client: Arc<ResilientJsonClient>,
    api_key: Secret,
}

/// A field that is `{ "<field>": ... }` or, from a flattened response, a bare string.
fn named(root: Option<&Value>, property: &str, field: &str) -> Option<String> {
    match json::member(root, property)? {
        member @ Value::Object(_) => json::text(Some(member), field),
        Value::String(s) => Some(s.clone()),
        _ => None,
    }
}

impl GeoapifyGeoLocator {
    #[must_use]
    pub fn new(client: Arc<ResilientJsonClient>, api_key: Secret) -> Self {
        Self { client, api_key }
    }

    async fn run(&self, ip: &str) -> Option<GeoLocation> {
        let key = self.api_key.expose();
        let url = format!(
            "https://api.geoapify.com/v1/ipinfo?ip={}&apiKey={}",
            escape_data_string(ip),
            escape_data_string(key)
        );
        let options = FetchOptions {
            secrets: vec![key],
            ..FetchOptions::default()
        };
        let root = self.client.get(&url, "geoapify", options).await?;
        if !root.is_object() {
            return None;
        }
        let root = Some(&root);
        let location = json::member(root, "location");
        // `state` and `subdivisions[0]` are one thing by two names, depending on the country.
        let region = named(root, "state", "name")
            .or_else(|| named(root, "region", "name"))
            .or_else(|| {
                json::member(root, "subdivisions")?
                    .as_array()?
                    .iter()
                    .find_map(|entry| json::text(Some(entry), "name"))
            });
        let result = GeoLocation {
            city: named(root, "city", "name"),
            region,
            country: named(root, "country", "name"),
            country_code: named(root, "country", "iso_code"),
            zip: json::text(root, "postcode"),
            timezone: named(root, "timezone", "name"),
            latitude: location.and_then(|l| json::number(Some(l), "latitude")),
            longitude: location.and_then(|l| json::number(Some(l), "longitude")),
            ..GeoLocation::default()
        };
        /* An object with no usable field is NOT an answer: it would satisfy the chain's
        "first answer wins" and stop ip-api ever being tried. */
        (result.country.is_some() || result.city.is_some() || result.country_code.is_some())
            .then_some(result)
    }
}

impl GeoLocator for GeoapifyGeoLocator {
    fn locate<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, Result<Option<GeoLocation>, String>> {
        Box::pin(async move { Ok(self.run(ip).await) })
    }
}

/// The first provider with an answer wins; the rest are not asked.
pub struct GeoLocatorChain {
    providers: Vec<(String, Arc<dyn GeoLocator>)>,
}

impl std::fmt::Debug for GeoLocatorChain {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let names: Vec<&str> = self.providers.iter().map(|(n, _)| n.as_str()).collect();
        f.debug_struct("GeoLocatorChain")
            .field("providers", &names)
            .finish()
    }
}

impl GeoLocatorChain {
    /// Providers in order, each with a name for the log.
    #[must_use]
    pub fn new(providers: Vec<(String, Arc<dyn GeoLocator>)>) -> Self {
        Self { providers }
    }
}

impl GeoLocator for GeoLocatorChain {
    /// Cancellation is the caller dropping this future: the chain stops where it is and no
    /// later provider is asked, rather than working through all of them during shutdown.
    fn locate<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, Result<Option<GeoLocation>, String>> {
        Box::pin(async move {
            for (name, provider) in &self.providers {
                match provider.locate(ip).await {
                    Ok(Some(located)) => return Ok(Some(located)),
                    Ok(None) => {}
                    Err(error) => {
                        tracing::warn!(provider = %name, %error, "geolocation provider failed - trying the next one")
                    }
                }
            }
            Ok(None)
        })
    }
}
