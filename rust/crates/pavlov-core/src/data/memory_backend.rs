//! An in-memory [`KeyValueBackend`]: the store's behaviour without a database.

use std::collections::HashMap;
use std::io;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Mutex, PoisonError};

use super::KeyValueBackend;

/// Datasets held in memory, for tests and for `--selftest`, which must build the whole
/// object graph without touching the real database.
#[derive(Debug, Default)]
pub struct MemoryBackend {
    data: Mutex<HashMap<String, String>>,
    writes: AtomicUsize,
}

impl MemoryBackend {
    /// An empty backend.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Store `json` under `key` directly, without counting it as a write.
    pub fn seed(&self, key: &str, json: &str) {
        self.data
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(key.to_owned(), json.to_owned());
    }

    /// How many times [`KeyValueBackend::write`] has been called.
    #[must_use]
    pub fn writes(&self) -> usize {
        self.writes.load(Ordering::SeqCst)
    }
}

impl KeyValueBackend for MemoryBackend {
    fn read(&self, key: &str) -> io::Result<Option<String>> {
        Ok(self
            .data
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .get(key)
            .cloned())
    }

    fn write(&self, key: &str, json: &str) -> io::Result<()> {
        self.writes.fetch_add(1, Ordering::SeqCst);
        self.data
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(key.to_owned(), json.to_owned());
        Ok(())
    }
}
