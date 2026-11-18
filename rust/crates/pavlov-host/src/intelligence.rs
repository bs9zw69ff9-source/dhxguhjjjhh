//! One player, assembled from every system that already knows something about them
//! (C# `Intelligence/PlayerIntelligenceService.cs`).
//!
//! - IT OWNS NO DATA: every field is read from the service that owns it, and nothing here is
//!   stored. A seventh copy would be a seventh thing to go stale.
//! - EVERY OPTIONAL SOURCE IS OPTIONAL: a fresh install with no ledger, roster path or VPN keys
//!   still gets a profile, rather than an error exactly when somebody is setting it up.
//! - The Discord layer reads it ONLY through [`PlayerIntelligenceService::profile_for`], which
//!   redacts here, so a command never holds an address it must not print.
//! - Risk signals are GATHERED here and scored by [`RiskScorer`]; the evasion match reuses
//!   [`EvasionScorer`] and its own threshold, so `/player` and the auto-ban path never disagree.

use std::sync::Arc;

use chrono::TimeDelta;
use pavlov_core::data::SerializedStore;
use pavlov_core::economy::BalanceStore;
use pavlov_core::evasion::{
    self, AccountRecord, EvasionScorer, FlagMatch, FlagMatching, JoinContext, SignalKind,
};
use pavlov_core::intelligence::{
    PlayerProfile, ProfileActivity, ProfileAssociation, ProfileCombat, ProfileEconomy,
    ProfileFaction, ProfileIdentity, ProfileModeration, ProfileNetwork, ProfileRedaction,
    ProfileVisibility, RiskScorer, RiskSignal, RiskSignalKind,
};
use pavlov_core::json::timestamp_from_value;
use pavlov_core::moderation::{BanRecord, BanRules};
use pavlov_core::text::case::{IgnoreCaseMap, IgnoreCaseSet, eq_ignore_case};
use pavlov_core::time::Clock;
use pavlov_storage::datasets::{LAST_SEEN, MOD_LOG, PLAYTIME};
use pavlov_storage::playtime::PlaytimeEntry;
use serde_json::Value;

use crate::economy::payroll::Payroll;
use crate::factions::{FactionMembers, RosterService};
use crate::kill_stats::KillStats;
use crate::logs::ip_tracking::IpTrackingService;
use crate::moderation::audit::ModAction;
use crate::moderation::bans::BanService;
use crate::moderation::warnings::WarningService;
use crate::registry::RconRegistry;
use crate::vpn::screening::VpnScreeningService;

/// How recently an account must have appeared to count as brand new. A context signal only:
/// most new players are new players.
pub const NEW_ACCOUNT_WINDOW: TimeDelta = TimeDelta::days(1);
/// Recent staff actions listed on a profile.
const RECENT_ACTIONS: usize = 10;
/// Associations listed on a profile.
const MAX_ASSOCIATIONS: usize = 25;

/// The sources a profile is built from. The optional ones are absent on a partial install.
pub struct IntelligenceSources {
    pub tracking: Arc<IpTrackingService>,
    pub bans: Arc<BanService>,
    pub warnings: Arc<WarningService>,
    pub rcon: Arc<RconRegistry>,
    pub store: Arc<SerializedStore>,
    pub clock: Arc<dyn Clock>,
    pub rosters: Option<Arc<RosterService>>,
    pub members: Option<Arc<FactionMembers>>,
    pub balances: Option<Arc<dyn BalanceStore>>,
    pub payroll: Option<Arc<Payroll>>,
    pub vpn: Option<Arc<VpnScreeningService>>,
    pub kill_stats: Option<Arc<KillStats>>,
}

impl std::fmt::Debug for IntelligenceSources {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("IntelligenceSources")
            .finish_non_exhaustive()
    }
}

/// The service.
pub struct PlayerIntelligenceService {
    s: IntelligenceSources,
}

impl std::fmt::Debug for PlayerIntelligenceService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PlayerIntelligenceService")
            .finish_non_exhaustive()
    }
}

