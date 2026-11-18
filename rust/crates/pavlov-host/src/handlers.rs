//! Subscribers to an event (the C# `event Func<T, Task>`).
//!
//! Every subscriber is awaited, in subscription order. The C# multicast delegate returned only
//! the LAST subscriber's task, so the others ran unawaited; here a slow subscriber delays the
//! next one rather than racing it, and none is ever left running in the background.

use std::sync::{Arc, Mutex, PoisonError};

use futures_util::future::BoxFuture;

type Handler<T> = Arc<dyn Fn(T) -> BoxFuture<'static, ()> + Send + Sync>;

/// A subscriber list.
pub struct Handlers<T> {
    list: Mutex<Vec<Handler<T>>>,
}

impl<T> Default for Handlers<T> {
    fn default() -> Self {
        Self {
            list: Mutex::new(Vec::new()),
        }
    }
}

impl<T> std::fmt::Debug for Handlers<T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Handlers")
            .field("subscribers", &self.len())
            .finish()
    }
}

impl<T> Handlers<T> {
    #[must_use]
    pub fn len(&self) -> usize {
        self.list
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .len()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

impl<T: Clone + Send + 'static> Handlers<T> {
    /// Add a subscriber.
    pub fn subscribe(&self, handler: impl Fn(T) -> BoxFuture<'static, ()> + Send + Sync + 'static) {
        self.list
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .push(Arc::new(handler));
    }

    /// Hand `value` to every subscriber in turn. The list is copied first, so a subscriber may
    /// subscribe another without deadlocking.
    pub async fn emit(&self, value: T) {
        let handlers: Vec<Handler<T>> = self
            .list
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone();
        for handler in handlers {
            handler(value.clone()).await;
        }
    }
}
