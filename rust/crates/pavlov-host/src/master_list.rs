//! The platform's public server list, cached briefly (C# `Servers/MasterServerList.cs`).
//!
//! The cache protects the endpoint from bursts of interactive commands (four people running
//! `/server lookup` at once cost one request). A background tick that publishes a number
//! FORCES a read instead, so it never publishes what a lookup cached a minute ago.

use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use chrono::{DateTime, Utc};
use pavlov_core::servers::listing::{self, ServerListing};
use pavlov_core::time::Clock;

use crate::http::{HttpClient, HttpRequest};

/// The game version in the URL. Vankrupt version the ENDPOINT, not the payload: a wrong one
/// is a 200 with no servers, which looks exactly like a quiet platform.
pub const DEFAULT_VERSION: &str = "1.0.32";
const TTL: Duration = Duration::from_secs(60);
const FETCH_TIMEOUT: Duration = Duration::from_secs(12);

/// The list for a version.
#[must_use]
pub fn endpoint(version: &str) -> String {
    format!("https://prod2-shack-pavlov-ms.vankrupt.net/servers/v2/list/{version}/oculus/0/0/0/all")
}

/// What a read returns.
#[derive(Debug, Clone)]
pub struct Snapshot {
    pub servers: Arc<Vec<ServerListing>>,
    /// How old the list is; `None` when nothing has ever been fetched.
    pub age: Option<Duration>,
    /// This read wanted a fresh list and did not get one: what is here is the last good one.
    pub failed: bool,
}

#[derive(Debug, Default)]
struct Cache {
    servers: Arc<Vec<ServerListing>>,
    fetched_at: Option<DateTime<Utc>>,
}

/// The cached list.
pub struct MasterServerList {
    http: Arc<dyn HttpClient>,
    clock: Arc<dyn Clock>,
    version: String,
    cache: Mutex<Cache>,
    /// Single flight: one fetch at a time, and the losers get the winner's answer.
    gate: tokio::sync::Mutex<()>,
}

impl std::fmt::Debug for MasterServerList {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MasterServerList")
            .field("version", &self.version)
            .finish_non_exhaustive()
    }
}

impl MasterServerList {
    /// A blank or absent version (`PAVLOV_VERSION=`) is the default, never `/list//oculus`.
    #[must_use]
    pub fn new(http: Arc<dyn HttpClient>, version: Option<&str>, clock: Arc<dyn Clock>) -> Self {
        let version = version
            .map(str::trim)
            .filter(|v| !v.is_empty())
            .unwrap_or(DEFAULT_VERSION)
            .to_owned();
        Self {
            http,
            clock,
            version,
            cache: Mutex::new(Cache::default()),
            gate: tokio::sync::Mutex::new(()),
        }
    }

    #[must_use]
    pub fn version(&self) -> &str {
        &self.version
    }

    fn age(&self, fetched_at: Option<DateTime<Utc>>) -> Option<Duration> {
        fetched_at.map(|at| (self.clock.now() - at).to_std().unwrap_or_default())
    }

    fn fresh(&self) -> Option<Snapshot> {
        let cache = self.cache.lock().unwrap_or_else(PoisonError::into_inner);
        let age = self.age(cache.fetched_at)?;
        (!cache.servers.is_empty() && age < TTL).then(|| Snapshot {
            servers: Arc::clone(&cache.servers),
            age: Some(age),
            failed: false,
        })
    }

    fn stale(&self) -> Snapshot {
        let cache = self.cache.lock().unwrap_or_else(PoisonError::into_inner);
        Snapshot {
            servers: Arc::clone(&cache.servers),
            age: self.age(cache.fetched_at),
            failed: true,
        }
    }

    /// The list: cached inside the window unless `force`.
    pub async fn get(&self, force: bool) -> Snapshot {
        if !force && let Some(snapshot) = self.fresh() {
            return snapshot;
        }
        let _flight = self.gate.lock().await;
        if !force && let Some(snapshot) = self.fresh() {
            return snapshot;
        }
        let Some(fetched) = self.fetch().await else {
            return self.stale();
        };
        let mut cache = self.cache.lock().unwrap_or_else(PoisonError::into_inner);
        /* AN EMPTY LIST NEVER EVICTS A GOOD ONE. A version mismatch is a SUCCESSFUL request
        with zero servers; letting it overwrite hundreds made a search say "nothing matched"
        under a list still showing the server, and published "Pavlov Shack: 0". The fetch time
        is NOT advanced, so the next caller tries again; the gate stops that being a burst. */
        if fetched.is_empty() && !cache.servers.is_empty() {
            drop(cache);
            return self.stale();
        }
        cache.servers = Arc::new(fetched);
        cache.fetched_at = Some(self.clock.now());
        Snapshot {
            servers: Arc::clone(&cache.servers),
            age: Some(Duration::ZERO),
            failed: false,
        }
    }

    async fn fetch(&self) -> Option<Vec<ServerListing>> {
        let request = HttpRequest::get(endpoint(&self.version), FETCH_TIMEOUT);
        let response = match self.http.send(request).await {
            Ok(response) => response,
            Err(error) => {
                tracing::warn!(%error, "could not reach the Pavlov master server");
                return None;
            }
        };
        if !response.is_success() {
            tracing::warn!(
                status = response.status,
                "the Pavlov master server answered with an error - the server browser will show what it had"
            );
            return None;
        }
        let servers = listing::parse(&response.text());
        if servers.is_empty() {
            tracing::warn!(
                version = %self.version,
                "the Pavlov master server returned no servers for this version. That is usually a game update rather than an empty platform - the version is part of the URL"
            );
        }
        Some(servers)
    }
}
