//! Port of `EventRecorderTests.cs`: the timeline writer batches, survives a failing store,
//! never throws, and flushes on shutdown.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::io;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use chrono::{DateTime, Utc};
use pavlov_core::events::{EventCategory, EventQuery, ServerEvent};
use pavlov_host::timeline::{EventRecorder, Timeline};
use pavlov_storage::{EventGrouping, EventStore};

#[derive(Default)]
struct RecordingStore {
    written: Mutex<Vec<ServerEvent>>,
    batches: AtomicUsize,
    fail: AtomicBool,
}

impl RecordingStore {
    fn written(&self) -> Vec<ServerEvent> {
        self.written.lock().unwrap().clone()
    }
}

impl EventStore for RecordingStore {
    fn append(&self, events: &[ServerEvent]) -> io::Result<()> {
        if self.fail.load(Ordering::SeqCst) {
            return Err(io::Error::other("disk is full"));
        }
        self.written.lock().unwrap().extend_from_slice(events);
        self.batches.fetch_add(1, Ordering::SeqCst);
        Ok(())
    }
    fn query(&self, _: &EventQuery) -> io::Result<Vec<ServerEvent>> {
        Ok(self.written())
    }
    fn count(&self) -> io::Result<u64> {
        Ok(self.written().len() as u64)
    }
    fn prune(&self, _: DateTime<Utc>) -> io::Result<usize> {
        Ok(0)
    }
    fn count_by(&self, _: EventGrouping, _: &EventQuery) -> io::Result<Vec<(String, u64)>> {
        Ok(Vec::new())
    }
    fn count_distinct(&self, _: EventGrouping, _: &EventQuery) -> io::Result<u64> {
        Ok(0)
    }
}

fn event(kind: &str) -> ServerEvent {
    let mut e = ServerEvent::new(Utc::now(), EventCategory::Player, kind);
    e.player = Some("Alice".into());
    e
}

#[tokio::test]
async fn everything_recorded_is_written_by_the_time_it_is_disposed() {
    let store = Arc::new(RecordingStore::default());
    let recorder = EventRecorder::start(Arc::clone(&store) as _);

    for i in 0..20 {
        recorder.record(event(&format!("e{i}")));
    }
    recorder.shutdown().await;

    assert_eq!(store.written().len(), 20);
}

#[tokio::test]
async fn a_burst_is_batched_rather_than_written_one_at_a_time() {
    // Waits for the DRAIN, not shutdown: shutdown flushes in one batch and proves nothing.
    let store = Arc::new(RecordingStore::default());
    let recorder = EventRecorder::start(Arc::clone(&store) as _);

    for i in 0..500 {
        recorder.record(event(&format!("e{i}")));
    }
    let deadline = Instant::now() + Duration::from_secs(30);
    while store.written().len() < 500 && Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(100)).await;
    }

    assert_eq!(store.written().len(), 500);
    let batches = store.batches.load(Ordering::SeqCst);
    assert!(batches < 20, "500 events took {batches} transactions");
    recorder.shutdown().await;
}

#[tokio::test]
async fn a_failing_store_does_not_kill_the_writer() {
    let store = Arc::new(RecordingStore::default());
    store.fail.store(true, Ordering::SeqCst);
    let recorder = EventRecorder::start(Arc::clone(&store) as _);

    recorder.record(event("lost"));
    tokio::time::sleep(Duration::from_millis(200)).await;
    store.fail.store(false, Ordering::SeqCst);
    recorder.record(event("later"));
    recorder.shutdown().await;

    // The first batch was lost, which is the accepted cost. The writer survived it.
    assert!(store.written().iter().any(|e| e.kind == "later"));
}

#[tokio::test]
async fn recording_never_throws() {
    let store = Arc::new(RecordingStore::default());
    store.fail.store(true, Ordering::SeqCst);
    let recorder = EventRecorder::start(Arc::clone(&store) as _);

    recorder.record(event("a"));
    recorder.record_all([event("b"), event("c")]);
    recorder.shutdown().await;
}

#[tokio::test]
async fn recording_after_disposal_is_safe() {
    let store = Arc::new(RecordingStore::default());
    let recorder = EventRecorder::start(Arc::clone(&store) as _);
    recorder.shutdown().await;

    recorder.record(event("late"));

    assert!(recorder.dropped() >= 1);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn concurrent_producers_do_not_lose_events() {
    let store = Arc::new(RecordingStore::default());
    let recorder = EventRecorder::start(Arc::clone(&store) as _);

    let producers: Vec<_> = (0..8)
        .map(|worker| {
            let recorder = Arc::clone(&recorder);
            tokio::spawn(async move {
                for i in 0..50 {
                    recorder.record(event(&format!("w{worker}-{i}")));
                }
            })
        })
        .collect();
    for producer in producers {
        producer.await.unwrap();
    }
    recorder.shutdown().await;

    assert_eq!(store.written().len(), 400);
    assert_eq!(recorder.dropped(), 0);
}
