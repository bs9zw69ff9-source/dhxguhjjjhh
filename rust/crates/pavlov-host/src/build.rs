//! Which build is running (C# `HealthCommand.BuildStamp`). Logged at startup and shown by
//! `/health`, so "is my fix deployed?" is answered without a shell: an unchanged stamp after a
//! deploy means the process was never replaced.

/// The commit `scripts/deploy-rust.sh` stamped in at compile time (`PAVLOV_BUILD_STAMP`), or
/// the crate version marked as a local build when nothing was stamped.
#[must_use]
pub fn stamp() -> String {
    match option_env!("PAVLOV_BUILD_STAMP")
        .map(str::trim)
        .filter(|s| !s.is_empty())
    {
        Some(stamp) => stamp.to_owned(),
        None => format!("local-{}", env!("CARGO_PKG_VERSION")),
    }
}
