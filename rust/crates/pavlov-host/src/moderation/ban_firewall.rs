//! Banned players' addresses denied at the box's firewall, and lifted when the ban ends (C#
//! `Moderation/BanFirewall.cs`).
//!
//! NEVER DENIED: a non-public address (loopback above all - RCON runs over it, so denying it
//! takes the bot off every server), a master's address, a `FIREWALL_NEVER_BLOCK` entry, and an
//! address an owner blocked by hand (that rule is the owner panel's, never this one's). Only
//! the rules this pass added are ever lifted, at most 50 changes a pass, and a failure backs
//! off for ten minutes rather than hammering ufw.

use std::collections::HashMap;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::sync::{Arc, Mutex, PoisonError};

use chrono::{DateTime, TimeDelta, Utc};
use indexmap::{IndexMap, IndexSet};
use pavlov_core::data::SerializedStore;
use pavlov_core::evasion::AccountRecord;
use pavlov_core::moderation::BanRecord;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::Clock;
use pavlov_storage::datasets::FIREWALL_BANS;
use serde::{Deserialize, Serialize};

use super::bans::{BanService, MasterNames};
use super::master_names::StoredMasterNames;
use crate::firewall::Firewall;
use crate::logs::IpTrackingService;

/// The most rules one pass changes.
pub const MAX_CHANGES_PER_PASS: usize = 50;
/// How long a failed change waits before it is tried again.
pub const RETRY_AFTER: TimeDelta = TimeDelta::minutes(10);

/// One rule this feature added.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FirewalledBan {
    pub player: String,
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub at: DateTime<Utc>,
}

/// What a pass did.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct BanFirewallPass {
    pub denied: usize,
    pub lifted: usize,
    pub failed: usize,
    pub deferred: usize,
}

type Managed = IndexMap<String, FirewalledBan>;

/// The address in canonical text, IPv4-mapped IPv6 unwrapped; `None` when not an address.
#[must_use]
pub fn canonical(raw: Option<&str>) -> Option<String> {
    let address: IpAddr = raw?.trim().parse().ok()?;
    Some(
        match address {
            IpAddr::V6(v6) => v6.to_ipv4_mapped().map_or(IpAddr::V6(v6), IpAddr::V4),
            v4 @ IpAddr::V4(_) => v4,
        }
        .to_string(),
    )
}

fn v4_in(address: Ipv4Addr, network: [u8; 4], prefix: u32) -> bool {
    let mask = if prefix == 0 {
        0
    } else {
        u32::MAX << (32 - prefix)
    };
    u32::from(address) & mask == u32::from(Ipv4Addr::from(network)) & mask
}

fn v6_in(address: Ipv6Addr, network: Ipv6Addr, prefix: u32) -> bool {
    let mask = if prefix == 0 {
        0
    } else {
        u128::MAX << (128 - prefix)
    };
    u128::from(address) & mask == u128::from(network) & mask
}

/// Public and routable: not loopback, private, CGNAT, link-local, multicast or reserved.
#[must_use]
pub fn is_public(canonical: &str) -> bool {
    match canonical.parse::<IpAddr>() {
        Ok(IpAddr::V4(v4)) => ![
            ([0, 0, 0, 0], 8),
            ([10, 0, 0, 0], 8),
            ([100, 64, 0, 0], 10),
            ([127, 0, 0, 0], 8),
            ([169, 254, 0, 0], 16),
            ([172, 16, 0, 0], 12),
            ([192, 168, 0, 0], 16),
            ([224, 0, 0, 0], 3),
        ]
        .iter()
        .any(|(network, prefix)| v4_in(v4, *network, *prefix)),
        Ok(IpAddr::V6(v6)) => ![
            (Ipv6Addr::UNSPECIFIED, 128),
            (Ipv6Addr::LOCALHOST, 128),
            (Ipv6Addr::new(0xfc00, 0, 0, 0, 0, 0, 0, 0), 7),
            (Ipv6Addr::new(0xfe80, 0, 0, 0, 0, 0, 0, 0), 10),
            (Ipv6Addr::new(0xff00, 0, 0, 0, 0, 0, 0, 0), 8),
        ]
        .iter()
        .any(|(network, prefix)| v6_in(v6, *network, *prefix)),
        Err(_) => false,
    }
}