fn latest_ip(account: &AccountRecord) -> Option<&str> {
    account.confirmed_ips.last().map(String::as_str)
}

/// The moderation ban in the shape the evasion scorer reads: the seam between the two
/// `BanRecord` types.
fn to_evasion_ban(ban: &BanRecord) -> evasion::BanRecord {
    evasion::BanRecord {
        player_id: ban.player_id.clone(),
        reason: ban.reason.clone().unwrap_or_else(|| "unknown".into()),
        moderator: ban.moderator.clone().unwrap_or_else(|| "unknown".into()),
        permanent: ban.permanent,
        expires: ban.expires,
        network: ban.network.clone(),
    }
}

impl PlayerIntelligenceService {
    #[must_use]
    pub fn new(sources: IntelligenceSources) -> Self {
        Self { s: sources }
    }

    /// A profile narrowed to what `visibility` may see: the ONLY entry point for Discord.
    #[must_use]
    pub fn profile_for(&self, player: &str, visibility: ProfileVisibility) -> PlayerProfile {
        ProfileRedaction::for_visibility(&self.profile(player), visibility).into_owned()
    }

    /// Everything known about one player, by in-game name (what staff have in front of them).
    #[must_use]
    pub fn profile(&self, player: &str) -> PlayerProfile {
        let name = player.trim();
        let account = self.s.tracking.account_by_name(name);
        let identity = ProfileIdentity {
            current: name.to_owned(),
            account_id: account.as_ref().map(|a| a.id.clone()),
            previous_names: account
                .as_ref()
                .map(|a| {
                    a.names
                        .iter()
                        .filter(|n| !eq_ignore_case(n, name))
                        .cloned()
                        .collect()
                })
                .unwrap_or_default(),
        };
        let activity = self.activity(name, account.as_ref());
        let network = self.network(account.as_ref());
        let moderation = self.moderation(name);
        let faction = self.faction(name);
        let economy = self.economy(name);
        let associations = self.associations(account.as_ref());
        let risk = RiskScorer::assess(self.signals(
            name,
            account.as_ref(),
            &network,
            &moderation,
            &associations,
            &activity,
        ));
        // BY NAME: the only key a Pavlov kill line gives.
        let combat = self.s.kill_stats.as_ref().map(|k| {
            let kills = k.of(name);
            ProfileCombat {
                kills: kills.kills,
                deaths: kills.deaths,
                suicides: kills.suicides,
                last_at: kills.last_at,
            }
        });
        PlayerProfile {
            identity,
            activity,
            network,
            moderation,
            faction,
            economy,
            associations,
            risk,
            redacted: false,
            combat,
        }
    }

    fn activity(&self, name: &str, account: Option<&AccountRecord>) -> ProfileActivity {
        let playtime: IgnoreCaseMap<PlaytimeEntry> = self
            .s
            .store
            .read(PLAYTIME, IgnoreCaseMap::new())
            .unwrap_or_default();
        let entry = playtime.get(name);
        let seen: IgnoreCaseMap<Value> = self
            .s
            .store
            .read(LAST_SEEN, IgnoreCaseMap::new())
            .unwrap_or_default();
        let last_seen = seen
            .get(name)
            .and_then(timestamp_from_value)
            .or_else(|| entry.and_then(|e| e.last_seen));
        let servers: Vec<String> = self
            .s
            .rcon
            .servers()
            .into_iter()
            .filter(|s| {
                self.s
                    .rcon
                    .roster(s)
                    .players
                    .iter()
                    .any(|p| eq_ignore_case(&p.name, name))
            })
            .collect();
        ProfileActivity {
            first_seen: account.and_then(|a| a.first_seen),
            last_seen,
            playtime_minutes: entry.map_or(0, |e| e.minutes),
            online: !servers.is_empty(),
            servers,
        }
    }

