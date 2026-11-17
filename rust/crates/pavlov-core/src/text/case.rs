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
}
