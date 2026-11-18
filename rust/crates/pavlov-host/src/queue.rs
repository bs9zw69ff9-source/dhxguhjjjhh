//! A bounded queue that DROPS THE OLDEST when full, with one async consumer (the C#
//! `Channel.CreateBounded(DropOldest, SingleReader)`).
//!
//! For work that is cosmetic next to what produces it: a feed line or a timeline event must
//! never hold up the log ingest that produced it, so a full queue loses its oldest item rather
//! than making the producer wait.

use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Mutex, PoisonError};

use tokio::sync::Notify;

/// The queue.
#[derive(Debug)]
pub struct DropOldestQueue<T> {
    items: Mutex<VecDeque<T>>,
    capacity: usize,
    notify: Notify,
    closed: AtomicBool,
    dropped: AtomicU64,
}

impl<T> DropOldestQueue<T> {
    #[must_use]
    pub fn new(capacity: usize) -> Self {
        Self {
            items: Mutex::new(VecDeque::with_capacity(capacity.min(1024))),
            capacity: capacity.max(1),
            notify: Notify::new(),
            closed: AtomicBool::new(false),
            dropped: AtomicU64::new(0),
        }
    }

    /// Add an item, never waiting. `Some(oldest)` when it pushed one out; an item offered
    /// after [`close`](Self::close) is counted as dropped and returned.
    pub fn push(&self, item: T) -> Option<T> {
        if self.closed.load(Ordering::Acquire) {
            self.dropped.fetch_add(1, Ordering::Relaxed);
            return Some(item);
        }
        let evicted = {
            let mut items = self.items.lock().unwrap_or_else(PoisonError::into_inner);
            let evicted = (items.len() >= self.capacity)
                .then(|| items.pop_front())
                .flatten();
            items.push_back(item);
            evicted
        };
        if evicted.is_some() {
            self.dropped.fetch_add(1, Ordering::Relaxed);
        }
        self.notify.notify_one();
        evicted
    }

    /// Up to `max` items, oldest first, without waiting.
    pub fn take(&self, max: usize) -> Vec<T> {
        let mut items = self.items.lock().unwrap_or_else(PoisonError::into_inner);
        let n = max.min(items.len());
        items.drain(..n).collect()
    }

    /// Wait until there is something to take, or the queue is closed and empty. `false` means
    /// closed and drained: the consumer should stop.
    pub async fn ready(&self) -> bool {
        loop {
            let notified = self.notify.notified();
            if !self.is_empty() {
                return true;
            }
            if self.closed.load(Ordering::Acquire) {
                return false;
            }
            notified.await;
        }
    }

    /// No more items will be accepted; the consumer drains what is left and stops.
    pub fn close(&self) {
        self.closed.store(true, Ordering::Release);
        self.notify.notify_waiters();
        self.notify.notify_one();
    }

    #[must_use]
    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::Acquire)
    }

    #[must_use]
    pub fn len(&self) -> usize {
        self.items
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .len()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Items lost to a full queue or offered after close.
    #[must_use]
    pub fn dropped(&self) -> u64 {
        self.dropped.load(Ordering::Relaxed)
    }
}
