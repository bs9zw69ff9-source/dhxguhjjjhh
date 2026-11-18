//! Read-modify-write over named JSON datasets, serialised per key (C# `Core/Data/SerializedStore.cs`).
//!
//! Every mutation the bot makes is read-modify-write: load the ban list, add one, save it. Run
//! two of those concurrently against the same dataset and one silently overwrites the other -
//! a ban that "didn't take", with no error anywhere. Serialising per key is what prevents it.
//!
//! The properties carried over from the C# (and, before it, the Node bot):
//!
//! - A FAILED UPDATE MUST NOT POISON THE QUEUE. A mutator that returns an error leaves the
//!   dataset as it was and the next caller runs normally. A mutator that PANICS cannot wedge
//!   it either: tokio's mutex has no poisoning, and its guard is released during unwinding.
//! - READS ARE ISOLATED. Every read deserialises a fresh value, so a caller changing what it
//!   read cannot corrupt state for anybody else.
//! - AN UNREADABLE DATASET IS NEVER WRITTEN OVER. It reads as the fallback, so one bad
//!   character cannot take down every command that touches it, but an update refuses rather
//!   than replacing the whole dataset with one entry.
//!
//! Locking is per key: two different datasets never wait on each other.
//!
//! THE BACKEND IS SYNCHRONOUS, as in the C#, and is called while the dataset's lock is held.
//! The datasets are small and the SQLite backend (M4) writes one row, so this is the same
//! trade the C# makes; a backend that could block for long belongs on `spawn_blocking`.

use std::collections::HashMap;
use std::convert::Infallible;
use std::fmt;
use std::io;
use std::sync::{Arc, Mutex, PoisonError};

use indexmap::IndexSet;
use serde::Serialize;
use serde::de::DeserializeOwned;

/// Backing storage for [`SerializedStore`]: JSON text keyed by dataset name.
pub trait KeyValueBackend: Send + Sync {
    /// The stored text for `key`, or `None` when nothing is stored.
    ///
    /// # Errors
    /// When the storage itself cannot be read.
    fn read(&self, key: &str) -> io::Result<Option<String>>;

    /// Replace the stored text for `key`.
    ///
    /// # Errors
    /// When the storage itself cannot be written.
    fn write(&self, key: &str, json: &str) -> io::Result<()>;
}

/// Why the store could not do what it was asked. Never a reason the CALLER gave (a veto or a
/// mutator error): those come back in [`UpdateResult`].
#[derive(Debug)]
pub enum StoreError {
    /// A dataset name that is empty or only whitespace: a bug at the call site.
    BlankKey,
    /// The backend failed to read or write.
    Backend { key: String, source: io::Error },
    /// The value could not be turned into JSON.
    Encode {
        key: String,
        source: serde_json::Error,
    },
}

impl fmt::Display for StoreError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::BlankKey => f.write_str("a dataset name must not be blank"),
            Self::Backend { key, source } => {
                write!(f, "dataset \"{key}\" could not be accessed: {source}")
            }
            Self::Encode { key, source } => {
                write!(
                    f,
                    "dataset \"{key}\" could not be encoded as JSON: {source}"
                )
            }
        }
    }
}

impl std::error::Error for StoreError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::BlankKey => None,
            Self::Backend { source, .. } => Some(source),
            Self::Encode { source, .. } => Some(source),
        }
    }
}

/// The outcome of an update the store was able to attempt.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UpdateResult<T> {
    /// False when the mutator vetoed or failed, or the dataset is unreadable; nothing was
    /// written in any of those cases.
    pub ok: bool,
    /// The value now stored (the fallback when nothing is).
    pub value: T,
    /// Why the update did not apply, when it did not.
    pub error: Option<String>,
}

impl<T> UpdateResult<T> {
    /// The mutator declined on purpose - nothing went wrong.
    #[must_use]
    pub fn is_vetoed(&self) -> bool {
        self.error.as_deref() == Some(SerializedStore::VETOED)
    }
}

/// What a dataset holds, as far as a write is concerned.
enum Loaded<T> {
    /// Nothing stored, or a bare JSON `null`.
    Absent,
    /// Parsed, with the text it came from.
    Value(T, String),
    /// Text is stored and will not parse. Not the same as absent: it must not be written over.
    Unreadable,
}

