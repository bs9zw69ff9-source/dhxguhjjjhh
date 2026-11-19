//! Acting on a VPN verdict (C# `Moderation/VpnResponder.cs`).
//!
//! The order is the policy: no verdict, then BELOW THRESHOLD (said out loud: it is what
//! somebody checks when a known VPN user is still on), then the `VPN_AUTOBAN` switch (checked
//! after the verdict, so with it off the would-be ban is still reported), then the
//! protections, then an existing ban (never rewritten: that would promote a temp ban to
//! permanent), then the per-address debounce, and only then the ban.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::{BanRecord, BanRules};
use pavlov_core::text::Sanitize;
use pavlov_core::time::Clock;
use pavlov_core::vpn::{VpnRecord, verdict};
use pavlov_storage::datasets::TEMP_BANS;

use super::alerts::{SecurityAlert, SecurityAlertKind, SecurityAlerts};
use super::audit::AuditLog;
use super::bans::{BanService, MasterNames};
use crate::feeds::{CONNECT, FeedWebhooks};
use crate::metrics::{MetricLabels, MetricsRegistry};

const ACTION_DEBOUNCE: TimeDelta = TimeDelta::hours(1);
/// Past this many remembered addresses, the stale ones are swept.
const DEBOUNCE_SWEEP_AT: usize = 500;

/// What was done.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VpnBanOutcome {
    NoVerdict,
    BelowThreshold,
    Master,
    Disabled,
    Exempt,
    AlreadyBanned,
    Debounced,
    Banned,
}

/// The ban reason: the verdict first, worded as every other surface words it, because it is
/// what gets quoted back in an appeal.
#[must_use]
pub fn label(record: &VpnRecord) -> String {
    let summary = verdict::of(Some(record));
    let brand = record
        .provider
        .as_deref()
        .filter(|p| !p.is_empty())
        .map(|p| format!(" ({})", Sanitize::message(Some(p))))
        .unwrap_or_default();
    format!("{}{brand} — {}", summary.headline, summary.plain)
}

/// Whether to act on this address now (and remember that it was). Bounded: past 500 entries
/// the expired ones are swept.
pub fn should_action(
    actioned: &mut HashMap<String, DateTime<Utc>>,
    ip: &str,
    now: DateTime<Utc>,
    window: TimeDelta,
) -> bool {
    if actioned.get(ip).is_some_and(|last| now - *last < window) {
        return false;
    }
    actioned.insert(ip.to_owned(), now);
    if actioned.len() > DEBOUNCE_SWEEP_AT {
        actioned.retain(|_, at| now - *at <= window);
    }
    true
}

/// The responder.
pub struct VpnResponder {
    bans: Arc<BanService>,
    masters: Arc<dyn MasterNames>,
    store: Arc<SerializedStore>,
    audit: Arc<AuditLog>,
    feeds: Arc<FeedWebhooks>,
    metrics: Arc<MetricsRegistry>,
    clock: Arc<dyn Clock>,
    auto_ban: bool,
    alerts: Option<Arc<SecurityAlerts>>,
    actioned: Mutex<HashMap<String, DateTime<Utc>>>,
    reported: Mutex<HashMap<String, DateTime<Utc>>>,
}

impl std::fmt::Debug for VpnResponder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("VpnResponder")
            .field("auto_ban", &self.auto_ban)
            .finish_non_exhaustive()
    }
}

impl VpnResponder {
    #[allow(clippy::too_many_arguments)]
    #[must_use]
    pub fn new(
        bans: Arc<BanService>,
        masters: Arc<dyn MasterNames>,
        store: Arc<SerializedStore>,
        audit: Arc<AuditLog>,
        feeds: Arc<FeedWebhooks>,
        metrics: Arc<MetricsRegistry>,
        clock: Arc<dyn Clock>,
        auto_ban: bool,
        alerts: Option<Arc<SecurityAlerts>>,
    ) -> Self {
        Self {
            bans,
            masters,
            store,
            audit,
            feeds,
            metrics,
            clock,
            auto_ban,
            alerts,
            actioned: Mutex::new(HashMap::new()),
            reported: Mutex::new(HashMap::new()),
        }
    }

    fn count(&self, outcome: &str, stage: &str) {
        self.metrics.increment(
            "vpn_bans_total",
            &MetricLabels::of2("outcome", outcome, "stage", stage),
            1.0,
            Some("VPN verdicts by what was done about them"),
        );
    }

