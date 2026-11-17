//! Everything the bot knows about one player, and what staff may see of it
//! (C# `Core/Intelligence/PlayerProfile.cs`).

use std::borrow::Cow;

use chrono::{DateTime, Utc};

use super::{Addresses, RiskAssessment, RiskSignal};

/// Who they are.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct ProfileIdentity {
    pub current: String,
    pub account_id: Option<String>,
    pub previous_names: Vec<String>,
}

impl ProfileIdentity {
    /// Nothing known but the name asked about.
    #[must_use]
    pub fn unknown(name: impl Into<String>) -> Self {
        Self {
            current: name.into(),
            account_id: None,
            previous_names: Vec::new(),
        }
    }
}

/// When and where they play.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct ProfileActivity {
    pub first_seen: Option<DateTime<Utc>>,
    pub last_seen: Option<DateTime<Utc>>,
    pub playtime_minutes: i64,
    pub online: bool,
    pub servers: Vec<String>,
}

/// Network facts. The ADDRESSES are the sensitive part; the verdicts are not.
#[derive(Debug, Clone, Default, PartialEq, Eq, serde::Serialize)]
pub struct ProfileNetwork {
    pub confirmed_ips: Vec<String>,
    pub guessed_ips: Vec<String>,
    pub vpn: Option<bool>,
    pub proxy: Option<bool>,
    pub asn: Option<String>,
    pub country: Option<String>,
}

impl ProfileNetwork {
    #[must_use]
    pub fn known_addresses(&self) -> usize {
        self.confirmed_ips.len() + self.guessed_ips.len()
    }
}

/// Bans, warnings and kicks.
#[derive(Debug, Clone, Default, PartialEq, Eq, serde::Serialize)]
pub struct ProfileModeration {
    pub active_ban: Option<String>,
    pub ban_expires: Option<DateTime<Utc>>,
    pub ban_is_permanent: bool,
    pub warnings: i64,
    pub total_warnings: i64,
    pub kicks: i64,
    pub actions: Vec<String>,
}

impl ProfileModeration {
    #[must_use]
    pub fn banned(&self) -> bool {
        self.active_ban.is_some()
    }
}

/// Their faction, if any.
#[derive(Debug, Clone, Default, PartialEq, Eq, serde::Serialize)]
pub struct ProfileFaction {
    pub name: Option<String>,
    pub rank: Option<String>,
    pub discord_id: Option<u64>,
}

/// Kills and deaths.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, serde::Serialize)]
pub struct ProfileCombat {
    pub kills: i64,
    pub deaths: i64,
    pub suicides: i64,
    pub last_at: Option<DateTime<Utc>>,
}

impl ProfileCombat {
    /// K/D, with deaths floored at one.
    #[must_use]
    pub fn ratio(&self) -> f64 {
        // Lossless for any real kill count.
        #[allow(clippy::cast_precision_loss)]
        let ratio = self.kills as f64 / self.deaths.max(1) as f64;
        ratio
    }

    #[must_use]
    pub fn any(&self) -> bool {
        self.kills > 0 || self.deaths > 0 || self.suicides > 0
    }
}

/// Money.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, serde::Serialize)]
pub struct ProfileEconomy {
    pub balance: Option<i64>,
    pub owed_wages: i64,
    pub lifetime_wages: i64,
}

/// Another account linked through a shared address.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct ProfileAssociation {
    pub name: String,
    pub account_id: String,
    pub reason: String,
    pub banned: bool,
}

/// The whole profile.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct PlayerProfile {
    pub identity: ProfileIdentity,
    pub activity: ProfileActivity,
    pub network: ProfileNetwork,
    pub moderation: ProfileModeration,
    pub faction: ProfileFaction,
    pub economy: ProfileEconomy,
    pub associations: Vec<ProfileAssociation>,
    pub risk: RiskAssessment,
    /// Network detail was withheld from this copy.
    pub redacted: bool,
    /// `None` when built without combat data: "none", not zeroes.
    pub combat: Option<ProfileCombat>,
}

impl PlayerProfile {
    /// Whether the bot knows anything about them at all.
    #[must_use]
    pub fn known(&self) -> bool {
        self.identity.account_id.is_some()
            || self.activity.last_seen.is_some()
            || self.activity.playtime_minutes > 0
            || self.moderation.banned()
            || self.moderation.total_warnings > 0
    }
}

/// How much network detail a viewer may see.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
pub enum ProfileVisibility {
    Moderation,
    Network,
}

/// Namespace for redaction, mirroring the C# static class.
#[derive(Debug)]
pub struct ProfileRedaction;

impl ProfileRedaction {
    /// The profile as `visibility` may see it. Network access gets the profile itself,
    /// borrowed, untouched.
    ///
    /// THE VERDICTS SURVIVE, THE ADDRESSES DO NOT: "on a VPN" is a moderation fact and says
    /// nothing about where somebody lives. ASSOCIATIONS GO ENTIRELY: each is derived from a
    /// shared address, so "these two are linked" leaks the address relationship.
    #[must_use]
    pub fn for_visibility(
        profile: &PlayerProfile,
        visibility: ProfileVisibility,
    ) -> Cow<'_, PlayerProfile> {
        if visibility == ProfileVisibility::Network {
            return Cow::Borrowed(profile);
        }
        let had_network = profile.network.known_addresses() > 0
            || profile.network.asn.is_some()
            || !profile.associations.is_empty()
            || profile.risk.signals.iter().any(|s| s.sensitive);
        Cow::Owned(PlayerProfile {
            network: ProfileNetwork {
                vpn: profile.network.vpn,
                proxy: profile.network.proxy,
                country: profile.network.country.clone(),
                ..ProfileNetwork::default()
            },
            associations: Vec::new(),
            risk: RiskAssessment {
                signals: profile.risk.signals.iter().map(Self::narrow).collect(),
                ..profile.risk.clone()
            },
            redacted: had_network,
            ..profile.clone()
        })
    }

    fn narrow(signal: &RiskSignal) -> RiskSignal {
        RiskSignal {
            summary: Addresses::scrub(Some(&signal.summary)),
            evidence: Some(if signal.sensitive {
                "withheld - needs network access".to_owned()
            } else {
                Addresses::scrub(signal.evidence.as_deref())
            }),
            ..signal.clone()
        }
    }

    /// Network access sees everything; everyone else sees the moderation view.
    #[must_use]
    pub fn visibility_for(has_network_access: bool) -> ProfileVisibility {
        if has_network_access {
            ProfileVisibility::Network
        } else {
            ProfileVisibility::Moderation
        }
    }
}
