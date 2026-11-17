//! Compile-once regular expressions.
//!
//! The patterns are string constants in the source, so a pattern that fails to compile is a
//! programming error, not a runtime condition: the first test that touches it fails. That is
//! the one place `expect` is allowed in production code, and it is confined to this macro.

/// A lazily compiled `regex::Regex`.
macro_rules! static_regex {
    ($vis:vis $name:ident = $pattern:expr) => {
        $vis static $name: std::sync::LazyLock<regex::Regex> = std::sync::LazyLock::new(|| {
            #[allow(clippy::expect_used)]
            regex::Regex::new($pattern).expect(concat!("constant pattern ", stringify!($name), " compiles"))
        });
    };
}

/// A lazily compiled `fancy_regex::Regex`, for the patterns the C# writes with lookaround,
/// which the `regex` crate does not support by design.
macro_rules! static_fancy_regex {
    ($vis:vis $name:ident = $pattern:expr) => {
        $vis static $name: std::sync::LazyLock<fancy_regex::Regex> = std::sync::LazyLock::new(|| {
            #[allow(clippy::expect_used)]
            fancy_regex::Regex::new($pattern).expect(concat!("constant pattern ", stringify!($name), " compiles"))
        });
    };
}

pub(crate) use static_fancy_regex;
pub(crate) use static_regex;