    /// Respond to one screened connection. `stage` is where it came from (the join line or the
    /// disconnect), for the log and the metric.
    pub async fn respond(
        &self,
        player: &str,
        record: Option<&VpnRecord>,
        unique_id: Option<&str>,
        stage: &str,
    ) -> VpnBanOutcome {
        let Some(record) = record.filter(|_| !player.trim().is_empty()) else {
            return VpnBanOutcome::NoVerdict;
        };
        let decision = &record.decision;
        if !decision.flagged {
            return VpnBanOutcome::NoVerdict;
        }
        let now = self.clock.now();
        // SAID ONCE PER ADDRESS: the join and the disconnect both reach here for one session.
        let announce = should_action(
            &mut self.reported.lock().unwrap_or_else(PoisonError::into_inner),
            &record.ip,
            now,
            ACTION_DEBOUNCE,
        );
        let headline = verdict::of(Some(record)).headline;

        if !decision.actionable {
            if announce {
                tracing::info!(player, ip = %record.ip, stage, reason = %decision.reason, "VPN flag NOT actioned");
                self.post(&format!(
                    "[VPN] {}  |  {headline}  |  NOT BANNED - {}",
                    Sanitize::message(Some(player)),
                    decision.reason
                ));
                self.alert(
                    player,
                    record,
                    stage,
                    &format!("Not banned - under the threshold. {}", decision.reason),
                    unique_id,
                )
                .await;
            }
            self.count("below_threshold", stage);
            return VpnBanOutcome::BelowThreshold;
        }
        if !self.auto_ban {
            if announce {
                tracing::info!(player, ip = %record.ip, stage, reason = %decision.reason, "VPN auto-ban is OFF (VPN_AUTOBAN) - this would have been a ban");
                self.post(&format!(
                    "[VPN] {}  |  {headline}  |  NOT BANNED - automatic VPN banning is off (VPN_AUTOBAN)",
                    Sanitize::message(Some(player))
                ));
                self.alert(
                    player,
                    record,
                    stage,
                    "Not banned - automatic VPN banning is off (VPN_AUTOBAN).",
                    unique_id,
                )
                .await;
            }
            self.count("disabled", stage);
            return VpnBanOutcome::Disabled;
        }
        if self.masters.is_master(player) {
            // An owner on a hotspot, not an evader. Loud, since a lockout needs a console.
            if announce {
                tracing::warn!(player, reason = %decision.reason, "VPN AUTO-BAN REFUSED - a master account");
                self.alert(
                    player,
                    record,
                    stage,
                    "Not banned - they are a master account.",
                    unique_id,
                )
                .await;
            }
            self.count("master", stage);
            return VpnBanOutcome::Master;
        }
        if self.masters.is_protected(player) {
            tracing::warn!(player, reason = %decision.reason, "VPN AUTO-BAN REFUSED - on the never-ban list");
            return VpnBanOutcome::Master;
        }
        if self.masters.is_exempt(player) {
            if announce {
                tracing::info!(player, "VPN auto-ban skipped - exempt");
            }
            self.count("exempt", stage);
            return VpnBanOutcome::Exempt;
        }
        if self
            .bans
            .active_bans()
            .iter()
            .any(|b| BanRules::same_player(Some(&b.player_id), Some(player)))
        {
            self.count("already_banned", stage);
            return VpnBanOutcome::AlreadyBanned;
        }
        let act = should_action(
            &mut self.actioned.lock().unwrap_or_else(PoisonError::into_inner),
            &record.ip,
            now,
            ACTION_DEBOUNCE,
        );
        if !act {
            tracing::info!(player, stage, ip = %record.ip, "VPN auto-ban skipped - the address was actioned recently");
            self.count("debounced", stage);
            return VpnBanOutcome::Debounced;
        }

        let reason = label(record);
        let ban = BanRecord {
            // The certain id from the same line, rather than re-derived from the name twice.
            unique_id: unique_id.map(str::to_owned),
            reason: Some(Sanitize::message(Some(&reason))),
            moderator: Some("auto".into()),
            at: Some(now),
            expires: None,
            permanent: true,
            duration_label: Some("Permanent".into()),
            tier: None,
            ..BanRecord::for_player(player)
        };
        if let Err(error) = self
            .store
            .update(TEMP_BANS, Vec::new, |mut list: Vec<BanRecord>| {
                list.retain(|b| !BanRules::same_player(Some(&b.player_id), Some(player)));
                list.push(ban);
                Some(list)
            })
            .await
        {
            tracing::error!(player, %error, "could not record the VPN ban");
        }
        let enforcement = self.bans.hard_enforce(player, unique_id, true, true).await;
        if let Err(error) = self
            .audit
            .record("vpnban", "auto", player, Some(&reason))
            .await
        {
            tracing::error!(player, %error, "could not write the VPN ban to the audit log");
        }
        self.count("banned", stage);
        tracing::warn!(player, ip = %record.ip, stage, kicked_on = enforcement.servers, save = enforcement.summary(), reason = %reason, "VPN AUTO-BANNED");
        self.post(&format!(
            "[VPN BAN] {}  |  {reason}",
            Sanitize::message(Some(player))
        ));
        self.alert(
            player,
            record,
            stage,
            &format!("Banned permanently: {}.", enforcement.summary()),
            unique_id,
        )
        .await;
        VpnBanOutcome::Banned
    }

    fn post(&self, line: &str) {
        self.feeds.post(CONNECT, line);
    }

    async fn alert(
        &self,
        player: &str,
        record: &VpnRecord,
        stage: &str,
        action: &str,
        unique_id: Option<&str>,
    ) {
        let Some(alerts) = self.alerts.as_ref().filter(|a| a.enabled()) else {
            return;
        };
        let summary = verdict::of(Some(record));
        let network = record
            .organization
            .as_deref()
            .or(record.isp.as_deref())
            .filter(|w| !w.is_empty())
            .map(|w| format!(" Network: {}.", Sanitize::message(Some(w))))
            .unwrap_or_default();
        let alert = SecurityAlert {
            kind: SecurityAlertKind::Vpn,
            player: player.to_owned(),
            headline: format!("{} on {stage}.{network}", summary.headline),
            action: action.to_owned(),
            account_id: unique_id.map(str::to_owned),
            ip: Some(record.ip.clone()),
            server: None,
            alts: Vec::new(),
            at: Some(self.clock.now()),
        };
        alerts.post(&alert).await;
    }
}
