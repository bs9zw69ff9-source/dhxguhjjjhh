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

/// .NET `ToLowerInvariant`: the simple (one-to-one) lower-case mapping, so "İ" stays one
/// character where Rust's full `to_lowercase` would make it two.
#[must_use]
pub fn to_lower_invariant(s: &str) -> String {
    s.chars()
        .map(|c| {
            let mut lower = c.to_lowercase();
            match (lower.next(), lower.next()) {
                (Some(l), None) => l,
                _ => c,
            }
        })
        .collect()
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

/// `string.Compare(a, b, StringComparison.OrdinalIgnoreCase)`, as an ordering.
#[must_use]
pub fn cmp_ignore_case(a: &str, b: &str) -> std::cmp::Ordering {
    a.chars().map(simple_upper).cmp(b.chars().map(simple_upper))
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

/// A string-keyed map compared like `Dictionary<string, V>(StringComparer.OrdinalIgnoreCase)`.
///
/// THE COMPARER LIVES IN THE TYPE, which is the whole point. The C# had to remember to call
/// `SerializedStore.ReadMap` rather than `Read`, because JSON carries no comparer and a plain
/// read handed back an ORDINAL dictionary the moment the row existed: an exemption filed as
/// "Holosight1" stopped matching "holosight1", silently, on the enforcement path. Here a
/// dataset read as an `IgnoreCaseMap` is case-insensitive whichever way it was read.
///
/// Like the C# indexer, [`insert`](Self::insert) keeps the FIRST spelling of a key and
/// replaces its value, so a round trip preserves the stored spelling and order. Serialises as
/// a JSON object.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IgnoreCaseMap<V> {
    entries: indexmap::IndexMap<String, (String, V)>,
}

impl<V> Default for IgnoreCaseMap<V> {
    fn default() -> Self {
        Self {
            entries: indexmap::IndexMap::new(),
        }
    }
}

impl<V> IgnoreCaseMap<V> {
    /// An empty map.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Set `key` to `value`, returning the value it replaced. An existing key keeps the
    /// spelling it was first written with.
    pub fn insert(&mut self, key: impl Into<String>, value: V) -> Option<V> {
        let key = key.into();
        match self.entries.entry(fold_case(&key)) {
            indexmap::map::Entry::Occupied(mut slot) => {
                Some(std::mem::replace(&mut slot.get_mut().1, value))
            }
            indexmap::map::Entry::Vacant(slot) => {
                slot.insert((key, value));
                None
            }
        }
    }

    /// The value under `key`, ignoring case.
    #[must_use]
    pub fn get(&self, key: &str) -> Option<&V> {
        self.entries.get(&fold_case(key)).map(|(_, v)| v)
    }

    /// The value under `key`, ignoring case, for changing in place.
    pub fn get_mut(&mut self, key: &str) -> Option<&mut V> {
        self.entries.get_mut(&fold_case(key)).map(|(_, v)| v)
    }

    /// Whether a key equal to `key` (ignoring case) is present.
    #[must_use]
    pub fn contains_key(&self, key: &str) -> bool {
        self.entries.contains_key(&fold_case(key))
    }

    /// Remove the entry whose key equals `key` ignoring case, keeping the others in order.
    pub fn remove(&mut self, key: &str) -> Option<V> {
        self.entries.shift_remove(&fold_case(key)).map(|(_, v)| v)
    }

    /// The entries as `(spelling, value)`, in insertion order.
    pub fn iter(&self) -> impl Iterator<Item = (&str, &V)> {
        self.entries.values().map(|(k, v)| (k.as_str(), v))
    }

    /// The keys as first written, in insertion order.
    pub fn keys(&self) -> impl Iterator<Item = &str> {
        self.entries.values().map(|(k, _)| k.as_str())
    }

    /// The values, in insertion order.
    pub fn values(&self) -> impl Iterator<Item = &V> {
        self.entries.values().map(|(_, v)| v)
    }

    #[must_use]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
}

impl<K: Into<String>, V> FromIterator<(K, V)> for IgnoreCaseMap<V> {
    fn from_iter<I: IntoIterator<Item = (K, V)>>(iter: I) -> Self {
        let mut map = Self::new();
        for (key, value) in iter {
            map.insert(key, value);
        }
        map
    }
}

impl<V: serde::Serialize> serde::Serialize for IgnoreCaseMap<V> {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_map(self.iter())
    }
}

impl<'de, V: serde::Deserialize<'de>> serde::Deserialize<'de> for IgnoreCaseMap<V> {
    /// A JSON object; `null` reads as empty.
    ///
    /// KEYS THAT DIFFER ONLY BY CASE ARE MERGED: the first spelling, the last value. The C#
    /// threw here instead (the copy into an `OrdinalIgnoreCase` dictionary rejects the
    /// duplicate), and such a row is exactly what the old ordinal-read bug could leave behind,
    /// so throwing turned one bad row into a dataset nothing could read.
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let entries =
            Option::<indexmap::IndexMap<String, V>>::deserialize(deserializer)?.unwrap_or_default();
        Ok(entries.into_iter().collect())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_ignore_case_map_keeps_the_first_spelling_and_takes_the_last_value() {
        let mut map: IgnoreCaseMap<i32> = [("Holosight1", 1), ("bob", 2)].into_iter().collect();
        assert_eq!(map.insert("HOLOSIGHT1", 3), Some(1));
        assert_eq!(map.get("holosight1"), Some(&3));
        assert_eq!(map.keys().collect::<Vec<_>>(), ["Holosight1", "bob"]);
        assert_eq!(
            serde_json::to_string(&map).unwrap(),
            r#"{"Holosight1":3,"bob":2}"#
        );

        let merged: IgnoreCaseMap<i32> =
            serde_json::from_str(r#"{"Alice":1,"ALICE":2,"Bob":3}"#).unwrap();
        assert_eq!(merged.len(), 2);
        assert_eq!(merged.iter().next(), Some(("Alice", &2)));
        assert!(
            serde_json::from_str::<IgnoreCaseMap<i32>>("null")
                .unwrap()
                .is_empty()
        );

        assert_eq!(map.remove("BOB"), Some(2));
        assert!(!map.contains_key("bob"));
    }

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