type UnreadableHook = Box<dyn Fn(&str) + Send + Sync>;

/// Read-modify-write over named JSON datasets. See the module documentation.
pub struct SerializedStore {
    backend: Arc<dyn KeyValueBackend>,
    gates: Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
    unreadable: Mutex<IndexSet<String>>,
    on_unreadable: Option<UnreadableHook>,
}

impl fmt::Debug for SerializedStore {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("SerializedStore")
            .field("unreadable", &self.unreadable())
            .finish_non_exhaustive()
    }
}

impl SerializedStore {
    /// The [`UpdateResult::error`] of an update the mutator declined.
    pub const VETOED: &'static str = "vetoed by mutator";

    /// A store over `backend`.
    #[must_use]
    pub fn new(backend: Arc<dyn KeyValueBackend>) -> Self {
        Self {
            backend,
            gates: Mutex::new(HashMap::new()),
            unreadable: Mutex::new(IndexSet::new()),
            on_unreadable: None,
        }
    }

    /// Be told the name of a dataset that holds data but will not parse - once, until it
    /// parses again. The host logs it at error level: a dataset in that state can no longer
    /// be changed.
    #[must_use]
    pub fn with_unreadable_hook(mut self, hook: impl Fn(&str) + Send + Sync + 'static) -> Self {
        self.on_unreadable = Some(Box::new(hook));
        self
    }

    /// Datasets currently holding data that does not parse, in the order they were found.
    #[must_use]
    pub fn unreadable(&self) -> Vec<String> {
        self.unreadable
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .iter()
            .cloned()
            .collect()
    }

    /// The stored text for a key, unparsed.
    ///
    /// FOR TELLING "ABSENT" FROM "UNREADABLE", and nothing else. [`read`](Self::read) maps both
    /// to the fallback on purpose, which makes a row full of unparseable text look exactly like
    /// an empty one to anybody reporting what is stored. This is the only way to say which.
    ///
    /// # Errors
    /// When the backend cannot be read.
    pub fn read_raw(&self, key: &str) -> Result<Option<String>, StoreError> {
        Self::check_key(key)?;
        self.backend
            .read(key)
            .map_err(|source| StoreError::Backend {
                key: key.to_owned(),
                source,
            })
    }

    /// Read a dataset, or `fallback` when it is absent or unreadable.
    ///
    /// A corrupt dataset returns the fallback rather than failing, so a single bad character
    /// cannot take down every command that touches it. It is REPORTED, though (see
    /// [`with_unreadable_hook`](Self::with_unreadable_hook)), and [`update`](Self::update)
    /// will not write over it.
    ///
    /// # Errors
    /// When the backend cannot be read - which is not the same as the data being bad.
    pub fn read<T: DeserializeOwned>(&self, key: &str, fallback: T) -> Result<T, StoreError> {
        Ok(match self.load(key)? {
            Loaded::Value(value, _) => value,
            Loaded::Absent | Loaded::Unreadable => fallback,
        })
    }

    /// Apply `mutate` to the current value and persist what it returns; `None` is a veto.
    /// Serialised against every other update and write to the same key.
    ///
    /// `fallback` makes the value used when nothing is stored. It is a function because the
    /// result must report the value stored AFTER a veto, and a moved-in fallback would be gone.
    ///
    /// # Errors
    /// When the backend fails or the new value cannot be encoded. A veto and an unreadable
    /// dataset are not errors: see [`UpdateResult`].
    pub async fn update<T, F>(
        &self,
        key: &str,
        fallback: impl Fn() -> T,
        mutate: F,
    ) -> Result<UpdateResult<T>, StoreError>
    where
        T: Serialize + DeserializeOwned,
        F: FnOnce(T) -> Option<T>,
    {
        self.try_update(key, fallback, |current| {
            Ok::<_, Infallible>(mutate(current))
        })
        .await
    }