    fn network(&self, account: Option<&AccountRecord>) -> ProfileNetwork {
        let Some(account) = account else {
            return ProfileNetwork::default();
        };
        // THE MOST RECENT confirmed address drives the VPN answer: an old one says where they were.
        let record = latest_ip(account).and_then(|ip| self.s.vpn.as_ref()?.cached(ip));
        ProfileNetwork {
            confirmed_ips: account.confirmed_ips.clone(),
            guessed_ips: account.guessed_ips.clone(),
            vpn: record.as_ref().and_then(|r| r.vpn),
            proxy: record.as_ref().and_then(|r| r.proxy),
            asn: record.as_ref().and_then(|r| r.asn.clone()),
            country: record.and_then(|r| r.country),
        }
    }

    fn moderation(&self, name: &str) -> ProfileModeration {
        let now = self.s.clock.now();
        let all = self.s.bans.load_bans();
        let active = all
            .iter()
            .find(|b| BanRules::same_player(Some(&b.player_id), Some(name)) && b.is_active_at(now));
        let mut log: Vec<ModAction> = self
            .s
            .store
            .read::<Vec<Option<ModAction>>>(MOD_LOG, Vec::new())
            .unwrap_or_default()
            .into_iter()
            .flatten()
            .filter(|a| BanRules::same_player(Some(&a.player), Some(name)))
            .collect();
        log.sort_by_key(|a| std::cmp::Reverse(a.at));
        let count = |n: usize| i64::try_from(n).unwrap_or(i64::MAX);
        ProfileModeration {
            active_ban: active.map(|b| {
                b.reason
                    .clone()
                    .unwrap_or_else(|| "no reason recorded".into())
            }),
            ban_expires: active.and_then(|b| b.expires),
            ban_is_permanent: active.is_some_and(|b| b.permanent),
            warnings: count(self.s.warnings.active_for(name).len()),
            total_warnings: count(self.s.warnings.for_player(name).len()),
            kicks: count(
                log.iter()
                    .filter(|a| eq_ignore_case(&a.action, "kick"))
                    .count(),
            ),
            actions: log
                .iter()
                .take(RECENT_ACTIONS)
                .map(|a| format!("{} by {}", a.action, a.moderator))
                .collect(),
        }
    }

    fn faction(&self, name: &str) -> ProfileFaction {
        let Some(rosters) = self.s.rosters.as_ref().filter(|r| r.enabled()) else {
            return ProfileFaction::default();
        };
        let Some(membership) = rosters.find(name) else {
            return ProfileFaction::default();
        };
        ProfileFaction {
            name: Some(membership.faction),
            rank: Some(membership.rank),
            discord_id: self.s.members.as_ref().and_then(|m| m.owner_of(name)),
        }
    }

    fn economy(&self, name: &str) -> ProfileEconomy {
        // Read through BalanceStore: a profile only reads. `None` is "never banked", not broke.
        ProfileEconomy {
            balance: self.s.balances.as_ref().and_then(|b| b.read(name)),
            owed_wages: self.s.payroll.as_ref().map_or(0, |p| p.owed_to(name)),
            lifetime_wages: self.s.payroll.as_ref().map_or(0, |p| p.total_paid_to(name)),
        }
    }

    /// Accounts sharing a CONFIRMED address, banned ones first. A guessed address can put a
    /// stranger on somebody's profile as a "known associate", so it is never used.
    fn associations(&self, account: Option<&AccountRecord>) -> Vec<ProfileAssociation> {
        let Some(account) = account else {
            return Vec::new();
        };
        let banned: IgnoreCaseSet = self
            .s
            .bans
            .active_bans()
            .into_iter()
            .map(|b| b.player_id)
            .collect();
        let mut rows: Vec<ProfileAssociation> = self
            .s
            .tracking
            .alts_of(&account.id)
            .into_iter()
            .map(|a| ProfileAssociation {
                name: a.name().unwrap_or(&a.id).to_owned(),
                account_id: a.id.clone(),
                reason: "shared a confirmed address".into(),
                banned: a.names.iter().any(|n| banned.contains(n)),
            })
            .collect();
        rows.sort_by_key(|a| !a.banned);
        rows.truncate(MAX_ASSOCIATIONS);
        rows
    }

