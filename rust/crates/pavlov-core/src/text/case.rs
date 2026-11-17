//! .NET's `StringComparison.OrdinalIgnoreCase`, which the C# uses for names everywhere.
//!
//! It compares char by char under SIMPLE upper-case mapping. Rust's `to_uppercase` is the FULL
//! mapping ("ß" becomes "SS"), under which "straße" and "STRASSE" would compare equal and the
//! .NET rule says they do not. These helpers keep the .NET answer.

/// The simple (one-to-one) upper-case mapping of `c`, or `c` itself when the full mapping is
/// not a single character.
fn simple_upper(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(u), None) => u,
        _ => c,
    }
}

/// `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`.
#[must_use]
pub fn eq_ignore_case(a: &str, b: &str) -> bool {
    a.chars().count() == b.chars().count()
        && a.chars()
            .zip(b.chars())
            .all(|(x, y)| x == y || simple_upper(x) == simple_upper(y))
}

/// A key that compares and hashes like `StringComparer.OrdinalIgnoreCase`: two strings equal
/// under [`eq_ignore_case`] fold to the same key.
#[must_use]
pub fn fold_case(s: &str) -> String {
    s.chars().map(simple_upper).collect()
}

/// `haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)`.
#[must_use]
pub fn contains_ignore_case(haystack: &str, needle: &str) -> bool {
    fold_case(haystack).contains(&fold_case(needle))
}

/// `haystack.StartsWith(needle, StringComparison.OrdinalIgnoreCase)`.
#[must_use]
pub fn starts_with_ignore_case(haystack: &str, needle: &str) -> bool {
    fold_case(haystack).starts_with(&fold_case(needle))
}

/// A set of strings compared like `HashSet<string>(StringComparer.OrdinalIgnoreCase)`.
///
/// Keeps each value as FIRST written (so a round trip preserves the stored spelling) and its
/// insertion order (so a round trip preserves the stored order). Serialises as a JSON array.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct IgnoreCaseSet {
    values: indexmap::IndexMap<String, String>,
}

impl IgnoreCaseSet {
    /// An empty set.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Add `value`; `false` when an equal value (ignoring case) was already present.
    pub fn insert(&mut self, value: impl Into<String>) -> bool {
        let value = value.into();
        let key = fold_case(&value);
        if self.values.contains_key(&key) {
            return false;
        }
        self.values.insert(key, value);
        true
    }

    /// Whether an equal value (ignoring case) is present.
    #[must_use]
    pub fn contains(&self, value: &str) -> bool {
        self.values.contains_key(&fold_case(value))
    }

    /// Remove the value equal to `value` ignoring case; `true` when one was removed.
    pub fn remove(&mut self, value: &str) -> bool {
        self.values.shift_remove(&fold_case(value)).is_some()
    }

    /// The stored values, in insertion order.
    pub fn iter(&self) -> impl Iterator<Item = &str> {
        self.values.values().map(String::as_str)
    }

    #[must_use]
    pub fn len(&self) -> usize {
        self.values.len()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }
}

impl<S: Into<String>> FromIterator<S> for IgnoreCaseSet {
    fn from_iter<I: IntoIterator<Item = S>>(iter: I) -> Self {
        let mut set = Self::new();
        for value in iter {
            set.insert(value);
        }
        set
    }
}

impl serde::Serialize for IgnoreCaseSet {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_seq(self.iter())
    }
}

impl<'de> serde::Deserialize<'de> for IgnoreCaseSet {
    /// An array of strings; `null` reads as empty, and a non-string entry is skipped rather
    /// than failing the set.
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let values =
            Option::<Vec<serde_json::Value>>::deserialize(deserializer)?.unwrap_or_default();
        Ok(values
            .into_iter()
            .filter_map(|v| v.as_str().map(str::to_owned))
            .collect())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matches_dotnet_ordinal_ignore_case() {
        assert!(eq_ignore_case("Cheater", "cheater"));
        assert!(eq_ignore_case("ÉCOLE", "école"));
        assert!(!eq_ignore_case("straße", "STRASSE"));
        assert!(!eq_ignore_case("a", "ab"));
        assert!(contains_ignore_case(
            "Ban evasion - Blacklisted IP 1.2.3.4",
            "blacklisted ip"
        ));
        assert!(starts_with_ignore_case("AUTO-BAN: x", "auto-ban"));
        assert_eq!(fold_case("Alice"), fold_case("aLICE"));
    }

    #[test]
    fn an_ignore_case_set_keeps_the_first_spelling_and_the_order() {
        let mut set: IgnoreCaseSet = ["Bob", "alice", "ALICE"].into_iter().collect();
        assert_eq!(set.len(), 2);
        assert!(set.contains("BOB"));
        assert_eq!(set.iter().collect::<Vec<_>>(), ["Bob", "alice"]);
        assert!(!set.insert("bob"));
        assert!(set.remove("Alice"));
        assert_eq!(serde_json::to_string(&set).unwrap(), r#"["Bob"]"#);
        let back: IgnoreCaseSet = serde_json::from_str(r#"["X", 7, "x", null]"#).unwrap();
        assert_eq!(back.iter().collect::<Vec<_>>(), ["X"]);
    }
}
