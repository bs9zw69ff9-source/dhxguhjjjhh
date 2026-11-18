//! Port of `GeoLocatorTests.cs` (`GeoapifyGeoLocatorTests`, `GeoLocatorChainTests`).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use common::FakeHttp;
use futures_util::future::BoxFuture;
use pavlov_core::security::Secret;
use pavlov_core::time::SystemClock;
use pavlov_core::vpn::GeoLocation;
use pavlov_host::vpn::geo::{GeoapifyGeoLocator, IpApiGeoLocator};
use pavlov_host::vpn::{GeoLocator, GeoLocatorChain, ResilientJsonClient};

fn client(http: &Arc<FakeHttp>) -> Arc<ResilientJsonClient> {
    Arc::new(ResilientJsonClient::new(
        Arc::clone(http) as _,
        Arc::new(SystemClock),
    ))
}

fn geoapify(http: &Arc<FakeHttp>) -> GeoapifyGeoLocator {
    GeoapifyGeoLocator::new(client(http), Secret::new("test-key"))
}

const NESTED_BODY: &str = r#"{
  "ip": "8.8.8.8",
  "city": { "name": "Mountain View" },
  "country": { "name": "United States", "iso_code": "US" },
  "state": { "name": "California" },
  "postcode": "94035",
  "location": { "latitude": 37.386, "longitude": -122.084 },
  "timezone": { "name": "America/Los_Angeles" }
}"#;

async fn locate(locator: &dyn GeoLocator, ip: &str) -> Option<GeoLocation> {
    locator.locate(ip).await.unwrap()
}

#[tokio::test]
async fn reads_the_nested_response() {
    let http = Arc::new(FakeHttp::canned(200, NESTED_BODY));
    let located = locate(&geoapify(&http), "8.8.8.8").await.unwrap();

    assert_eq!(located.city.as_deref(), Some("Mountain View"));
    assert_eq!(located.region.as_deref(), Some("California"));
    assert_eq!(located.country.as_deref(), Some("United States"));
    assert_eq!(located.country_code.as_deref(), Some("US"));
    assert_eq!(located.zip.as_deref(), Some("94035"));
    assert_eq!(located.timezone.as_deref(), Some("America/Los_Angeles"));
    assert_eq!(located.latitude, Some(37.386));
    assert_eq!(located.longitude, Some(-122.084));
}

#[tokio::test]
async fn region_falls_back_to_subdivisions() {
    let http = Arc::new(FakeHttp::canned(
        200,
        r#"{ "city": { "name": "Toronto" }, "country": { "name": "Canada", "iso_code": "CA" },
             "subdivisions": [ { "iso_code": "ON", "name": "Ontario" } ] }"#,
    ));
    let located = locate(&geoapify(&http), "1.1.1.1").await.unwrap();
    assert_eq!(located.region.as_deref(), Some("Ontario"));
}

#[tokio::test]
async fn accepts_bare_strings_where_objects_are_expected() {
    // A provider flattening its JSON would otherwise turn every lookup into a silent None.
    let http = Arc::new(FakeHttp::canned(
        200,
        r#"{ "city": "Berlin", "country": "Germany" }"#,
    ));
    let located = locate(&geoapify(&http), "1.1.1.1").await.unwrap();
    assert_eq!(located.city.as_deref(), Some("Berlin"));
    assert_eq!(located.country.as_deref(), Some("Germany"));
}

