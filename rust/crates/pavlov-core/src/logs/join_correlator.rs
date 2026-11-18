//! Correlating an accepted connection with the login that follows it (C#
//! `Core/Logs/JoinCorrelator.cs`).
//!
//! Pavlov logs the address at accept time and the account at login time, on different lines,
//! with nothing shared between them, so the only link is proximity. THE RESULT IS A GUESS AND IS
//! LABELLED AS ONE: kept for display, NEVER used to flag an address or trigger a ban. Only a
//! [`super::ConfirmedPairing`] can do that.
//!
//! Two rules keep the guess honest:
//! - A WINDOW. An accept more than a few seconds before the login is unrelated to it.
//! - AMBIGUITY IS TRACKED, NOT RESOLVED. Every distinct address accepted since the last login
//!   is remembered; more than one means "we do not know", and the caller is told so.
//!
//! Per log file: two servers writing two logs have independent connection streams.

use std::collections::{HashMap, HashSet};

use chrono::{DateTime, TimeDelta, Utc};

/// The address for a login, and whether exactly one connection was pending.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct JoinGuess {
    pub ip: Option<String>,
    /// True only when EXACTLY ONE connection was pending: with two people connecting at once
    /// there is no way to tell which address is whose.
    pub confident: bool,
}

#[derive(Debug, Default)]
struct FileState {
    latest_ip: Option<String>,
    latest_at: Option<DateTime<Utc>>,
    pending_ips: HashSet<String>,
}

/// The per-file correlation state. Not shared between threads: the log tailer owns it.
#[derive(Debug)]
pub struct JoinCorrelator {
    files: HashMap<String, FileState>,
    window: TimeDelta,
}

impl Default for JoinCorrelator {
    /// Ten seconds: generous for a handshake, short enough that an idle connection does not
    /// linger into somebody else's join.
    fn default() -> Self {
        Self::new(TimeDelta::seconds(10))
    }
}

impl JoinCorrelator {
    #[must_use]
    pub fn new(window: TimeDelta) -> Self {
        Self {
            files: HashMap::new(),
            window,
        }
    }

    fn state(&mut self, file: &str) -> &mut FileState {
        self.files.entry(file.to_owned()).or_default()
    }

    fn within(latest: Option<DateTime<Utc>>, at: DateTime<Utc>, window: TimeDelta) -> bool {
        latest.is_some_and(|latest| at - latest <= window)
    }

    /// Record an accepted connection.
    pub fn accepted(&mut self, file: &str, ip: &str, at: DateTime<Utc>) {
        let window = self.window;
        let state = self.state(file);

        /* Expire the ambiguity set before adding. Without this, addresses from minutes ago
        keep it above one forever and every join reads as ambiguous - which looks like the
        feature is broken rather than cautious. */
        if !Self::within(state.latest_at, at, window) {
            state.pending_ips.clear();
        }

        state.latest_ip = Some(ip.to_owned());
        state.latest_at = Some(at);
        state.pending_ips.insert(ip.to_owned());
    }

    /// Resolve the address for a login, and CLEAR the pending state - except for a
    /// placeholder id: the real login for that same connection is still coming, and clearing
    /// here would leave it nothing to correlate against.
    pub fn login(&mut self, file: &str, at: DateTime<Utc>, is_placeholder_id: bool) -> JoinGuess {
        let window = self.window;
        let state = self.state(file);

        let guess = if Self::within(state.latest_at, at, window) {
            JoinGuess {
                ip: state.latest_ip.clone(),
                confident: state.pending_ips.len() == 1,
            }
        } else {
            JoinGuess::default()
        };

        if !is_placeholder_id {
            state.latest_ip = None;
            state.pending_ips.clear();
        }
        guess
    }

    /// How many distinct connections are currently unattributed. Diagnostic.
    #[must_use]
    pub fn pending_count(&self, file: &str) -> usize {
        self.files.get(file).map_or(0, |s| s.pending_ips.len())
    }

    /// Forget one file, or everything: used when a log is truncated or rotated.
    pub fn reset(&mut self, file: Option<&str>) {
        match file {
            None => self.files.clear(),
            Some(file) => {
                self.files.remove(file);
            }
        }
    }
}
