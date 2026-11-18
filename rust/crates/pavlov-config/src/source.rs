//! Where settings come from: the `.env` file, with the process environment over it.

use std::collections::HashMap;
use std::ffi::OsString;
use std::fmt;
use std::io;
use std::path::{Path, PathBuf};

use pavlov_core::text::case::fold_case;

use crate::dotenv;

/// Flat `UPPER_SNAKE` settings, looked up ignoring case like .NET's `IConfiguration`, so a
/// `.env` key and an environment variable that differ only in case are one setting, not two.
#[derive(Clone, Default)]
pub struct Config {
    values: HashMap<String, String>,
}

impl fmt::Debug for Config {
    /// The keys only: the values include the Discord token and the RCON passwords.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let mut keys: Vec<&String> = self.values.keys().collect();
        keys.sort();
        f.debug_struct("Config").field("keys", &keys).finish()
    }
}

/// The configuration could not be read at all.
#[derive(Debug)]
pub struct ConfigError {
    pub path: PathBuf,
    pub source: io::Error,
}

impl fmt::Display for ConfigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "{} could not be read: {}",
            self.path.display(),
            self.source
        )
    }
}

impl std::error::Error for ConfigError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.source)
    }
}

impl Config {
    /// Settings from explicit pairs, a later pair winning. For tests and tools.
    pub fn from_pairs<K: AsRef<str>, V: Into<String>>(
        pairs: impl IntoIterator<Item = (K, V)>,
    ) -> Self {
        let mut config = Self::default();
        for (key, value) in pairs {
            config.set(key.as_ref(), value);
        }
        config
    }

    /// Settings from environment variables, as `std::env::vars_os()` yields them. A name or
    /// value that is not UTF-8 is skipped: nothing this bot reads is spelled that way.
    pub fn from_environment(vars: impl IntoIterator<Item = (OsString, OsString)>) -> Self {
        Self::from_pairs(
            vars.into_iter().filter_map(|(key, value)| {
                Some((key.into_string().ok()?, value.into_string().ok()?))
            }),
        )
    }

    /// The `.env` at `env_path`, then `vars` over it: a real environment variable wins, which
    /// is what dotenv does and what lets a unit file override one setting.
    ///
    /// A missing `.env` is not an error - every setting can come from the environment.
    ///
    /// # Errors
    /// When the file exists but cannot be read.
    pub fn load(
        env_path: &Path,
        vars: impl IntoIterator<Item = (OsString, OsString)>,
    ) -> Result<Self, ConfigError> {
        let file = match std::fs::read_to_string(env_path) {
            Ok(contents) => dotenv::parse(&contents),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Self::default(),
            Err(source) => {
                return Err(ConfigError {
                    path: env_path.to_path_buf(),
                    source,
                });
            }
        };
        Ok(file.overlaid_with(Self::from_environment(vars)))
    }

    /// These settings with every one of `other` over them.
    #[must_use]
    pub fn overlaid_with(mut self, other: Self) -> Self {
        self.values.extend(other.values);
        self
    }

    /// The raw value of `key`, ignoring case. `Some("")` is "set to nothing", which is not the
    /// same as unset: an appended `PAYROLL_AMOUNT=` must beat an inherited figure.
    #[must_use]
    pub fn get(&self, key: &str) -> Option<&str> {
        self.values.get(&fold_case(key)).map(String::as_str)
    }

    /// Whether `key` is set at all, even to nothing.
    #[must_use]
    pub fn contains_key(&self, key: &str) -> bool {
        self.values.contains_key(&fold_case(key))
    }

    /// How many settings there are.
    #[must_use]
    pub fn len(&self) -> usize {
        self.values.len()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }

    pub(crate) fn set(&mut self, key: &str, value: impl Into<String>) {
        self.values.insert(fold_case(key), value.into());
    }
}
