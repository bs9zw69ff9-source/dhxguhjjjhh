//! A button or select menu's custom id: a prefix that routes it and its arguments (C#
//! `Discord/IComponentHandler.cs` `ComponentId`). Pure, so the routing is testable without a
//! gateway.
//!
//! - An id over Discord's 100 characters is REFUSED, never truncated: a truncated id still
//!   looks valid and routes to the wrong handler, or to none.
//! - [`ComponentId::parse`] never fails, and a missing argument is `None`: buttons posted by an
//!   older build may carry fewer arguments than today's handler expects.
//! - The Node bot's PERSISTENT panel buttons (`verify_start`, `menu_start` and their modals)
//!   are translated so they still route after the cutover. Transient controls and the staff
//!   decision buttons are not: their state died with the bot that made them.

/// Between the prefix and each argument.
pub const SEPARATOR: char = ':';
/// Discord's limit on a custom id.
pub const MAX_LENGTH: usize = 100;

/// Why an id could not be encoded.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComponentIdError(pub String);

impl std::fmt::Display for ComponentIdError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for ComponentIdError {}

/// A parsed custom id.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComponentId {
    pub prefix: String,
    pub arguments: Vec<String>,
}

impl ComponentId {
    /// The argument at `index`, or `None` past the end.
    #[must_use]
    pub fn argument(&self, index: usize) -> Option<&str> {
        self.arguments.get(index).map(String::as_str)
    }

    /// The argument at `index` as a number, or `fallback`.
    #[must_use]
    pub fn argument_as_int(&self, index: usize, fallback: i32) -> i32 {
        self.argument(index)
            .and_then(|a| a.parse().ok())
            .unwrap_or(fallback)
    }

    /// `prefix:arg1:arg2`, or just `prefix`.
    ///
    /// # Errors
    /// A blank prefix, a prefix containing the separator (it would parse back as a different
    /// prefix), or an id over [`MAX_LENGTH`]. Callers encoding user text must shorten it
    /// themselves, where they know what is safe to lose.
    pub fn encode(prefix: &str, arguments: &[&str]) -> Result<String, ComponentIdError> {
        if prefix.trim().is_empty() {
            return Err(ComponentIdError("a component id needs a prefix".into()));
        }
        if prefix.contains(SEPARATOR) {
            return Err(ComponentIdError(format!(
                "a prefix may not contain '{SEPARATOR}'"
            )));
        }
        let mut encoded = prefix.to_owned();
        for argument in arguments {
            encoded.push(SEPARATOR);
            encoded.push_str(argument);
        }
        // Discord counts UTF-16 units; for the ASCII ids this bot makes, that is chars.
        let length = encoded.encode_utf16().count();
        if length > MAX_LENGTH {
            return Err(ComponentIdError(format!(
                "custom id '{prefix}' is {length} characters; Discord's limit is {MAX_LENGTH}"
            )));
        }
        Ok(encoded)
    }

    /// Split a custom id. Never fails: an argument containing the separator splits into more.
    #[must_use]
    pub fn parse(custom_id: &str) -> Self {
        let translated = translate(custom_id);
        let mut parts = translated.split(SEPARATOR).map(str::to_owned);
        let prefix = parts.next().unwrap_or_default();
        Self {
            prefix,
            arguments: parts.collect(),
        }
    }
}

/// The Node bot's persistent panel ids, mapped onto ours.
fn translate(custom_id: &str) -> String {
    match custom_id {
        "verify_start" => format!("verify{SEPARATOR}start"),
        "verify_modal" => format!("verify{SEPARATOR}submit"),
        "menu_start" => format!("menu{SEPARATOR}start"),
        "menu_modal" => format!("menu{SEPARATOR}submit"),
        other => other.to_owned(),
    }
}
