//! JSON from third-party reputation and geolocation APIs, defensively (C#
//! `Vpn/ResilientJsonClient.cs`).
//!
//! Every lookup on the join path goes through here, so it: retries transport failures with
//! jittered backoff; remembers a 429/503 per host and stops asking until the pause is over;
//! names a rejected key ONCE in the log but returns the reason EVERY time (the diagnostic
//! command has to be able to ask); and scrubs every configured key out of anything it returns.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::time::Clock;
use serde_json::Value;

use crate::http::{HttpClient, HttpError, HttpRequest, HttpResponse, host_of};

/// The default per-attempt timeout.
pub const DEFAULT_TIMEOUT: Duration = Duration::from_secs(6);
const DEFAULT_ATTEMPTS: u32 = 3;
const DEFAULT_PAUSE: Duration = Duration::from_secs(60);
/// A daily quota legitimately asks for a day; anything longer is a broken header, and obeying
/// it would switch the provider off for good.
const MAX_PAUSE: Duration = Duration::from_secs(24 * 60 * 60);

/// A parsed document, or why there is none, worded for the operator.
#[derive(Debug, Clone, PartialEq)]
pub struct JsonFetch {
    pub document: Option<Value>,
    pub failure: Option<String>,
}

impl JsonFetch {
    fn ok(document: Value) -> Self {
        Self {
            document: Some(document),
            failure: None,
        }
    }

    fn failed(why: impl Into<String>) -> Self {
        Self {
            document: None,
            failure: Some(why.into()),
        }
    }
}

/// How to ask.
#[derive(Debug, Clone)]
pub struct FetchOptions<'a> {
    pub headers: Vec<(&'a str, String)>,
    /// Scrubbed out of anything returned or logged.
    pub secrets: Vec<&'a str>,
    pub attempts: u32,
    pub timeout: Duration,
}

impl Default for FetchOptions<'_> {
    fn default() -> Self {
        Self {
            headers: Vec::new(),
            secrets: Vec::new(),
            attempts: DEFAULT_ATTEMPTS,
            timeout: DEFAULT_TIMEOUT,
        }
    }
}

/// The client. One per process: the rate-limit memory is per host and shared by every caller.
pub struct ResilientJsonClient {
    http: Arc<dyn HttpClient>,
    clock: Arc<dyn Clock>,
    rate_limited_until: Mutex<HashMap<String, DateTime<Utc>>>,
    auth_warned: Mutex<HashSet<String>>,
}

impl std::fmt::Debug for ResilientJsonClient {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ResilientJsonClient")
            .finish_non_exhaustive()
    }
}

fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(PoisonError::into_inner)
}

/// `text` with every secret replaced by `***`.
#[must_use]
pub fn scrub(text: &str, secrets: &[&str]) -> String {
    secrets
        .iter()
        .filter(|s| !s.is_empty())
        .fold(text.to_owned(), |acc, secret| acc.replace(secret, "***"))
}

fn clip(text: &str, limit: usize) -> String {
    text.chars().take(limit).collect()
}

impl ResilientJsonClient {
    #[must_use]
    pub fn new(http: Arc<dyn HttpClient>, clock: Arc<dyn Clock>) -> Self {
        Self {
            http,
            clock,
            rate_limited_until: Mutex::new(HashMap::new()),
            auth_warned: Mutex::new(HashSet::new()),
        }
    }

    /// The document, or `None`; the reason is logged.
    pub async fn get(&self, url: &str, label: &str, options: FetchOptions<'_>) -> Option<Value> {
        self.fetch(url, label, options).await.document
    }

    /// The document, or why not.
    pub async fn fetch(&self, url: &str, label: &str, options: FetchOptions<'_>) -> JsonFetch {
        let host = host_of(url);
        if let Some(until) = lock(&self.rate_limited_until).get(&host).copied() {
            let now = self.clock.now();
            if now < until {
                let seconds = (until - now).num_seconds();
                tracing::debug!(label, seconds, "skipped - rate limited");
                return JsonFetch::failed(format!(
                    "rate limited - not retrying for another {seconds}s"
                ));
            }
        }

        let attempts = options.attempts.max(1);
        for attempt in 0..attempts {
            let mut request = HttpRequest::get(url, options.timeout);
            for (name, value) in &options.headers {
                request = request.header(*name, value.clone());
            }
            let error = match self.http.send(request).await {
                Ok(response) => match self.answer(&host, label, &response, &options.secrets) {
                    Some(fetch) => return fetch,
                    None => format!("HTTP {}", response.status),
                },
                Err(HttpError::Timeout(_)) => "timed out".to_owned(),
                Err(other) => scrub(&other.to_string(), &options.secrets),
            };
            if attempt + 1 == attempts {
                tracing::warn!(label, message = %error, "lookup failed");
                return JsonFetch::failed(clip(&error, 160));
            }
            /* Exponential backoff with full jitter: five detectors retrying the same upstream
            in lockstep is the burst most likely to trip the rate limit this avoids. */
            tokio::time::sleep(pavlov_rcon::backoff::delay(attempt, 300, 5_000)).await;
        }
        JsonFetch::failed("no response after retries")
    }

