//! Port of `DetectorFailureTests.cs`: why a detector gave no verdict. An invalid key, a spent
//! quota, a blocked port and a body with no field are four fixes behind one blank answer
//! unless the reason reaches `/vpncheck`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;

use common::FakeHttp;
use pavlov_core::security::Secret;
use pavlov_core::time::SystemClock;
use pavlov_core::vpn::VpnKeys;
use pavlov_host::vpn::detectors::{
    IpHubDetector, IpapiIsDetector, IpqsDetector, ProxyCheckDetector, SentinelDetector, all,
};
use pavlov_host::vpn::{DetectorProbe, FetchOptions, ResilientJsonClient, VpnDetector};

fn client(http: &Arc<FakeHttp>) -> Arc<ResilientJsonClient> {
    Arc::new(ResilientJsonClient::new(
        Arc::clone(http) as _,
        Arc::new(SystemClock),
    ))
}

fn keyed() -> VpnKeys {
    let k = || Some(Secret::new("k"));
    VpnKeys {
        ip_hub: k(),
        ipqs: k(),
        proxy_check: k(),
        ipapi_is: k(),
        sentinel: k(),
    }
}

// ---- the transport ----

#[tokio::test]
async fn a_rejected_key_is_named_as_one() {
    let http = Arc::new(FakeHttp::canned(401, r#"{"error":"invalid api key"}"#));

    let fetch = client(&http)
        .fetch("https://example.test/x", "test", FetchOptions::default())
        .await;

    assert!(fetch.document.is_none());
    let failure = fetch.failure.unwrap();
    assert!(failure.contains("401"), "{failure}");
    assert!(
        failure.to_lowercase().contains("check the api key"),
        "{failure}"
    );
    assert!(failure.contains("invalid api key"), "{failure}");
}

#[tokio::test]
async fn a_spent_quota_says_so_and_says_for_how_long() {
    let http = Arc::new(FakeHttp::canned(429, "{}"));

    let failure = client(&http)
        .fetch("https://example.test/x", "test", FetchOptions::default())
        .await
        .failure
        .unwrap();

    assert!(failure.contains("429"), "{failure}");
    assert!(
        failure.to_lowercase().contains("quota or rate limit"),
        "{failure}"
    );
    assert!(failure.contains("60s"), "{failure}");
}

#[tokio::test]
async fn the_rate_limit_pause_is_visible_rather_than_silent() {
    // The second lookup never leaves the process, and says so rather than nothing.
    let http = Arc::new(FakeHttp::canned(429, "{}"));
    let client = client(&http);

    let _ = client
        .fetch("https://example.test/x", "test", FetchOptions::default())
        .await;
    let second = client
        .fetch("https://example.test/y", "test", FetchOptions::default())
        .await
        .failure
        .unwrap();

    assert_eq!(http.calls(), 1); // genuinely not re-requested
    assert!(second.to_lowercase().contains("rate limited"), "{second}");
    assert!(second.to_lowercase().contains("not retrying"), "{second}");
}

#[tokio::test]
async fn an_html_error_page_is_not_mistaken_for_a_network_failure() {
    let http = Arc::new(FakeHttp::canned(200, "<html>Access denied</html>"));

    let fetch = client(&http)
        .fetch("https://example.test/x", "test", FetchOptions::default())
        .await;

    assert!(fetch.document.is_none());
    assert!(fetch.failure.unwrap().contains("not JSON"));
}

#[tokio::test]
async fn a_key_is_never_echoed_into_the_reason() {
    // Providers quote the URL - and so the key - back in their error bodies.
    const SECRET: &str = "sk_live_abcdef123456";
    let http = Arc::new(FakeHttp::canned(
        403,
        &format!(r#"{{"error":"bad key {SECRET}"}}"#),
    ));
    let options = FetchOptions {
        secrets: vec![SECRET],
        ..FetchOptions::default()
    };

    let failure = client(&http)
        .fetch(
            &format!("https://example.test/x?key={SECRET}"),
            "test",
            options,
        )
        .await
        .failure
        .unwrap();

    assert!(!failure.contains(SECRET), "{failure}");
    assert!(failure.contains("***"), "{failure}");
}

#[tokio::test]
async fn a_successful_fetch_has_no_failure_text() {
    let http = Arc::new(FakeHttp::canned(200, r#"{"ok":true}"#));

    let fetch = client(&http)
        .fetch("https://example.test/x", "test", FetchOptions::default())
        .await;

    assert!(fetch.document.is_some());
    assert_eq!(fetch.failure, None);
}

#[tokio::test(start_paused = true)]
async fn a_server_error_is_retried_and_then_named() {
    // Rust-only: 5xx is the one answer worth asking again, with backoff, then reported.
    let http = Arc::new(FakeHttp::canned(500, "oops"));

    let failure = client(&http)
        .fetch("https://example.test/x", "test", FetchOptions::default())
        .await
        .failure
        .unwrap();

    assert_eq!(http.calls(), 3);
    assert_eq!(failure, "HTTP 500");
}

#[tokio::test]
async fn a_retry_after_header_sets_the_pause() {
    let http = Arc::new(FakeHttp::responding(
        pavlov_host::http::HttpResponse::new(429, "{}").with_header("Retry-After", "120"),
    ));

    let failure = client(&http)
        .fetch("https://example.test/x", "test", FetchOptions::default())
        .await
        .failure
        .unwrap();

    assert!(failure.contains("paused for 120s"), "{failure}");
}

// ---- the detectors ----

fn every(http: &Arc<FakeHttp>) -> Vec<Arc<dyn VpnDetector>> {
    all(&client(http), &keyed())
}

#[tokio::test]
async fn every_detector_reports_a_rejected_key_rather_than_shrugging() {
    // All of them: the reported failure was "every detector except one says nothing".
    let http = Arc::new(FakeHttp::canned(403, r#"{"error":"nope"}"#));
    for detector in every(&http) {
        let outcome = detector.lookup("8.8.8.8").await;

        assert!(outcome.reading.is_none());
        let failure = outcome.failure.unwrap_or_default();
        assert!(
            !failure.trim().is_empty(),
            "{} gave no reason",
            detector.name()
        );
        assert!(failure.contains("403"), "{}: {failure}", detector.name());
    }
}

#[tokio::test]
async fn a_provider_that_answered_without_a_verdict_says_that() {
    // On the keyless providers, this is what a spent quota looks like.
    let http = Arc::new(FakeHttp::canned(200, r#"{"unexpected":true}"#));
    for detector in every(&http) {
        let outcome = detector.lookup("8.8.8.8").await;

        assert!(outcome.reading.is_none());
        let failure = outcome.failure.unwrap();
        assert!(
            failure.starts_with("answered, but"),
            "{}: {failure}",
            detector.name()
        );
    }
}

#[tokio::test]
async fn a_good_response_still_produces_a_reading() {
    let http = Arc::new(FakeHttp::canned(
        200,
        r#"{"ip":"8.8.8.8","is_vpn":true,"is_proxy":false,"is_tor":false,"is_abuser":false,
            "is_datacenter":true,"is_mobile":false,
            "company":{"name":"Google LLC","type":"hosting"},
            "asn":{"asn":15169,"org":"Google LLC"},
            "location":{"country":"United States","country_code":"US","city":"Mountain View"}}"#,
    ));

    let outcome = IpapiIsDetector::new(client(&http), keyed())
        .lookup("8.8.8.8")
        .await;

    assert_eq!(outcome.failure, None);
    let reading = outcome.reading.unwrap();
    assert!(reading.flagged);
    assert_eq!(reading.vpn, Some(true));
    assert_eq!(reading.asn.as_deref(), Some("AS15169"));
    assert_eq!(reading.detail.as_deref(), Some("vpn (datacenter)"));
    assert_eq!(reading.residential, Some(false));
}

#[test]
fn an_unconfigured_detector_is_disabled_not_failing() {
    // "No key set" is a different sentence from "this one is broken".
    let none = VpnKeys::default();
    let http = Arc::new(FakeHttp::canned(200, "{}"));
    let c = client(&http);

    assert!(!IpHubDetector::new(Arc::clone(&c), none.clone()).enabled());
    assert!(!IpqsDetector::new(Arc::clone(&c), none.clone()).enabled());
    assert!(!SentinelDetector::new(Arc::clone(&c), none.clone()).enabled());
    // These two work without a key, which is why a bot with no configuration still screens.
    assert!(IpapiIsDetector::new(Arc::clone(&c), none.clone()).enabled());
    assert!(ProxyCheckDetector::new(c, none).enabled());
}

fn probe(
    answered: bool,
    detail: Option<&str>,
    error: Option<&str>,
    configured: bool,
) -> DetectorProbe {
    DetectorProbe {
        name: "iphub".into(),
        tier: 1,
        answered,
        flagged: answered.then_some(true),
        detail: detail.map(str::to_owned),
        duration_ms: 12.0,
        error: error.map(str::to_owned),
        configured,
    }
}

#[test]
fn the_probe_line_never_reads_as_a_bare_no_verdict() {
    assert_eq!(
        probe(false, None, None, false).outcome(),
        "no API key set - not checked"
    );
    assert_eq!(
        probe(false, None, Some("HTTP 401 - check the API key"), true).outcome(),
        "HTTP 401 - check the API key"
    );
    assert_eq!(
        probe(true, Some("vpn:true"), None, true).outcome(),
        "vpn:true"
    );
}

#[tokio::test]
async fn each_detector_asks_its_own_endpoint_with_the_address_escaped() {
    // Rust-only: the wire, per provider.
    let http = Arc::new(FakeHttp::canned(403, "{}"));
    for detector in every(&http) {
        let _ = detector.lookup("2001:db8::1").await;
    }
    let urls = http.urls();
    assert_eq!(urls[0], "https://v2.api.iphub.info/ip/2001%3Adb8%3A%3A1");
    assert_eq!(urls[1], "https://api.ipapi.is/?q=2001%3Adb8%3A%3A1&key=k");
    assert_eq!(
        urls[2],
        "https://proxycheck.io/v2/2001%3Adb8%3A%3A1?vpn=1&risk=1&asn=1&key=k"
    );
    assert_eq!(urls[3], "https://sntlhq.com/v1/lookup/2001%3Adb8%3A%3A1");
    assert_eq!(
        urls[4],
        "https://www.ipqualityscore.com/api/json/ip/k/2001%3Adb8%3A%3A1?strictness=1"
    );
    let requests = http.requests.lock().unwrap();
    assert!(
        requests[0]
            .headers
            .contains(&("X-Key".to_owned(), "k".to_owned()))
    );
    assert!(
        requests[3]
            .headers
            .contains(&("Authorization".to_owned(), "Bearer k".to_owned()))
    );
}
