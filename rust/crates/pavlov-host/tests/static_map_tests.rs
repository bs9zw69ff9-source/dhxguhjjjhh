//! Port of `StaticMapTests.cs`: a map image, bytes and never a URL.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use common::FakeHttp;
use pavlov_core::security::Secret;
use pavlov_host::http::HttpResponse;
use pavlov_host::vpn::StaticMap;

const PNG: [u8; 8] = [0x89, b'P', b'N', b'G', 1, 2, 3, 4];

fn image(status: u16, body: &[u8], media: Option<&str>) -> Arc<FakeHttp> {
    let mut response = HttpResponse::bytes(status, body.to_vec());
    if let Some(media) = media {
        response = response.with_header("Content-Type", media);
    }
    Arc::new(FakeHttp::responding(response))
}

fn map(http: &Arc<FakeHttp>) -> StaticMap {
    StaticMap::new(Arc::clone(http) as _, Secret::new("secret-key"))
}

#[tokio::test]
async fn returns_the_image_bytes() {
    let http = image(200, &PNG, Some("image/png"));
    assert_eq!(map(&http).render(43.65, -79.38).await.unwrap(), PNG);
}

#[tokio::test]
async fn coordinates_are_formatted_invariantly() {
    // Rust formatting has no culture; pinned anyway, because "-79,38,43,65" is a different place.
    let http = image(200, &PNG, Some("image/png"));
    let _ = map(&http).render(43.65, -79.38).await;
    assert!(http.last_url().unwrap().contains("lonlat:-79.38,43.65"));

    let _ = map(&http).render(12.123456789, 5.0).await;
    assert!(
        http.last_url().unwrap().contains("lonlat:5,12.12346"),
        "{:?}",
        http.last_url()
    );
}

#[tokio::test]
async fn impossible_coordinates_are_not_requested_at_all() {
    for (latitude, longitude) in [
        (0.0, 0.0),
        (91.0, 10.0),
        (-91.0, 10.0),
        (10.0, 181.0),
        (10.0, -181.0),
        (f64::NAN, 10.0),
    ] {
        let http = image(200, &PNG, Some("image/png"));
        assert_eq!(
            map(&http).render(latitude, longitude).await,
            None,
            "{latitude},{longitude}"
        );
        assert_eq!(http.calls(), 0); // no quota spent on a coordinate that is not a place
    }
}

#[tokio::test]
async fn an_error_page_served_as200_is_not_treated_as_an_image() {
    let http = image(
        200,
        br#"{"error":"quota exceeded"}"#,
        Some("application/json"),
    );
    assert_eq!(map(&http).render(43.65, -79.38).await, None);
}

#[tokio::test]
async fn a_failed_request_is_null_rather_than_an_exception() {
    let http = image(429, &[], None);
    assert_eq!(map(&http).render(43.65, -79.38).await, None);
}

#[tokio::test]
async fn an_empty_body_is_not_an_image() {
    let http = image(200, &[], Some("image/png"));
    assert_eq!(map(&http).render(43.65, -79.38).await, None);
}

#[tokio::test]
async fn shutdown_cancellation_propagates() {
    // Dropping the render (the bot stopping) ends it at once: nothing holds shutdown open for
    // a picture.
    let http = Arc::new(FakeHttp::hanging());
    let gave_up =
        tokio::time::timeout(Duration::from_millis(20), map(&http).render(43.65, -79.38)).await;
    assert!(gave_up.is_err());
}

#[tokio::test]
async fn the_api_key_goes_to_geoapify_and_is_never_returned_to_the_caller() {
    let http = image(200, &PNG, Some("image/png"));
    let bytes: Vec<u8> = map(&http).render(43.65, -79.38).await.unwrap();

    let url = http.last_url().unwrap();
    assert!(
        url.starts_with("https://maps.geoapify.com/v1/staticmap"),
        "{url}"
    );
    assert!(url.contains("apiKey=secret-key"), "{url}");
    assert!(!String::from_utf8_lossy(&bytes).contains("secret-key"));
    assert_eq!(http.requests.lock().unwrap()[0].max_body, 2 * 1024 * 1024);
}
