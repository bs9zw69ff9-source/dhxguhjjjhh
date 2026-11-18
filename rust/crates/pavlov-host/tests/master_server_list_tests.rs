//! Port of `MasterServerListTests.cs`: the master-server cache, and who is allowed to be served
//! out of it. The cache protects the endpoint from bursts of interactive commands; it is not
//! there to spare a background tick one request.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::Duration;

use common::FakeHttp;
use pavlov_core::time::SystemClock;
use pavlov_host::http::HttpResponse;
use pavlov_host::master_list::{DEFAULT_VERSION, MasterServerList};

/// The API's OWN field names: it calls the live count "slots" and the capacity "maxSlots".
fn one_server(players: usize) -> String {
    format!(
        r#"{{"servers":[{{"version":"1.0.27","name":"one","ip":"1.2.3.4","port":7777,"slots":{players},"maxSlots":24,"mapLabel":"datacenter","mapId":"UGC1","gameMode":"SND","gameModeLabel":"SND","bPasswordProtected":false,"bSecured":true}}]}}"#
    )
}

/// One more player on each call, starting at 1.
fn counting() -> Arc<FakeHttp> {
    let calls = AtomicUsize::new(0);
    Arc::new(FakeHttp::new(move |_| {
        let n = calls.fetch_add(1, Ordering::SeqCst) + 1;
        Ok(HttpResponse::new(200, one_server(n)))
    }))
}

/// One server, or (once switched) a 200 with none: a version mismatch is a SUCCESSFUL request.
fn switchable() -> (Arc<FakeHttp>, Arc<AtomicBool>) {
    let empty = Arc::new(AtomicBool::new(false));
    let flag = Arc::clone(&empty);
    let http = Arc::new(FakeHttp::new(move |_| {
        Ok(HttpResponse::new(
            200,
            if flag.load(Ordering::SeqCst) {
                r#"{"servers":[]}"#.to_owned()
            } else {
                one_server(3)
            },
        ))
    }));
    (http, empty)
}

fn master(http: &Arc<FakeHttp>, version: Option<&str>) -> MasterServerList {
    MasterServerList::new(Arc::clone(http) as _, version, Arc::new(SystemClock))
}

#[tokio::test]
async fn a_second_reader_inside_the_window_is_served_from_the_cache() {
    let http = counting();
    let list = master(&http, None);

    let first = list.get(false).await;
    let second = list.get(false).await;

    assert_eq!(http.calls(), 1);
    assert_eq!(first.servers[0].players, second.servers[0].players);
}

#[tokio::test]
async fn a_forced_read_always_goes_to_the_network() {
    // THE FIX FOR THE "TICK BEHIND" SYMPTOM: a published number must be true when published.
    let http = counting();
    let list = master(&http, None);

    let _ = list.get(false).await; // warms the cache with 1
    let forced = list.get(true).await; // must not be served 1

    assert_eq!(http.calls(), 2);
    assert_eq!(forced.servers[0].players, 2);
    assert_eq!(forced.age, Some(Duration::ZERO));
}

#[tokio::test]
async fn a_forced_read_that_fails_keeps_the_last_good_list() {
    let http = Arc::new(FakeHttp::canned(503, "{}"));
    let list = master(&http, None);

    let snapshot = list.get(true).await;

    assert!(snapshot.failed);
    assert!(snapshot.servers.is_empty()); // nothing was ever cached, so there is nothing to keep
    assert_eq!(snapshot.age, None);
}

#[tokio::test]
async fn an_empty_successful_answer_keeps_the_servers_we_already_had() {
    let (http, empty) = switchable();
    let list = master(&http, None);

    assert_eq!(list.get(true).await.servers.len(), 1);

    // The game updates: the URL still answers 200, with nothing in it.
    empty.store(true, Ordering::SeqCst);
    let second = list.get(true).await;

    assert_eq!(second.servers.len(), 1); // the good list survives
    assert_eq!(second.servers[0].name, "one");
    assert!(second.failed); // and is honestly flagged as stale
}

#[tokio::test]
async fn a_good_answer_after_an_empty_one_replaces_the_list() {
    let (http, empty) = switchable();
    let list = master(&http, None);

    let _ = list.get(true).await;
    empty.store(true, Ordering::SeqCst);
    let _ = list.get(true).await;
    empty.store(false, Ordering::SeqCst);
    let recovered = list.get(true).await;

    assert!(!recovered.failed);
    assert_eq!(recovered.servers.len(), 1);
}

#[tokio::test]
async fn an_empty_answer_with_nothing_cached_is_not_reported_as_a_failure() {
    let (http, empty) = switchable();
    empty.store(true, Ordering::SeqCst);
    let list = master(&http, None);

    let snapshot = list.get(true).await;

    assert!(snapshot.servers.is_empty());
    assert!(!snapshot.failed);
}

#[tokio::test]
async fn the_configured_version_is_the_one_requested() {
    let http = Arc::new(FakeHttp::canned(200, r#"{"servers":[]}"#));

    let _ = master(&http, Some("9.9.9")).get(true).await;

    assert!(http.urls()[0].contains("/list/9.9.9/"), "{:?}", http.urls());
}

#[test]
fn the_default_version_is_shaped_like_a_pavlov_version() {
    // A WRONG VERSION IS SILENT: "1.30" for "1.0.30" returns 200 and nothing at all.
    let parts: Vec<&str> = DEFAULT_VERSION.split('.').collect();
    assert_eq!(parts.len(), 3);
    assert!(
        parts
            .iter()
            .all(|p| !p.is_empty() && p.bytes().all(|b| b.is_ascii_digit())),
        "\"{DEFAULT_VERSION}\" is not a version the master server will recognise"
    );
}

#[tokio::test]
async fn the_default_version_is_used_when_none_is_configured() {
    let http = Arc::new(FakeHttp::canned(200, r#"{"servers":[]}"#));

    let _ = master(&http, None).get(true).await;

    assert!(http.urls()[0].contains(&format!("/list/{DEFAULT_VERSION}/")));
}

#[tokio::test]
async fn an_empty_override_falls_back_to_the_default() {
    // `PAVLOV_VERSION=` must not request /list//oculus and read as the platform being down.
    for configured in ["", "   "] {
        let http = Arc::new(FakeHttp::canned(200, r#"{"servers":[]}"#));
        let _ = master(&http, Some(configured)).get(true).await;
        assert!(
            http.urls()[0].contains(&format!("/list/{DEFAULT_VERSION}/")),
            "{configured:?}"
        );
    }
}

#[tokio::test]
async fn concurrent_readers_share_one_request() {
    // Rust-only: the single-flight gate, exercised.
    let http = counting();
    let list = Arc::new(master(&http, None));

    let readers: Vec<_> = (0..8)
        .map(|_| {
            let list = Arc::clone(&list);
            tokio::spawn(async move { list.get(false).await })
        })
        .collect();
    for reader in readers {
        assert_eq!(reader.await.unwrap().servers.len(), 1);
    }
    assert_eq!(http.calls(), 1);
}
