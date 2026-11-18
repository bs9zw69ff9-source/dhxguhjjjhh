//! Interaction work off the gateway's read loop (C# `InteractionDispatcher.cs`). The gateway
//! awaited event handlers inline, so a handler blocked for a minute stopped the bot reading
//! Discord for a minute. Work is spawned, counted, contained, and drained on shutdown.

use std::future::Future;
use std::panic::AssertUnwindSafe;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use futures_util::FutureExt;
use tokio::sync::Notify;

#[derive(Debug, Default)]
struct Counter {
    running: AtomicUsize,
    idle: Notify,
}

/// Decrements on drop, so a panicking task is still counted out.
struct InFlight(Arc<Counter>);

impl Drop for InFlight {
    fn drop(&mut self) {
        if self.0.running.fetch_sub(1, Ordering::AcqRel) == 1 {
            self.0.idle.notify_waiters();
        }
    }
}

/// The dispatcher.
#[derive(Debug, Default)]
pub struct InteractionDispatcher {
    counter: Arc<Counter>,
}

impl InteractionDispatcher {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Start `work` in the background and return at once. A panic in it is contained and logged.
    pub fn run(&self, what: &'static str, work: impl Future<Output = ()> + Send + 'static) {
        self.counter.running.fetch_add(1, Ordering::AcqRel);
        let guard = InFlight(Arc::clone(&self.counter));
        tokio::spawn(async move {
            let _guard = guard;
            if AssertUnwindSafe(work).catch_unwind().await.is_err() {
                tracing::error!(
                    what,
                    "interaction work panicked outside its own error handling"
                );
            }
        });
    }

    /// How many are still running.
    #[must_use]
    pub fn in_flight(&self) -> usize {
        self.counter.running.load(Ordering::Acquire)
    }

    /// Wait up to `timeout` for everything running. True when it all finished.
    pub async fn drain(&self, timeout: Duration) -> bool {
        tokio::time::timeout(timeout, async {
            loop {
                let idle = self.counter.idle.notified();
                if self.in_flight() == 0 {
                    return;
                }
                idle.await;
            }
        })
        .await
        .is_ok()
    }
}
