//! Self-verification rules (C# `Core/Verification/VerificationRules.cs`).
//!
//! A Pavlov name is linked to a Discord user through their confirmed IPs: one account per name
//! in BOTH directions, and a confirmed IP already tied to a different verified account means an
//! alt. THE IP RULE HAS FALSE POSITIVES (housemates, a campus NAT) and that is understood: it
//! refuses SELF-verification, it does not ban, and staff can verify by hand.

use std::collections::HashSet;

use indexmap::IndexMap;

/// Why a verification was refused.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VerificationConflict {
    None,
    /// This Discord account is already verified.
    AlreadyVerified,
    /// That Pavlov name is verified to somebody else.
    NameTaken,
    /// A confirmed IP is already tied to a different verified account: an alt.
    Alt,
}

/// The verdict.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VerificationVerdict {
    pub conflict: VerificationConflict,
    /// The Discord account already holding the name or IP.
    pub other_id: Option<String>,
    pub reason: Option<String>,
}

impl VerificationVerdict {
    /// No conflict.
    #[must_use]
    pub fn allowed() -> Self {
        Self {
            conflict: VerificationConflict::None,
            other_id: None,
            reason: None,
        }
    }

    /// Whether verification may proceed.
    #[must_use]
    pub fn is_allowed(&self) -> bool {
        self.conflict == VerificationConflict::None
    }

    fn refused(conflict: VerificationConflict, other_id: &str, reason: String) -> Self {
        Self {
            conflict,
            other_id: Some(other_id.to_owned()),
            reason: Some(reason),
        }
    }
}

/// What one account is verified as.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VerificationRecord {
    /// The Pavlov name.
    pub name: String,
    /// Confirmed IPs, which is what makes alt detection possible at all.
    pub ips: Vec<String>,
}

/// Namespace for the rule, mirroring the C# static class.
#[derive(Debug)]
pub struct VerificationRules;

fn normalise(value: &str) -> String {
    value.trim().to_lowercase()
}

impl VerificationRules {
    /// Check a verification request against everything already verified.
    ///
    /// `store` is Discord id to record, IN STORED ORDER: when two accounts conflict for
    /// different reasons the first one found is reported, as the C# dictionary did.
    #[must_use]
    pub fn check(
        store: &IndexMap<String, VerificationRecord>,
        discord_id: &str,
        name: Option<&str>,
        ips: &[String],
    ) -> VerificationVerdict {
        if let Some(own) = store.get(discord_id) {
            return VerificationVerdict::refused(
                VerificationConflict::AlreadyVerified,
                discord_id,
                format!("You're already verified as `{}`.", own.name),
            );
        }

        let want_name = normalise(name.unwrap_or(""));
        let want_ips: HashSet<String> = ips
            .iter()
            .map(|ip| normalise(ip))
            .filter(|ip| !ip.is_empty())
            .collect();

        for (id, record) in store {
            if id == discord_id {
                continue;
            }
            if !want_name.is_empty() && normalise(&record.name) == want_name {
                return VerificationVerdict::refused(
                    VerificationConflict::NameTaken,
                    id,
                    format!(
                        "The name `{}` is already verified to another member.",
                        name.unwrap_or("")
                    ),
                );
            }
            if record
                .ips
                .iter()
                .any(|ip| want_ips.contains(&normalise(ip)))
            {
                return VerificationVerdict::refused(
                    VerificationConflict::Alt,
                    id,
                    "This looks like an alt account - a shared IP is already verified to another member.".to_owned(),
                );
            }
        }
        VerificationVerdict::allowed()
    }
}