    fn signals(
        &self,
        name: &str,
        account: Option<&AccountRecord>,
        network: &ProfileNetwork,
        moderation: &ProfileModeration,
        associations: &[ProfileAssociation],
        activity: &ProfileActivity,
    ) -> Vec<RiskSignal> {
        let mut signals = Vec::new();
        if moderation.banned() {
            signals.push(RiskSignal::new(
                RiskSignalKind::BannedAccount,
                "This account is banned right now.",
                moderation.active_ban.clone(),
                false,
            ));
        }
        for associate in associations.iter().filter(|a| a.banned) {
            signals.push(RiskSignal::new(
                RiskSignalKind::SharedAddressWithBanned,
                format!(
                    "Shares a confirmed address with {}, who is banned.",
                    associate.name
                ),
                Some(associate.account_id.clone()),
                true,
            ));
        }
        if let Some(account) = account {
            let verdict = FlagMatching::check(
                &self.s.tracking.load_flags(),
                latest_ip(account),
                Some(name),
                Some(&account.id),
            );
            if verdict.hit() {
                signals.push(RiskSignal::new(
                    RiskSignalKind::Flagged,
                    format!(
                        "Matches a standing ban-evasion flag ({:?}).",
                        verdict.matched
                    ),
                    verdict.detail.clone(),
                    verdict.matched == FlagMatch::Ip,
                ));
            }
            signals.extend(self.evasion(name, account, network));
        }
        let prior = self
            .s
            .bans
            .load_bans()
            .iter()
            .filter(|b| BanRules::same_player(Some(&b.player_id), Some(name)))
            .count();
        if prior > 0 && !moderation.banned() {
            signals.push(RiskSignal::new(
                RiskSignalKind::PriorBan,
                format!("Has {prior} ban record(s) that are no longer in force."),
                None,
                false,
            ));
        }
        if network.vpn == Some(true) || network.proxy == Some(true) {
            let summary = if network.proxy == Some(true) {
                "Connecting through a proxy."
            } else {
                "Connecting through a VPN."
            };
            signals.push(RiskSignal::new(
                RiskSignalKind::Anonymising,
                summary,
                network.asn.clone(),
                false,
            ));
        }
        if moderation.warnings > 0 {
            signals.push(RiskSignal::new(
                RiskSignalKind::ActiveWarnings,
                format!(
                    "{} active warning(s) inside the decay window.",
                    moderation.warnings
                ),
                None,
                false,
            ));
        }
        if activity
            .first_seen
            .is_some_and(|first| self.s.clock.now() - first < NEW_ACCOUNT_WINDOW)
        {
            signals.push(RiskSignal::new(
                RiskSignalKind::BrandNewAccount,
                "First seen in the last day. Context only - most new players are new players.",
                None,
                false,
            ));
        }
        signals
    }

    fn evasion(
        &self,
        name: &str,
        account: &AccountRecord,
        network: &ProfileNetwork,
    ) -> Option<RiskSignal> {
        let join = JoinContext {
            name: Some(name.to_owned()),
            eos_id: Some(account.id.clone()),
            ip: latest_ip(account).map(str::to_owned),
            asn: network.asn.clone(),
            alt_names: account.names.clone(),
            ..JoinContext::default()
        };
        let bans: Vec<evasion::BanRecord> = self
            .s
            .bans
            .active_bans()
            .iter()
            .map(to_evasion_ban)
            .collect();
        let result = EvasionScorer::find(&join, &bans, None);
        // THE SCORER'S OWN THRESHOLD, not a second opinion about the same number.
        let best = result.matches.first()?;
        if result.score() < EvasionScorer::MATCH_THRESHOLD {
            return None;
        }
        let reason = best.reasons.iter().max_by_key(|r| r.weight());
        Some(RiskSignal::new(
            RiskSignalKind::EvasionMatch,
            format!(
                "The evasion scorer matched them to banned player {} (score {}): {}.",
                best.player_id,
                result.score(),
                reason.map_or("no detail recorded", |r| r.detail.as_str())
            ),
            Some(best.player_id.clone()),
            reason.is_some_and(|r| r.kind == SignalKind::Ip),
        ))
    }
}
