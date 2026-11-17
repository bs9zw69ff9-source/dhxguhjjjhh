//! Address scrubbing for staff-facing text (C# `Core/Intelligence/Addresses.cs`).

use crate::patterns::static_regex;

static_regex!(
    IPV4 = r"\b(?:(?:25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\b"
);
static_regex!(IPV6 = r"\b(?:[0-9a-fA-F]{1,4}:){2,7}(?::|[0-9a-fA-F]{1,4})\b");

/// Namespace for the helpers, mirroring the C# static class.
#[derive(Debug)]
pub struct Addresses;

impl Addresses {
    /// What an address is replaced with.
    pub const PLACEHOLDER: &'static str = "[address withheld]";

    /// Replace every IPv4 and IPv6 address with [`Self::PLACEHOLDER`].
    #[must_use]
    pub fn scrub(text: Option<&str>) -> String {
        let text = text.unwrap_or("");
        if text.is_empty() {
            return String::new();
        }
        let scrubbed = IPV4.replace_all(text, Self::PLACEHOLDER);
        IPV6.replace_all(&scrubbed, Self::PLACEHOLDER).into_owned()
    }

    /// Whether the text holds an address.
    #[must_use]
    pub fn contains(text: Option<&str>) -> bool {
        text.is_some_and(|t| !t.is_empty() && (IPV4.is_match(t) || IPV6.is_match(t)))
    }
}
