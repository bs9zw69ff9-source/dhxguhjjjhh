//! Port of `VpnCacheSnapshotTests.cs`: the VPN cache is not decoded from scratch for every
//! lookup. FeedBridge screens every confirmed connection, so the cost of admitting one player
//! used to grow with every player the server had EVER seen. Counted at the backend: in Rust
//! every store read is a read plus a full decode, with no string cache in between.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::io;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use common::FakeHttp;
use pavlov_core::data::{KeyValueBackend, MemoryBackend, SerializedStore};
use pavlov_core::time::SystemClock;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::vpn::{GeoLocatorChain, VpnScreeningService};
use pavlov_storage::datasets::VPN_CHECKS;

/// Counts how often a dataset is actually read (and so decoded).
#[derive(Default)]
struct Counting {
    inner: MemoryBackend,
    reads: AtomicUsize,
}

impl KeyValueBackend for Counting {
    fn read(&self, key: &str) -> io::Result<Option<String>> {
        self.reads.fetch_add(1, Ordering::SeqCst);
        self.inner.read(key)
    }

    fn write(&self, key: &str, json: &str) -> io::Result<()> {
        self.inner.write(key, json)
    }
}

fn build() -> (VpnScreeningService, Arc<Counting>) {
    let backend = Arc::new(Counting::default());
    // SEEDED: an empty dataset returns the fallback before decoding anything, which would
    // pass against the bug this exists for.
    backend.inner.seed(
        VPN_CHECKS,
        r#"{"203.0.113.1":{"ip":"203.0.113.1","screenHits":0,"screenAnswered":1}}"#,
    );
    let store = Arc::new(SerializedStore::new(Arc::clone(&backend) as _));
    let _unused_http = FakeHttp::canned(200, "{}");
    let service = VpnScreeningService::new(
        Vec::new(), // no detectors: the cache is what is under test
        store,
        Arc::new(GeoLocatorChain::new(Vec::new())),
        Arc::new(MetricsRegistry::default()),
        Arc::new(SystemClock),
    );
    (service, backend)
}

#[test]
fn repeated_lookups_do_not_redecode_the_whole_cache() {
    let (service, backend) = build();

    let _ = service.cached("203.0.113.1");
    let after_first = backend.reads.load(Ordering::SeqCst);
    for i in 0..200 {
        let _ = service.cached(&format!("203.0.113.{}", i % 24));
    }

    assert_eq!(backend.reads.load(Ordering::SeqCst), after_first);
}

#[test]
fn the_cache_is_still_read_correctly() {
    let (service, _) = build();

    assert_eq!(service.cached("198.51.100.7"), None);
    assert_eq!(service.cached("203.0.113.1").unwrap().screen_answered, 1);
    assert_eq!(service.load_cache().len(), 1);
}
