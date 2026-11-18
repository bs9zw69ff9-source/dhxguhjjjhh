//! The timeline writer (C# `Events/EventRecorder.cs`): every caller hands over an event and
//! returns at once; one task batches them into the event store.
//!
//! NEVER IN THE CALLER'S WAY: callers are log-ingest and command handlers, so recording never
//! waits, never fails, and never throws, during shutdown included. A burst becomes a handful of
//! transactions, a failing store costs one batch and never the writer, and shutdown flushes
//! what was accepted (with a five-second limit, so a wedged disk cannot hang a deploy).

use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use pavlov_core::events::ServerEvent;
use pavlov_storage::EventStore;
use tokio::sync::Notify;
use tokio::task::JoinHandle;

use crate::queue::DropOldestQueue;

pub const QUEUE_CAPACITY: usize = 2000;
pub const BATCH_SIZE: usize = 256;
/// The pause between batches, so a steady trickle coalesces.
pub const FLUSH_INTERVAL: Duration = Duration::from_secs(2);
const SHUTDOWN_GRACE: Duration = Duration::from_secs(5);

/// Where events go. Fire and forget.
pub trait Timeline: Send + Sync {
    fn record(&self, event: ServerEvent);
}

/// The batching writer.
pub struct EventRecorder {
    store: Arc<dyn EventStore>,
    queue: DropOldestQueue<ServerEvent>,
    stop: Notify,
    task: Mutex<Option<JoinHandle<()>>>,
}

impl std::fmt::Debug for EventRecorder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("EventRecorder")
            .field("queued", &self.queue.len())
            .field("dropped", &self.queue.dropped())
            .finish_non_exhaustive()
    }
}

impl EventRecorder {
    /// The recorder and its writer task.
    #[must_use]
    pub fn start(store: Arc<dyn EventStore>) -> Arc<Self> {
        let recorder = Arc::new(Self {
            store,
            queue: DropOldestQueue::new(QUEUE_CAPACITY),
            stop: Notify::new(),
            task: Mutex::new(None),
        });
        let writer = Arc::clone(&recorder);
        let task = tokio::spawn(async move { writer.drain().await });
        *recorder.task.lock().unwrap_or_else(PoisonError::into_inner) = Some(task);
        recorder
    }

    /// Events lost to a full queue, or offered after shutdown.
    #[must_use]
    pub fn dropped(&self) -> u64 {
        self.queue.dropped()
    }

    pub fn record_all(&self, events: impl IntoIterator<Item = ServerEvent>) {
        for event in events {
            self.record(event);
        }
    }

    async fn drain(&self) {
        while self.queue.ready().await {
            let batch = self.queue.take(BATCH_SIZE);
            if batch.is_empty() {
                continue;
            }
            self.write(batch).await;
            // A pause between batches, cut short by shutdown.
            if !self.queue.is_closed() {
                let _ = tokio::time::timeout(FLUSH_INTERVAL, self.stop.notified()).await;
            }
        }
    }

    async fn write(&self, batch: Vec<ServerEvent>) {
        let store = Arc::clone(&self.store);
        let count = batch.len();
        // SQLite is blocking; it does not get a runtime worker.
        let outcome = tokio::task::spawn_blocking(move || store.append(&batch)).await;
        match outcome {
            Ok(Ok(())) => {}
            // NEVER fatal: the writer is the queue's only consumer.
            Ok(Err(error)) => tracing::error!(count, %error, "could not write timeline events"),
            Err(error) => tracing::error!(count, %error, "the timeline write task failed"),
        }
    }

    /// Stop accepting, flush what was accepted, and wait for it (up to five seconds).
    pub async fn shutdown(&self) {
        self.queue.close();
        self.stop.notify_waiters();
        let task = self
            .task
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .take();
        if let Some(task) = task {
            if tokio::time::timeout(SHUTDOWN_GRACE, task).await.is_err() {
                tracing::warn!(
                    queued = self.queue.len(),
                    "the timeline writer did not finish within 5s - queued events may be lost"
                );
            }
        }
        if self.dropped() > 0 {
            tracing::warn!(
                count = self.dropped(),
                "timeline events were dropped this run - the writer could not keep up"
            );
        }
    }
}

impl Timeline for EventRecorder {
    fn record(&self, event: ServerEvent) {
        let _ = self.queue.push(event);
    }
}