    /// The fetch this response settles, or `None` for a 5xx worth retrying.
    fn answer(
        &self,
        host: &str,
        label: &str,
        response: &HttpResponse,
        secrets: &[&str],
    ) -> Option<JsonFetch> {
        let status = response.status;
        if status == 429 || status == 503 {
            let wait = self.retry_after(response);
            let until = TimeDelta::from_std(wait)
                .ok()
                .and_then(|d| self.clock.now().checked_add_signed(d))
                .unwrap_or(DateTime::<Utc>::MAX_UTC);
            lock(&self.rate_limited_until).insert(host.to_owned(), until);
            tracing::warn!(
                label,
                status,
                seconds = wait.as_secs(),
                "rate limited - pausing lookups to it"
            );
            return Some(JsonFetch::failed(format!(
                "HTTP {status} - quota or rate limit reached, paused for {}s",
                wait.as_secs()
            )));
        }
        if status >= 500 {
            return None;
        }
        if status >= 400 {
            /* A 4xx that is not a rate limit is CONFIGURATION: a bad key, a suspended account,
            an address not allowlisted. Its body has no verdict, so it would otherwise read as a
            bland "no verdict" forever. */
            let hint = error_hint(response, secrets);
            let auth = status == 401 || status == 403;
            if lock(&self.auth_warned).insert(host.to_owned()) {
                let advice = if auth {
                    "Check the API key is correct and active."
                } else {
                    "No verdict will be recorded from it."
                };
                tracing::warn!(label, status, hint = %hint, advice, "the request was rejected");
            }
            return Some(JsonFetch::failed(format!(
                "HTTP {status}{hint}{}",
                if auth { " - check the API key" } else { "" }
            )));
        }
        match serde_json::from_slice::<Value>(&response.body) {
            Ok(document) => Some(JsonFetch::ok(document)),
            /* A 200 that is not JSON is a captive portal, an HTML error page or a proxy in the
            way - none of which look like a network failure, all of which retry forever. */
            Err(_) => {
                tracing::warn!(label, status, "the body was not JSON");
                Some(JsonFetch::failed("responded, but the body was not JSON"))
            }
        }
    }

    /// `Retry-After` as seconds or an HTTP date; a minute when absent or not in the future.
    fn retry_after(&self, response: &HttpResponse) -> Duration {
        let Some(value) = response.header("retry-after").map(str::trim) else {
            return DEFAULT_PAUSE;
        };
        let wait = if let Ok(seconds) = value.parse::<u64>() {
            Some(Duration::from_secs(seconds))
        } else {
            DateTime::parse_from_rfc2822(value)
                .ok()
                .and_then(|date| (date.with_timezone(&Utc) - self.clock.now()).to_std().ok())
        };
        wait.filter(|w| !w.is_zero())
            .map_or(DEFAULT_PAUSE, |w| w.min(MAX_PAUSE))
    }
}

/// " - <the provider's `error` text>", scrubbed and clipped, or nothing.
fn error_hint(response: &HttpResponse, secrets: &[&str]) -> String {
    serde_json::from_slice::<Value>(&response.body)
        .ok()
        .and_then(|v| v.get("error").and_then(Value::as_str).map(str::to_owned))
        .map(|message| format!(" - {}", clip(&scrub(&message, secrets), 120)))
        .unwrap_or_default()
}

/// Reading provider bodies field by field (C# `JsonHelp`). Every accessor is `None` for
/// "absent or the wrong type", never a default.
pub mod json {
    use serde_json::Value;

    #[must_use]
    pub fn member<'a>(parent: Option<&'a Value>, name: &str) -> Option<&'a Value> {
        parent?.as_object()?.get(name)
    }

    /// A string, or a number as written; trimmed, and `None` when blank.
    #[must_use]
    pub fn text(parent: Option<&Value>, name: &str) -> Option<String> {
        let text = match member(parent, name)? {
            Value::String(s) => s.clone(),
            Value::Number(n) => n.to_string(),
            _ => return None,
        };
        let trimmed = text.trim();
        (!trimmed.is_empty()).then(|| trimmed.to_owned())
    }

    #[must_use]
    pub fn boolean(parent: Option<&Value>, name: &str) -> Option<bool> {
        member(parent, name)?.as_bool()
    }

    #[must_use]
    pub fn number(parent: Option<&Value>, name: &str) -> Option<f64> {
        member(parent, name)?.as_f64()
    }

    /// An integer literal that fits in 32 bits; `1.0` is not one (as `TryGetInt32`).
    #[must_use]
    pub fn int(parent: Option<&Value>, name: &str) -> Option<i32> {
        i32::try_from(member(parent, name)?.as_i64()?).ok()
    }
}
