//! Which server a Pavlov.log belongs to, and the reverse (C# `Logs/ServerLabels.cs`).
//!
//! The install order IS the server numbering: the first log is server 1's.

use std::sync::{PoisonError, RwLock};

use pavlov_core::text::case::eq_ignore_case;

/// The log paths, in server order, assigned once the installs are known.
#[derive(Debug, Default)]
pub struct ServerLabels {
    paths: RwLock<Vec<String>>,
}

/// The 1-based server number of a log file.
#[must_use]
pub fn number_of(paths: &[String], file: &str) -> Option<usize> {
    paths
        .iter()
        .position(|p| eq_ignore_case(p, file))
        .map(|i| i + 1)
}

/// The log of an RCON server: `serverN` is the Nth path.
#[must_use]
pub fn log_for(paths: &[String], rcon_server: &str) -> Option<String> {
    let digits = rcon_server
        .get(..6)
        .filter(|p| p.eq_ignore_ascii_case("server"))
        .map(|_| &rcon_server[6..])?;
    // NumberStyles.None: digits only, no sign or space.
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let number: usize = digits.parse().ok()?;
    number.checked_sub(1).and_then(|i| paths.get(i)).cloned()
}

/// "Server N" for a known log, "the server" otherwise.
#[must_use]
pub fn label(paths: &[String], file: &str) -> String {
    number_of(paths, file).map_or_else(|| "the server".to_owned(), |n| format!("Server {n}"))
}

impl ServerLabels {
    /// Set the log paths, in server order.
    pub fn assign(&self, log_paths: Vec<String>) {
        *self.paths.write().unwrap_or_else(PoisonError::into_inner) = log_paths;
    }

    fn paths(&self) -> Vec<String> {
        self.paths
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    #[must_use]
    pub fn of(&self, file: &str) -> String {
        label(&self.paths(), file)
    }

    #[must_use]
    pub fn number_of(&self, file: &str) -> Option<usize> {
        number_of(&self.paths(), file)
    }

    #[must_use]
    pub fn log_for(&self, rcon_server: &str) -> Option<String> {
        log_for(&self.paths(), rcon_server)
    }
}
