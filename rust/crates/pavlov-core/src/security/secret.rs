//! A value that must never reach a log line by accident.

use std::fmt;

/// A token, password, API key or webhook URL.
///
/// `Debug` and `Display` print `***`, so a struct holding one can derive `Debug` and be logged
/// whole without leaking it. The C# records had no such guard: a record's generated
/// `ToString` prints every property, the Discord token included. The value is reachable only
/// through [`expose`](Self::expose), which makes every use of it a visible, greppable choice.
#[derive(Clone, PartialEq, Eq)]
pub struct Secret(String);

impl Secret {
    /// Wrap `value`.
    #[must_use]
    pub fn new(value: impl Into<String>) -> Self {
        Self(value.into())
    }

    /// The value itself. Only for handing it to the thing that needs it - a login, a request
    /// header, the RCON handshake - never for printing.
    #[must_use]
    pub fn expose(&self) -> &str {
        &self.0
    }

    /// Whether the value is empty or only whitespace.
    #[must_use]
    pub fn is_blank(&self) -> bool {
        self.0.trim().is_empty()
    }
}

impl fmt::Debug for Secret {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("Secret(***)")
    }
}

impl fmt::Display for Secret {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("***")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_secret_never_prints_its_value() {
        let token = Secret::new("mfa.abc123");
        assert_eq!(format!("{token:?}"), "Secret(***)");
        assert_eq!(token.to_string(), "***");
        assert_eq!(token.expose(), "mfa.abc123");
        assert!(!format!("{:?}", Some(&token)).contains("abc"));
        assert!(Secret::new("  ").is_blank());
    }
}