    /// [`update`](Self::update) for a mutator that can fail. A failure leaves the dataset
    /// exactly as it was, and its message becomes [`UpdateResult::error`].
    ///
    /// # Errors
    /// As for [`update`](Self::update).
    pub async fn try_update<T, F, E>(
        &self,
        key: &str,
        fallback: impl Fn() -> T,
        mutate: F,
    ) -> Result<UpdateResult<T>, StoreError>
    where
        T: Serialize + DeserializeOwned,
        F: FnOnce(T) -> Result<Option<T>, E>,
        E: fmt::Display,
    {
        Self::check_key(key)?;
        let gate = self.gate(key);
        // Dropping this future while it waits releases nothing it did not take, and nothing
        // below awaits while the lock is held, so cancellation cannot wedge the dataset.
        let _held = gate.lock().await;

        let (current, stored) = match self.load::<T>(key)? {
            Loaded::Unreadable => {
                return Ok(UpdateResult {
                    ok: false,
                    value: fallback(),
                    error: Some(format!(
                        "dataset \"{key}\" holds data that cannot be read, so it was not changed - fix or restore it"
                    )),
                });
            }
            Loaded::Absent => (fallback(), None),
            Loaded::Value(value, text) => (value, Some(text)),
        };

        // What is stored if this update does not apply: the stored text again, not whatever
        // the mutator left behind in the value it was handed.
        let unchanged = |error: String| -> UpdateResult<T> {
            let value = stored
                .as_deref()
                .and_then(|text| serde_json::from_str(text).ok())
                .unwrap_or_else(&fallback);
            UpdateResult {
                ok: false,
                value,
                error: Some(error),
            }
        };

        match mutate(current) {
            Err(error) => Ok(unchanged(error.to_string())),
            Ok(None) => Ok(unchanged(Self::VETOED.to_owned())),
            Ok(Some(next)) => {
                self.write_unlocked(key, &next)?;
                Ok(UpdateResult {
                    ok: true,
                    value: next,
                    error: None,
                })
            }
        }
    }

    /// Overwrite a dataset outright. Prefer [`update`](Self::update) for read-modify-write.
    ///
    /// # Errors
    /// When the value cannot be encoded or the backend fails.
    pub async fn write<T: Serialize>(&self, key: &str, value: &T) -> Result<(), StoreError> {
        Self::check_key(key)?;
        let gate = self.gate(key);
        let _held = gate.lock().await;
        self.write_unlocked(key, value)
    }

    fn check_key(key: &str) -> Result<(), StoreError> {
        if key.trim().is_empty() {
            Err(StoreError::BlankKey)
        } else {
            Ok(())
        }
    }

    fn gate(&self, key: &str) -> Arc<tokio::sync::Mutex<()>> {
        let mut gates = self.gates.lock().unwrap_or_else(PoisonError::into_inner);
        Arc::clone(gates.entry(key.to_owned()).or_default())
    }

    /// Indented, as the C# writes it: these rows are read and hand-edited during incidents.
    fn write_unlocked<T: Serialize>(&self, key: &str, value: &T) -> Result<(), StoreError> {
        let json = serde_json::to_string_pretty(value).map_err(|source| StoreError::Encode {
            key: key.to_owned(),
            source,
        })?;
        self.backend
            .write(key, &json)
            .map_err(|source| StoreError::Backend {
                key: key.to_owned(),
                source,
            })
    }

    /// Parse a dataset. Absent (or a bare `null`) is NOT unreadable, and the difference
    /// decides whether a write is safe.
    fn load<T: DeserializeOwned>(&self, key: &str) -> Result<Loaded<T>, StoreError> {
        let Some(text) = self.read_raw(key)? else {
            return Ok(Loaded::Absent);
        };
        let trimmed = text.trim();
        if trimmed.is_empty() || trimmed == "null" {
            return Ok(Loaded::Absent);
        }

        if let Ok(value) = serde_json::from_str::<T>(&text) {
            self.unreadable
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .shift_remove(key);
            return Ok(Loaded::Value(value, text));
        }

        let newly = self
            .unreadable
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(key.to_owned());
        // Outside the lock, so a hook that asks the store anything cannot deadlock it.
        if newly {
            if let Some(hook) = &self.on_unreadable {
                hook(key);
            }
        }
        Ok(Loaded::Unreadable)
    }
}