/// Accounts by id and by every name they have used, built once per pass.
struct AccountIndex {
    by_id: IgnoreCaseMap<AccountRecord>,
    by_name: IgnoreCaseMap<AccountRecord>,
}

impl AccountIndex {
    fn new(accounts: IgnoreCaseMap<AccountRecord>) -> Self {
        let mut by_name = IgnoreCaseMap::default();
        for (_, account) in accounts.iter() {
            for name in &account.names {
                if !by_name.contains_key(name) {
                    by_name.insert(name.clone(), account.clone());
                }
            }
        }
        Self {
            by_id: accounts,
            by_name,
        }
    }
}

/// The ban firewall.
pub struct BanFirewall {
    bans: Arc<BanService>,
    tracking: Arc<IpTrackingService>,
    masters: Arc<StoredMasterNames>,
    firewall: Arc<dyn Firewall>,
    store: Arc<SerializedStore>,
    enabled: bool,
    never_block: IndexSet<String>,
    clock: Arc<dyn Clock>,
    failed_at: Mutex<HashMap<String, DateTime<Utc>>>,
}

impl std::fmt::Debug for BanFirewall {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BanFirewall")
            .field("enabled", &self.enabled)
            .field("never_block", &self.never_block)
            .finish_non_exhaustive()
    }
}

impl BanFirewall {
    /// `never_block` entries that are not addresses are reported and ignored.
    #[allow(clippy::too_many_arguments)]
    #[must_use]
    pub fn new(
        bans: Arc<BanService>,
        tracking: Arc<IpTrackingService>,
        masters: Arc<StoredMasterNames>,
        firewall: Arc<dyn Firewall>,
        store: Arc<SerializedStore>,
        enabled: bool,
        never_block: &[String],
        clock: Arc<dyn Clock>,
    ) -> Self {
        let mut parsed = IndexSet::new();
        for raw in never_block {
            match canonical(Some(raw)) {
                Some(ip) => {
                    parsed.insert(ip);
                }
                None => {
                    tracing::warn!(entry = %raw, "a FIREWALL_NEVER_BLOCK entry is not an address and is ignored")
                }
            }
        }
        Self {
            bans,
            tracking,
            masters,
            firewall,
            store,
            enabled,
            never_block: parsed,
            clock,
            failed_at: Mutex::new(HashMap::new()),
        }
    }

    fn manual_blocks(&self) -> IndexSet<String> {
        self.tracking
            .load_flags()
            .manual_ips
            .iter()
            .filter_map(|ip| canonical(Some(ip)))
            .collect()
    }

    fn spared(&self, accounts: &AccountIndex) -> IndexSet<String> {
        let mut spared = self.never_block.clone();
        for master in self.masters.masters().iter() {
            for raw in accounts
                .by_name
                .get(master)
                .map(|a| a.confirmed_ips.as_slice())
                .unwrap_or_default()
            {
                if let Some(ip) = canonical(Some(raw)) {
                    spared.insert(ip);
                }
            }
        }
        spared.extend(self.manual_blocks());
        spared
    }

