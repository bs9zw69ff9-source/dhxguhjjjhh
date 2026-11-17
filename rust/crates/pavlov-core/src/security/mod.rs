//! The compiled-in owner identity (C# `Core/Security/OwnerGuard.cs`).
//!
//! Two master owners and one master in-game name are built into the binary and pinned by a
//! SHA-256 fingerprint, so an edit to them (in source or by patching) stops the bot at startup
//! instead of silently handing the bot to somebody else. The resolved owner and master sets
//! must also still contain them after configuration is applied.

use std::collections::HashSet;

use sha2::{Digest, Sha256};

use crate::text::case::IgnoreCaseSet;

/// Why the bot refuses to start.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OwnerGuardError(pub String);

impl std::fmt::Display for OwnerGuardError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for OwnerGuardError {}

/// Namespace for the guard, mirroring the C# static class.
#[derive(Debug)]
pub struct OwnerGuard;

impl OwnerGuard {
    pub const SUPER_OWNER_ID: u64 = 1_014_251_293_159_731_310;
    pub const SECOND_MASTER_OWNER_ID: u64 = 307_052_224_087_851_009;
    pub const MASTER_NAME: &'static str = "fki6";
    /// SHA-256 of `"{SUPER_OWNER_ID}:{SECOND_MASTER_OWNER_ID}:{MASTER_NAME}"`, lowercase hex.
    pub const FINGERPRINT: &'static str =
        "13c2a29bdf4c59c87f22c9224dd9274f2a8fcd46b80caf5c868d0cb0a79380d7";

    /// The master owners: above the super owners added through `.env`.
    #[must_use]
    pub fn master_owner_ids() -> [u64; 2] {
        [Self::SUPER_OWNER_ID, Self::SECOND_MASTER_OWNER_ID]
    }

    #[must_use]
    pub fn is_master_owner(user_id: u64) -> bool {
        user_id == Self::SUPER_OWNER_ID || user_id == Self::SECOND_MASTER_OWNER_ID
    }

    /// The fingerprint of the built-in identity as compiled.
    #[must_use]
    pub fn compute() -> String {
        let digest = Sha256::digest(
            format!(
                "{}:{}:{}",
                Self::SUPER_OWNER_ID,
                Self::SECOND_MASTER_OWNER_ID,
                Self::MASTER_NAME
            )
            .as_bytes(),
        );
        digest.iter().map(|b| format!("{b:02x}")).collect()
    }

    /// Refuse to start when the built-in identity has been removed or edited.
    ///
    /// # Errors
    /// When an id or the name is missing, or the fingerprint does not match.
    pub fn verify() -> Result<(), OwnerGuardError> {
        if Self::SUPER_OWNER_ID == 0 || Self::SECOND_MASTER_OWNER_ID == 0 {
            return Err(OwnerGuardError(
                "a built-in master owner id has been removed".to_owned(),
            ));
        }
        if Self::MASTER_NAME.trim().is_empty() {
            return Err(OwnerGuardError(
                "the built-in master name has been removed".to_owned(),
            ));
        }
        let actual = Self::compute();
        if !constant_time_eq(actual.as_bytes(), Self::FINGERPRINT.as_bytes()) {
            return Err(OwnerGuardError(format!(
                "the built-in owner identity does not match its fingerprint - it has been edited. Expected {}, computed {actual}. The bot will not start.",
                Self::FINGERPRINT
            )));
        }
        Ok(())
    }

    /// The resolved super-owner set must still hold the built-in super owner.
    ///
    /// # Errors
    /// When the identity fails [`Self::verify`] or the super owner was filtered out.
    pub fn verify_owners(super_owners: &HashSet<u64>) -> Result<(), OwnerGuardError> {
        Self::verify()?;
        if !super_owners.contains(&Self::SUPER_OWNER_ID) {
            return Err(OwnerGuardError(format!(
                "the built-in super owner {} is missing from the resolved owner set. Something is filtering it out after it is added. The bot will not start.",
                Self::SUPER_OWNER_ID
            )));
        }
        Ok(())
    }

    /// The configured super owners plus the built-in one, verified.
    ///
    /// # Errors
    /// As [`Self::verify_owners`].
    pub fn with_built_in_owners(
        configured: impl IntoIterator<Item = u64>,
    ) -> Result<HashSet<u64>, OwnerGuardError> {
        let mut set: HashSet<u64> = configured.into_iter().collect();
        set.insert(Self::SUPER_OWNER_ID);
        Self::verify_owners(&set)?;
        Ok(set)
    }

    /// The resolved master-name list must still hold the built-in name.
    ///
    /// # Errors
    /// When the identity fails [`Self::verify`] or the name was filtered out.
    pub fn verify_masters(masters: &IgnoreCaseSet) -> Result<(), OwnerGuardError> {
        Self::verify()?;
        if !masters.contains(Self::MASTER_NAME) {
            return Err(OwnerGuardError(format!(
                "the built-in master name \"{}\" is missing from the resolved master list. The bot will not start.",
                Self::MASTER_NAME
            )));
        }
        Ok(())
    }

    /// The configured master names (trimmed, blanks dropped, case-insensitive) plus the
    /// built-in one, verified.
    ///
    /// # Errors
    /// As [`Self::verify_masters`].
    pub fn with_built_in_masters<'a>(
        configured: impl IntoIterator<Item = &'a str>,
    ) -> Result<IgnoreCaseSet, OwnerGuardError> {
        let mut set: IgnoreCaseSet = configured
            .into_iter()
            .map(str::trim)
            .filter(|n| !n.is_empty())
            .collect();
        set.insert(Self::MASTER_NAME);
        Self::verify_masters(&set)?;
        Ok(set)
    }
}

/// Compare without an early exit, as `CryptographicOperations.FixedTimeEquals` does.
fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0_u8, |acc, (x, y)| acc | (x ^ y)) == 0
}
