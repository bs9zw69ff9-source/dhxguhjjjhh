//! A map image of where an address is (C# `Vpn/StaticMap.cs`).
//!
//! BYTES, NEVER A URL: the key is in the URL, and a link in a Discord embed would show it to
//! everyone who can see the message, for as long as Discord keeps it.

use std::sync::Arc;
use std::time::Duration;

use pavlov_core::security::Secret;

use crate::http::{HttpClient, HttpRequest, escape_data_string};

const MAX_BYTES: usize = 2 * 1024 * 1024;
const TIMEOUT: Duration = Duration::from_secs(6);

/// `0.#####`, invariant: up to five decimals, no trailing zeros, always `.`.
fn coordinate(value: f64) -> String {
    let text = format!("{value:.5}");
    let trimmed = text.trim_end_matches('0').trim_end_matches('.');
    if trimmed == "-0" {
        "-0".into()
    } else {
        trimmed.to_owned()
    }
}

/// Geoapify's static map.
pub struct StaticMap {
    http: Arc<dyn HttpClient>,
    api_key: Secret,
}

impl std::fmt::Debug for StaticMap {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StaticMap").finish_non_exhaustive()
    }
}

impl StaticMap {
    #[must_use]
    pub fn new(http: Arc<dyn HttpClient>, api_key: Secret) -> Self {
        Self { http, api_key }
    }

    /// The image, or `None`: an impossible coordinate (not requested at all), a failed
    /// request, or anything that is not an image.
    pub async fn render(&self, latitude: f64, longitude: f64) -> Option<Vec<u8>> {
        // Outside these it is a parsing accident, and (0, 0) is several providers' "unknown".
        if latitude.is_nan()
            || longitude.is_nan()
            || !(-90.0..=90.0).contains(&latitude)
            || !(-180.0..=180.0).contains(&longitude)
            || (latitude == 0.0 && longitude == 0.0)
        {
            return None;
        }
        let (lat, lon) = (coordinate(latitude), coordinate(longitude));
        let url = format!(
            "https://maps.geoapify.com/v1/staticmap?style=osm-bright-smooth&width=600&height=320&zoom=9&center=lonlat:{lon},{lat}&marker=lonlat:{lon},{lat};color:%23e04f5f;size:medium&apiKey={}",
            escape_data_string(self.api_key.expose())
        );
        let mut request = HttpRequest::get(url, TIMEOUT);
        request.max_body = MAX_BYTES;
        let response = match self.http.send(request).await {
            Ok(response) => response,
            Err(error) => {
                // A map outage must not take the lookup with it. The URL is never logged.
                tracing::warn!(%error, "map render failed");
                return None;
            }
        };
        if !response.is_success() {
            tracing::warn!(status = response.status, "map render failed");
            return None;
        }
        // An error page served as 200 would upload to Discord as a broken "map".
        if let Some(media) = response.media_type()
            && !media.to_ascii_lowercase().starts_with("image/")
        {
            tracing::warn!(media, "map render returned something that is not an image");
            return None;
        }
        (!response.body.is_empty() && response.body.len() <= MAX_BYTES).then_some(response.body)
    }
}