    fn addresses_of<'a>(
        ban: &'a BanRecord,
        accounts: &'a AccountIndex,
    ) -> impl Iterator<Item = &'a String> {
        let account = ban
            .unique_id
            .as_deref()
            .filter(|id| !id.is_empty())
            .and_then(|id| accounts.by_id.get(id))
            .or_else(|| accounts.by_name.get(&ban.player_id));
        account
            .map(|a| a.confirmed_ips.as_slice())
            .unwrap_or_default()
            .iter()
            .chain(
                ban.network
                    .as_ref()
                    .map(|n| n.ips.as_slice())
                    .unwrap_or_default(),
            )
    }

    /// Every address that should be denied right now, and whose ban it is.
    #[must_use]
    pub fn wanted(&self) -> IndexMap<String, String> {
        let mut wanted = IndexMap::new();
        if !self.enabled {
            return wanted;
        }
        // One read of the accounts per pass, not one per ban: this runs every thirty seconds.
        let accounts = AccountIndex::new(self.tracking.accounts());
        let spared = self.spared(&accounts);
        for ban in self.bans.active_bans() {
            let name = &ban.player_id;
            if self.masters.is_master(name)
                || self.masters.is_protected(name)
                || self.masters.is_exempt(name)
            {
                continue;
            }
            for raw in Self::addresses_of(&ban, &accounts) {
                let Some(ip) = canonical(Some(raw)) else {
                    continue;
                };
                if spared.contains(&ip) || !is_public(&ip) {
                    continue;
                }
                wanted.entry(ip).or_insert_with(|| name.clone());
            }
        }
        wanted
    }

    /// The rules this feature owns.
    #[must_use]
    pub fn managed(&self) -> Managed {
        self.store
            .read(FIREWALL_BANS, Managed::new())
            .unwrap_or_default()
    }

    fn due(&self, ip: &str, now: DateTime<Utc>) -> bool {
        self.failed_at
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .get(ip)
            .is_none_or(|at| now - *at >= RETRY_AFTER)
    }

    fn failed(&self, ip: &str, now: DateTime<Utc>, action: &str, detail: &str) {
        self.failed_at
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(ip.to_owned(), now);
        tracing::error!(
            ip,
            action,
            detail,
            retry_minutes = RETRY_AFTER.num_minutes(),
            "FIREWALL FAILED"
        );
    }

    fn succeeded(&self, ip: &str) {
        self.failed_at
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .remove(ip);
    }

    async fn forget(&self, ip: &str) {
        let _ = self
            .store
            .update(FIREWALL_BANS, Managed::new, |mut rules: Managed| {
                rules.shift_remove(ip).map(|_| rules)
            })
            .await;
    }

    /// Lift what should no longer be denied, then deny what should be.
    pub async fn sync(&self) -> BanFirewallPass {
        let now = self.clock.now();
        let wanted = self.wanted();
        let managed = self.managed();
        let manual = self.manual_blocks();
        let mut pass = BanFirewallPass::default();
        let mut budget = MAX_CHANGES_PER_PASS;

        for (ip, rule) in managed.iter().filter(|(ip, _)| !wanted.contains_key(*ip)) {
            // An owner's manual block now owns this address; its rule is not ours to lift.
            if manual.contains(ip) {
                self.forget(ip).await;
                continue;
            }
            if !self.due(ip, now) {
                continue;
            }
            if budget == 0 {
                pass.deferred += 1;
                continue;
            }
            budget -= 1;
            let result = self.firewall.undeny(ip).await;
            if result.ok {
                self.forget(ip).await;
                self.succeeded(ip);
                pass.lifted += 1;
                tracing::warn!(ip, player = %rule.player, "FIREWALL LIFT - the ban has ended");
            } else {
                self.failed(ip, now, "remove the deny for", &result.detail);
                pass.failed += 1;
            }
        }

        for (ip, player) in wanted.iter().filter(|(ip, _)| !managed.contains_key(*ip)) {
            if !self.due(ip, now) {
                continue;
            }
            if budget == 0 {
                pass.deferred += 1;
                continue;
            }
            budget -= 1;
            let result = self.firewall.deny(ip).await;
            if result.ok {
                // Recorded at once, so a crash between two denies cannot orphan a rule.
                let rule = FirewalledBan {
                    player: player.clone(),
                    at: now,
                };
                if let Err(error) = self
                    .store
                    .update(FIREWALL_BANS, Managed::new, |mut rules: Managed| {
                        rules.insert(ip.clone(), rule);
                        Some(rules)
                    })
                    .await
                {
                    tracing::error!(ip, %error, "denied but could not record the rule - it will not be lifted automatically");
                }
                self.succeeded(ip);
                pass.denied += 1;
                tracing::warn!(ip, player = %player, "FIREWALL DENY - banned player");
            } else {
                self.failed(ip, now, "deny", &result.detail);
                pass.failed += 1;
            }
        }

        if pass.deferred > 0 {
            tracing::info!(
                deferred = pass.deferred,
                limit = MAX_CHANGES_PER_PASS,
                "ban firewall changes deferred to the next pass"
            );
        }
        pass
    }
}
