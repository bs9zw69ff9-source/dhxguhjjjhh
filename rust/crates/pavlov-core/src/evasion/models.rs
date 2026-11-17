//! The evasion data types (C# `Core/Evasion/EvasionModels.cs`).

use std::collections::HashSet;

use chrono::{DateTime, Utc};
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::json::{epoch_ms_opt, null_as_default};

/// How strongly one signal implicates a returning banned player. Additive, so no single weak
/// signal reaches the match threshold alone. The values are the Node bot's, unchanged, and
/// the weight IS the variant so the two cannot drift apart.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum SignalKind {
    /// The same EOS account. Definitive.
    Eos = 100,
    /// The join IP is in a ban's recorded addresses. Very strong.
    Ip = 70,
    /// The name matches a banned name or a known shared-IP alias.
    Username = 50,
    /// Same consumer VPN brand as a banned account.
    Provider = 25,
    /// Same ASN AND both sides on a VPN or hosting network.
    AsnVpn = 20,
    /// Same autonomous system. The weakest by far, so it never stands alone.
    Asn = 10,
}

impl SignalKind {
    /// The weight this signal adds.
    #[must_use]
    pub fn weight(self) -> i32 {
        self as i32
    }
}

/// One reason a join was correlated to a ban, carrying its own weight.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EvasionReason {
    pub kind: SignalKind,
    pub detail: String,
}

impl EvasionReason {
    /// The weight of [`Self::kind`].
    #[must_use]
    pub fn weight(&self) -> i32 {
        self.kind.weight()
    }
}

/// The network fingerprint captured at ban time and stored with the ban, so a later join can
/// still be correlated after the VPN cache entry for that address has expired.
///
/// Every anonymising flag is `Option<bool>` on purpose: `None` is "no detector could answer",
/// a different fact from `false`. Conflating them is what made the Node bot ban on a single
/// unconfirmed hit.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct BanNetwork {
    #[serde(deserialize_with = "null_as_default")]
    pub eos_ids: Vec<String>,
    #[serde(deserialize_with = "null_as_default")]
    pub ips: Vec<String>,
    #[serde(deserialize_with = "null_as_default")]
    pub names: Vec<String>,
    pub asn: Option<String>,
    pub organization: Option<String>,
    pub provider: Option<String>,
    pub vpn: Option<bool>,
    pub proxy: Option<bool>,
    pub tor: Option<bool>,
    pub hosting: Option<bool>,
    pub threat_score: Option<i64>,
    pub country: Option<String>,
    #[serde(with = "epoch_ms_opt")]
    pub detected_at: Option<DateTime<Utc>>,
    /// Fields this build does not know, kept so a write never drops them.
    #[serde(flatten)]
    pub extra: IndexMap<String, Value>,
}

impl BanNetwork {
    /// On an anonymising network we could CONFIRM.
    #[must_use]
    pub fn is_anonymising(&self) -> bool {
        self.vpn == Some(true) || self.hosting == Some(true)
    }
}

/// A ban as far as evasion scoring is concerned (the C# `Evasion.BanRecord`).
#[derive(Debug, Clone, PartialEq)]
pub struct BanRecord {
    pub player_id: String,
    /// "unknown" when the stored ban had none, as in the C#.
    pub reason: String,
    pub moderator: String,
    pub permanent: bool,
    pub expires: Option<DateTime<Utc>>,
    pub network: Option<BanNetwork>,
}

/// Everything known about the player who just connected.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct JoinContext {
    pub name: Option<String>,
    pub eos_id: Option<String>,
    pub ip: Option<String>,
    pub asn: Option<String>,
    pub provider: Option<String>,
    pub vpn: Option<bool>,
    pub hosting: Option<bool>,
    /// Names this account is known to share a confirmed IP with.
    pub alt_names: Vec<String>,
}

impl JoinContext {
    /// On an anonymising network we could confirm.
    #[must_use]
    pub fn is_anonymising(&self) -> bool {
        self.vpn == Some(true) || self.hosting == Some(true)
    }
}

/// Standing flags in the ban registry, independent of any per-player ban row.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RegistryFlags {
    pub eos_ids: HashSet<String>,
    pub ips: HashSet<String>,
    pub names: HashSet<String>,
}

/// One ban this join was correlated to, and why.
#[derive(Debug, Clone, PartialEq)]
pub struct EvasionMatch {
    pub player_id: String,
    pub reason: String,
    pub moderator: String,
    pub permanent: bool,
    pub expires: Option<DateTime<Utc>>,
    pub score: i32,
    /// Set when a definitive signal fired: currently only an EOS account match.
    pub definitive: bool,
    /// Strongest reason first, so a moderator reads the decisive one immediately.
    pub reasons: Vec<EvasionReason>,
}

/// A standing registry flag that fired for this join.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FlagHit {
    pub kind: SignalKind,
    pub detail: String,
}

/// The verdict. Reports what matched and why; it never bans anyone.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct EvasionResult {
    pub matches: Vec<EvasionMatch>,
    pub flags: Vec<FlagHit>,
}

impl EvasionResult {
    /// Anything at all correlated: worth a moderator's attention.
    #[must_use]
    pub fn is_evasion(&self) -> bool {
        !self.matches.is_empty() || !self.flags.is_empty()
    }

    /// A definitive signal fired: safe to act on without further confirmation.
    #[must_use]
    pub fn is_certain(&self) -> bool {
        self.matches.iter().any(|m| m.definitive)
            || self.flags.iter().any(|f| f.kind == SignalKind::Eos)
    }

    /// Score of the strongest match, or the EOS weight when only a registry flag fired.
    #[must_use]
    pub fn score(&self) -> i32 {
        match (self.matches.first(), self.flags.is_empty()) {
            (Some(first), _) => first.score,
            (None, false) => SignalKind::Eos.weight(),
            (None, true) => 0,
        }
    }
}