#[tokio::test]
async fn a_response_with_no_usable_field_is_not_an_answer() {
    // None, not an empty location: the chain stops at the first answer.
    let http = Arc::new(FakeHttp::canned(200, r#"{ "ip": "1.1.1.1" }"#));
    assert_eq!(locate(&geoapify(&http), "1.1.1.1").await, None);
}

#[tokio::test]
async fn the_key_goes_to_geoapify_and_nowhere_else() {
    let http = Arc::new(FakeHttp::canned(200, NESTED_BODY));
    let _ = locate(&geoapify(&http), "8.8.8.8").await;

    let url = http.last_url().unwrap();
    assert!(
        url.starts_with("https://api.geoapify.com/v1/ipinfo"),
        "{url}"
    );
    assert!(url.contains("apiKey=test-key"), "{url}");
}

#[tokio::test]
async fn ip_api_reads_the_isp_and_the_asn_out_of_the_as_field() {
    // Rust-only: the provider whose ISP and ASN the evasion scorer depends on.
    let http = Arc::new(FakeHttp::canned(
        200,
        r#"{"status":"success","country":"Canada","countryCode":"CA","regionName":"Ontario","city":"Toronto",
            "zip":"M5V","lat":43.65,"lon":-79.38,"timezone":"America/Toronto","isp":"Bell Canada",
            "org":"Bell","as":"AS577 Bell Canada"}"#,
    ));
    let located = locate(&IpApiGeoLocator::new(client(&http)), "1.2.3.4")
        .await
        .unwrap();

    assert_eq!(located.asn.as_deref(), Some("AS577"));
    assert_eq!(located.isp.as_deref(), Some("Bell Canada"));
    assert_eq!(located.region.as_deref(), Some("Ontario"));
    assert!(
        http.last_url()
            .unwrap()
            .starts_with("http://ip-api.com/json/1.2.3.4?fields=")
    );

    let failed = Arc::new(FakeHttp::canned(200, r#"{"status":"fail"}"#));
    assert_eq!(
        locate(&IpApiGeoLocator::new(client(&failed)), "1.2.3.4").await,
        None
    );
}

// ---- the chain ----

/// A fixed answer, a failure, or never answering, counting its calls.
struct Stub {
    result: Result<Option<GeoLocation>, String>,
    hang: bool,
    calls: AtomicUsize,
}

impl Stub {
    fn answering(result: Option<GeoLocation>) -> Arc<Self> {
        Arc::new(Self {
            result: Ok(result),
            hang: false,
            calls: AtomicUsize::new(0),
        })
    }

    fn failing() -> Arc<Self> {
        Arc::new(Self {
            result: Err("boom".into()),
            hang: false,
            calls: AtomicUsize::new(0),
        })
    }

    fn hanging() -> Arc<Self> {
        Arc::new(Self {
            result: Ok(None),
            hang: true,
            calls: AtomicUsize::new(0),
        })
    }

    fn calls(&self) -> usize {
        self.calls.load(Ordering::SeqCst)
    }
}

impl GeoLocator for Stub {
    fn locate<'a>(&'a self, _: &'a str) -> BoxFuture<'a, Result<Option<GeoLocation>, String>> {
        self.calls.fetch_add(1, Ordering::SeqCst);
        if self.hang {
            return Box::pin(futures_util::future::pending());
        }
        let result = self.result.clone();
        Box::pin(async move { result })
    }
}

fn city(name: &str) -> GeoLocation {
    GeoLocation {
        city: Some(name.into()),
        ..GeoLocation::default()
    }
}

fn chain(providers: &[&Arc<Stub>]) -> GeoLocatorChain {
    GeoLocatorChain::new(
        providers
            .iter()
            .enumerate()
            .map(|(i, p)| (format!("stub{i}"), Arc::clone(p) as Arc<dyn GeoLocator>))
            .collect(),
    )
}

#[tokio::test]
async fn the_first_provider_that_answers_wins_and_the_rest_are_not_called() {
    // Order is policy: a second provider asked while the first is healthy spends quota for a
    // strictly worse answer.
    let first = Stub::answering(Some(GeoLocation {
        city: Some("Toronto".into()),
        isp: Some("Bell".into()),
        asn: Some("AS577".into()),
        ..GeoLocation::default()
    }));
    let second = Stub::answering(Some(city("Nowhere")));

    let located = locate(&chain(&[&first, &second]), "1.1.1.1").await.unwrap();

    assert_eq!(located.city.as_deref(), Some("Toronto"));
    assert_eq!(located.asn.as_deref(), Some("AS577"));
    assert_eq!(first.calls(), 1);
    assert_eq!(second.calls(), 0);
}

#[tokio::test]
async fn a_null_answer_falls_through_to_the_next_provider() {
    let first = Stub::answering(None);
    let second = Stub::answering(Some(city("Berlin")));

    let located = locate(&chain(&[&first, &second]), "1.1.1.1").await.unwrap();

    assert_eq!(located.city.as_deref(), Some("Berlin"));
    assert_eq!(second.calls(), 1);
}

#[tokio::test]
async fn a_throwing_provider_does_not_end_the_chain() {
    let first = Stub::failing();
    let second = Stub::answering(Some(city("Berlin")));

    let located = locate(&chain(&[&first, &second]), "1.1.1.1").await.unwrap();

    assert_eq!(located.city.as_deref(), Some("Berlin"));
}

#[tokio::test]
async fn cancellation_is_not_swallowed_as_a_provider_failure() {
    // Rust's cancellation is the caller dropping the future (the bot shutting down). The chain
    // must stop where it is, not work through every remaining provider.
    let first = Stub::hanging();
    let second = Stub::answering(Some(city("Berlin")));
    let chain = chain(&[&first, &second]);

    let gave_up = tokio::time::timeout(Duration::from_millis(20), chain.locate("1.1.1.1")).await;

    assert!(gave_up.is_err());
    assert_eq!(first.calls(), 1);
    assert_eq!(second.calls(), 0);
}

#[tokio::test]
async fn no_provider_answering_is_null_rather_than_an_empty_location() {
    let located = locate(
        &chain(&[&Stub::answering(None), &Stub::answering(None)]),
        "1.1.1.1",
    )
    .await;
    assert_eq!(located, None);
}
