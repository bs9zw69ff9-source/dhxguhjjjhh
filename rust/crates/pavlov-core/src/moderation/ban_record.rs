//! One ban as stored, and the ban decisions that are pure (C# `Core/Moderation/BanRecord.cs`).

use std::collections::HashSet;

use chrono::{DateTime, Utc};
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::Value;

use super::{StaffHierarchy, StaffTier};
use crate::evasion::BanNetwork;
use crate::json::epoch_ms_opt;
use crate::text::case::{contains_ignore_case, eq_ignore_case, fold_case, starts_with_ignore_case};

/// One ban, in the stored shape (camelCase, millisecond timestamps, numeric tier).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct BanRecord {
    /// The display name the ban is filed under, and the name Shack's Ban/Kick/Unban key on.
    ///
    /// The C# declared this `required`, so a stored ban without it failed the WHOLE list. Here
    /// it reads as empty, which makes that one record invalid and skipped, and leaves the rest.
    pub player_id: String,
    /// The account id captured at ban time, for evasion correlation (not the RCON target).
    pub unique_id: Option<String>,
    pub reason: Option<String>,
    pub moderator: Option<String>,
    #[serde(with = "epoch_ms_opt")]
    pub at: Option<DateTime<Utc>>,
    #[serde(with = "epoch_ms_opt")]
    pub expires: Option<DateTime<Utc>>,
    /// No expiry. Read alongside `expires` so a malformed record cannot become permanent.
    pub permanent: bool,
    pub duration_label: Option<String>,
    /// The issuing tier, for [`StaffHierarchy::can_override`]. `None` predates the hierarchy.
    pub tier: Option<StaffTier>,
    /// Network intelligence snapshotted at ban time, because the VPN cache expires.
    pub network: Option<BanNetwork>,
    /// Fields this build does not know, kept so a write never drops them.
    #[serde(flatten)]
    pub extra: IndexMap<String, Value>,
}

impl BanRecord {
    /// A ban for `player_id` with everything else empty.
    #[must_use]
    pub fn for_player(player_id: impl Into<String>) -> Self {
        Self {
            player_id: player_id.into(),
            ..Self::default()
        }
    }

    /// Usable only if it can enforce something: a player, and either permanent or dated.
    /// Guessing either way about an undated, non-permanent ban is wrong.
    #[must_use]
    pub fn is_valid(&self) -> bool {
        !self.player_id.trim().is_empty() && (self.permanent || self.expires.is_some())
    }

    /// In force at `now`.
    #[must_use]
    pub fn is_active_at(&self, now: DateTime<Utc>) -> bool {
        self.permanent || self.expires.is_some_and(|e| e > now)
    }
}

/// What the IP guard should do about a join that matched a flag.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AutoBanAction {
    /// An unexpired temp ban already covers this name. Log it, never escalate.
    Block,
    /// The covering temp ban has been SERVED. Lift it rather than escalating.
    Lift,
    /// Nothing covers them: an evasion alt or a fresh match. Auto-ban.
    Ban,
}

/// Namespace for the ban rules, mirroring the C# static class.
#[derive(Debug)]
pub struct BanRules;

impl BanRules {
    /// Valid, in-force bans. Invalid records are dropped rather than guessed at.
    #[must_use]
    pub fn active(bans: &[BanRecord], now: DateTime<Utc>) -> Vec<&BanRecord> {
        bans.iter()
            .filter(|b| b.is_valid() && b.is_active_at(now))
            .collect()
    }

    /// Same player: trimmed, case-insensitive.
    #[must_use]
    pub fn same_player(a: Option<&str>, b: Option<&str>) -> bool {
        matches!((a, b), (Some(a), Some(b)) if eq_ignore_case(a.trim(), b.trim()))
    }

    /// What to do about a join that hit a flagged IP, name or account id.
    ///
    /// THE LIFT CASE MATTERS MOST: a served ban's own leftover IP and account flags look exactly
    /// like evasion until the sweep clears them, and escalating there turns a served temp ban
    /// into a permanent one. A USERNAME match still escalates: a name flag is only ever set
    /// deliberately by an admin, so there is no expiry to have served.
    #[must_use]
    pub fn auto_ban_decision(
        existing: Option<&BanRecord>,
        match_reason: Option<&str>,
        now: DateTime<Utc>,
    ) -> AutoBanAction {
        if let Some(BanRecord {
            permanent: false,
            expires: Some(expires),
            ..
        }) = existing
        {
            if *expires > now {
                return AutoBanAction::Block;
            }
            let reason = match_reason.unwrap_or("");
            if contains_ignore_case(reason, "blacklisted ip")
                || contains_ignore_case(reason, "blacklisted account")
            {
                return AutoBanAction::Lift;
            }
        }
        AutoBanAction::Ban
    }

    /// The active ban a new ban for this player must NOT replace, or `None` when it may.
    ///
    /// REPLACING IS LIFTING: one record per player, so a mod who cannot /unban an owner's
    /// permanent ban must not be able to /tempban the player for a minute and wait. Only an
    /// ACTIVE record is protected; an untiered one stays replaceable as it stays liftable, so
    /// automation (`actor = None`) can never replace a human's ban.
    #[must_use]
    pub fn protected_from_replacement<'a>(
        bans: &'a [BanRecord],
        player: &str,
        actor: StaffTier,
        now: DateTime<Utc>,
    ) -> Option<&'a BanRecord> {
        bans.iter().find(|b| {
            Self::same_player(Some(&b.player_id), Some(player))
                && b.is_valid()
                && b.is_active_at(now)
                && !StaffHierarchy::can_override(actor, b.tier)
        })
    }

    /// Whether a ban is a REAL punishment on a person rather than machinery: not an auto-ban,
    /// not an evasion ban, not a broad `/configure` blacklist entry. Used to find the original
    /// offence behind an auto-ban, so `/checkban` shows what somebody actually did.
    #[must_use]
    pub fn is_real_ban(ban: Option<&BanRecord>) -> bool {
        let Some(ban) = ban else { return false };
        let Some(reason) = ban.reason.as_deref().filter(|r| !r.is_empty()) else {
            return false;
        };
        if starts_with_ignore_case(reason, "auto-ban")
            || starts_with_ignore_case(reason, "ban evasion")
            || contains_ignore_case(reason, "blacklisted via")
        {
            return false;
        }
        let moderator = ban.moderator.as_deref().unwrap_or("");
        moderator != "IP-Guard" && !contains_ignore_case(moderator, "evasion")
    }

    /// The ban whose flag caught this player, so the record can show the real offence.
    /// SAME ACCOUNT first (the EOS id is the one identifier an evader cannot change), then the
    /// name and known alts.
    #[must_use]
    pub fn source_ban_for<'a>(
        bans: &'a [BanRecord],
        name: &str,
        unique_id: Option<&str>,
        eos_id_of: impl Fn(&str) -> Option<String>,
        alt_names: &[String],
    ) -> Option<&'a BanRecord> {
        let real: Vec<&BanRecord> = bans.iter().filter(|b| Self::is_real_ban(Some(b))).collect();

        if let Some(uid) = unique_id.filter(|u| !u.is_empty()) {
            if let Some(by_account) = real
                .iter()
                .find(|b| eos_id_of(&b.player_id).is_some_and(|eos| eq_ignore_case(&eos, uid)))
            {
                return Some(by_account);
            }
        }

        let mut related: HashSet<String> = HashSet::from([fold_case(name)]);
        related.extend(alt_names.iter().map(|a| fold_case(a)));
        real.into_iter()
            .find(|b| related.contains(&fold_case(&b.player_id)))
    }
}
