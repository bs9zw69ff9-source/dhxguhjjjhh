//! The last few command failures, by the correlation id the user was shown (C#
//! `Observability/RecentErrors.cs`), so "it said error ada985e1" can be looked up.

use std::collections::VecDeque;
use std::sync::{Mutex, PoisonError};

use chrono::{DateTime, Utc};
use pavlov_core::text::case::eq_ignore_case;

/// How many are kept.
pub const CAPACITY: usize = 25;

/// One recorded failure.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RecordedError {
    pub correlation_id: String,
    pub operation: String,
    pub user_id: u64,
    pub at: DateTime<Utc>,
    /// The error's type name.
    pub kind: String,
    pub message: String,
    /// The error's source chain and where it was recorded.
    pub trace: String,
}

/// The buffer.
#[derive(Debug, Default)]
pub struct RecentErrors {
    errors: Mutex<VecDeque<RecordedError>>,
}

impl RecentErrors {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Record a failure. Enqueue and trim under one lock, so the cap holds under concurrency.
    pub fn record<E: std::error::Error>(
        &self,
        correlation_id: &str,
        operation: &str,
        user_id: u64,
        error: &E,
    ) {
        let full = std::any::type_name::<E>();
        let mut trace = error.to_string();
        let mut source = error.source();
        while let Some(cause) = source {
            trace.push_str(&format!("\ncaused by: {cause}"));
            source = cause.source();
        }
        trace.push_str(&format!("\n{}", std::backtrace::Backtrace::force_capture()));
        let entry = RecordedError {
            correlation_id: correlation_id.to_owned(),
            operation: operation.to_owned(),
            user_id,
            at: Utc::now(),
            kind: full.rsplit("::").next().unwrap_or(full).to_owned(),
            message: error.to_string(),
            trace,
        };
        let mut errors = self.errors.lock().unwrap_or_else(PoisonError::into_inner);
        errors.push_back(entry);
        while errors.len() > CAPACITY {
            errors.pop_front();
        }
    }

    /// Newest first.
    #[must_use]
    pub fn all(&self) -> Vec<RecordedError> {
        self.errors
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .iter()
            .rev()
            .cloned()
            .collect()
    }

    /// By id, forgiving about case and surrounding spaces. `None` for blank or unknown.
    #[must_use]
    pub fn find(&self, correlation_id: Option<&str>) -> Option<RecordedError> {
        let id = correlation_id.map(str::trim).filter(|id| !id.is_empty())?;
        self.errors
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .iter()
            .find(|e| eq_ignore_case(&e.correlation_id, id))
            .cloned()
    }
}
