//! Accounts, standing flags and flag matching (C# `Core/Evasion/IpRegistry.cs`).
//!
//! EXACT MATCHES ONLY, never a subnet: a /24 on a residential ISP is 254 unrelated
//! households, and one evader would ban a neighbourhood.

use chrono::{DateTime, TimeDelta, Utc};
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::json::{epoch_ms, epoch_ms_opt, null_as_default};
use crate::text::case::IgnoreCaseSet;

/// What is known about one account.
///
/// EVERY LIST DEFAULTS TO EMPTY, null included. The C# learned this the hard way: a stored row
/// missing `names` threw on every login and disconnect for that player, and took the rest of the
/// log batch with it.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AccountRecord {
    /// The EOS UniqueId: the only identifier a player cannot change.
    #[serde(deserialize_with = "null_as_default")]
    pub id: String,
    /// Addresses seen on the SAME log line as this id. Certain, and the ONLY ones allowed to
    /// flag or trigger a ban.
    #[serde(deserialize_with = "null_as_default")]
    pub confirmed_ips: Vec<String>,
    /// Addresses correlated from a nearby accept line. DISPLAY ONLY: promoting one to a flag
    /// bans whoever else was connecting at that moment.
    #[serde(deserialize_with = "null_as_default")]
    pub guessed_ips: Vec<String>,
    /// Every display name this account has used, most recent first.
    #[serde(deserialize_with = "null_as_default")]
    pub names: Vec<String>,
    #[serde(with = "epoch_ms_opt")]
    pub first_seen: Option<DateTime<Utc>>,
    #[serde(with = "epoch_ms_opt")]
    pub last_seen: Option<DateTime<Utc>>,
    /// Fields this build does not know, kept so a write never drops them.
    #[serde(flatten)]
    pub extra: IndexMap<String, Value>,
}

impl AccountRecord {
    /// A record with nothing known but the id.
    #[must_use]
    pub fn empty(id: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            ..Self::default()
        }
    }

    /// The most recent name, if any.
    #[must_use]
    pub fn name(&self) -> Option<&str> {
        self.names.first().map(String::as_str)
    }
}

/// The standing flags. Addresses match exactly; names and ids ignoring case.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct FlagSet {
    /// Flagged addresses. EXACT match only.
    #[serde(deserialize_with = "null_as_default")]
    pub ips: indexmap::IndexSet<String>,
    /// Flagged display names, set deliberately by an admin.
    pub names: IgnoreCaseSet,
    /// Flagged account ids. Set only by a PERMANENT ban.
    pub ids: IgnoreCaseSet,
    /// Admin-flagged addresses, kept apart so a player unban cannot clear a standing block.
    #[serde(deserialize_with = "null_as_default")]
    pub manual_ips: indexmap::IndexSet<String>,
    /// Fields this build does not know, kept so a write never drops them.
    #[serde(flatten)]
    pub extra: IndexMap<String, Value>,
}

/// Why a join was refused, if it was.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FlagMatch {
    None,
    Ip,
    Name,
    AccountId,
}

/// The flag verdict.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FlagVerdict {
    pub matched: FlagMatch,
    /// The reason text the ban decision inspects. THE WORDING IS LOAD-BEARING:
    /// `BanRules::auto_ban_decision` reads it to tell a ban-derived flag from an admin one.
    pub detail: Option<String>,
    /// Set by an owner by hand, rather than left behind by a ban.
    pub manual: bool,
}

impl FlagVerdict {
    /// Nothing matched.
    #[must_use]
    pub fn clean() -> Self {
        Self {
            matched: FlagMatch::None,
            detail: None,
            manual: false,
        }
    }

    /// Whether anything matched.
    #[must_use]
    pub fn hit(&self) -> bool {
        self.matched != FlagMatch::None
    }

    fn of(matched: FlagMatch, detail: String, manual: bool) -> Self {
        Self {
            matched,
            detail: Some(detail),
            manual,
        }
    }
}

/// Namespace for flag matching, mirroring the C# static class.
#[derive(Debug)]
pub struct FlagMatching;

impl FlagMatching {
    /// Match one join against the flags: manual addresses, then ban addresses, then account
    /// ids, then names LAST (a name flag outlives a served ban, so an address or account match
    /// carrying the ban's own expiry should win when both apply).
    #[must_use]
    pub fn check(
        flags: &FlagSet,
        ip: Option<&str>,
        name: Option<&str>,
        account_id: Option<&str>,
    ) -> FlagVerdict {
        if let Some(ip) = ip.filter(|s| !s.is_empty()) {
            // The detail is identical either way; only `manual` tells them apart.
            if flags.manual_ips.contains(ip) {
                return FlagVerdict::of(FlagMatch::Ip, format!("blacklisted ip {ip}"), true);
            }
            if flags.ips.contains(ip) {
                return FlagVerdict::of(FlagMatch::Ip, format!("blacklisted ip {ip}"), false);
            }
        }
        if let Some(id) = account_id.filter(|s| !s.is_empty()) {
            if flags.ids.contains(id) {
                return FlagVerdict::of(
                    FlagMatch::AccountId,
                    format!("blacklisted account {id}"),
                    false,
                );
            }
        }
        if let Some(name) = name.filter(|s| !s.is_empty()) {
            if flags.names.contains(name) {
                return FlagVerdict::of(
                    FlagMatch::Name,
                    format!("blacklisted username {name}"),
                    true,
                );
            }
        }
        FlagVerdict::clean()
    }
}

/// An account banned while still online, waiting for its address to be confirmed by the
/// disconnect the ban itself causes.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingFlag {
    pub account_id: String,
    #[serde(with = "epoch_ms")]
    pub requested_at: DateTime<Utc>,
    pub flag_account_id: bool,
}

impl PendingFlag {
    /// Still inside the window: after a few minutes any disconnect is somebody else's.
    #[must_use]
    pub fn is_live(&self, now: DateTime<Utc>, window: TimeDelta) -> bool {
        now - self.requested_at <= window
    }
}

/// What a ban actually flagged, for the audit line.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FlagOutcome {
    pub ips: Vec<String>,
    pub ids: Vec<String>,
    pub alt_names: Vec<String>,
    /// The account had no confirmed address yet and the flag is deferred. Worth saying: a ban
    /// that flagged nothing looks identical to one that worked until an alt walks back in.
    pub pending: bool,
}
