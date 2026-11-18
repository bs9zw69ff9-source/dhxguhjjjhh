//! Auto-banning a join that matched a standing flag (C# `Moderation/EvasionResponder.cs`).
//!
//! - A master, a never-ban player or an exempt one is never banned, and a master or
//!   never-ban catch is still ALERTED: a protection that keeps firing needs a human.
//! - A SERVED BAN'S OWN LEFTOVERS ARE NOT EVASION: they are lifted, never escalated to
//!   permanent (an owner's name flag still bans: it has no expiry to have served).
//! - THE ACCOUNT ID AND NOTHING ELSE is flagged. Flagging the caught account's addresses and
//!   every account that ever shared one made one match into a spreading feedback loop.

use std::sync::Arc;

use pavlov_core::data::SerializedStore;
use pavlov_core::evasion::FlagMatch;
use pavlov_core::moderation::{AutoBanAction, BanRecord, BanRules};
use pavlov_core::text::Sanitize;
use pavlov_core::time::Clock;
use pavlov_storage::datasets::TEMP_BANS;

use super::alerts::{SecurityAlert, SecurityAlertKind, SecurityAlerts};
use super::audit::AuditLog;
use super::bans::{BanService, MasterNames};
use crate::feeds::{CONNECT, FeedWebhooks};
use crate::logs::{FlaggedJoin, IpTrackingService};
use crate::metrics::{MetricLabels, MetricsRegistry};

/// What the responder did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AutoBanOutcome {
    NoName,
    Master,
    Protected,
    Exempt,
    Served,
    EnforcedExisting,
    Banned,
}

fn match_name(m: FlagMatch) -> &'static str {
    match m {
        FlagMatch::None => "None",
        FlagMatch::Ip => "Ip",
        FlagMatch::Name => "Name",
        FlagMatch::AccountId => "AccountId",
    }
}

/// The responder.
pub struct EvasionResponder {
    tracking: Arc<IpTrackingService>,
    bans: Arc<BanService>,
    masters: Arc<dyn MasterNames>,
    store: Arc<SerializedStore>,
    audit: Arc<AuditLog>,
    feeds: Arc<FeedWebhooks>,
    metrics: Arc<MetricsRegistry>,
    clock: Arc<dyn Clock>,
    alerts: Option<Arc<SecurityAlerts>>,
}

impl std::fmt::Debug for EvasionResponder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("EvasionResponder").finish_non_exhaustive()
    }
}

impl EvasionResponder {
    /// The responder, subscribed to the tracker's flagged joins.
    #[allow(clippy::too_many_arguments)]
    #[must_use]
    pub fn attach(
        tracking: Arc<IpTrackingService>,
        bans: Arc<BanService>,
        masters: Arc<dyn MasterNames>,
        store: Arc<SerializedStore>,
        audit: Arc<AuditLog>,
        feeds: Arc<FeedWebhooks>,
        metrics: Arc<MetricsRegistry>,
        clock: Arc<dyn Clock>,
        alerts: Option<Arc<SecurityAlerts>>,
    ) -> Arc<Self> {
        let responder = Arc::new(Self {
            tracking: Arc::clone(&tracking),
            bans,
            masters,
            store,
            audit,
            feeds,
            metrics,
            clock,
            alerts,
        });
        /* STRONG: the tracker keeps the responder alive, so dropping the handle cannot silently
        unsubscribe it - "flagged and nothing listened" is the bug this exists for. */
        let held = Arc::clone(&responder);
        tracking.flagged.subscribe(move |join| {
            let responder = Arc::clone(&held);
            Box::pin(async move {
                responder.respond(&join).await;
            })
        });
        responder
    }

    fn refused(&self, reason: &str) {
        self.metrics.increment(
            "autoban_refused_total",
            &MetricLabels::of("reason", reason),
            1.0,
            Some("Auto-bans refused by a protection"),
        );
    }

    /// Respond to one flagged join.
    pub async fn respond(&self, join: &FlaggedJoin) -> AutoBanOutcome {
        // The display name is what RCON bans by; the account id is what identifies them.
        let name = join
            .name
            .clone()
            .or_else(|| {
                self.tracking
                    .account(&join.account_id)
                    .and_then(|a| a.name().map(str::to_owned))
            })
            .filter(|n| !n.trim().is_empty());
        let Some(name) = name else {
            // A ban nobody can enforce would be a list entry that never removes anybody.
            tracing::warn!(account = %join.account_id, "flagged join has no known name - cannot enforce");
            return AutoBanOutcome::NoName;
        };
        let detail = join
            .verdict
            .detail
            .clone()
            .unwrap_or_else(|| match_name(join.verdict.matched).to_owned());

        if self.masters.is_master(&name) {
            tracing::warn!(name = %name, detail = %detail, "AUTO-BAN REFUSED - a master account matched a flag; check whether that flag should exist at all");
            self.refused("master");
            self.alert(
                &name,
                join,
                "Matched a standing flag, but they are a master account. Nothing was done.",
            )
            .await;
            return AutoBanOutcome::Master;
        }
        if self.masters.is_protected(&name) {
            tracing::warn!(name = %name, detail = %detail, "AUTO-BAN REFUSED - on the never-ban list");
            self.refused("never-ban");
            self.alert(
                &name,
                join,
                "Matched a standing flag, but they are on the never-ban list. Nothing was done.",
            )
            .await;
            return AutoBanOutcome::Protected;
        }
        if self.masters.is_exempt(&name) {
            tracing::info!(name = %name, "auto-ban skipped - exempt after serving a ban");
            self.refused("exempt");
            return AutoBanOutcome::Exempt;
        }

        let now = self.clock.now();
        let reason = format!("Ban evasion - {detail}");
        /* THE SERVED-BAN CHECK: from expiry until the sweep, a temp ban's own leftover flags
        look exactly like evasion, and banning there turns the days served into forever. */
        let existing = self
            .bans
            .load_bans()
            .into_iter()
            .filter(|b| BanRules::same_player(Some(&b.player_id), Some(&name)))
            .max_by_key(|b| b.at);
        if BanRules::auto_ban_decision(existing.as_ref(), join.verdict.detail.as_deref(), now)
            == AutoBanAction::Lift
        {
            return self.release_served(&name, join, &detail).await;
        }

        let already = self
            .bans
            .active_bans()
            .iter()
            .any(|b| BanRules::same_player(Some(&b.player_id), Some(&name)));
        if !already {
            let record = BanRecord {
                // The id it is enforced against, so a later lift names the same thing.
                unique_id: Some(join.account_id.clone()),
                reason: Some(Sanitize::message(Some(&reason))),
                moderator: Some("auto".into()),
                at: Some(now),
                expires: None,
                permanent: true,
                duration_label: Some("Permanent".into()),
                // No staff tier: an automated ban must not outrank a human.
                tier: None,
                ..BanRecord::for_player(name.clone())
            };
            let write = self
                .store
                .update(TEMP_BANS, Vec::new, |mut bans: Vec<BanRecord>| {
                    // Replace, never append: a second record re-catches them after an unban.
                    bans.retain(|b| !BanRules::same_player(Some(&b.player_id), Some(&name)));
                    bans.push(record);
                    Some(bans)
                })
                .await;
            if let Err(error) = write {
                tracing::error!(name = %name, %error, "could not record the auto-ban");
                self.metrics.increment(
                    "autoban_errors_total",
                    &MetricLabels::none(),
                    1.0,
                    Some("Auto-ban attempts that threw"),
                );
            }
            // The account id is evidence; an address flag is a human's call.
            self.tracking.flag_account(&join.account_id).await;
        }

        let enforcement = self
            .bans
            .hard_enforce(&name, Some(&join.account_id), true, true)
            .await;
        if let Err(error) = self
            .audit
            .record("autoban", "auto", &name, Some(&reason))
            .await
        {
            tracing::error!(name = %name, %error, "could not write the auto-ban to the audit log");
        }
        self.metrics.increment(
            "autobans_total",
            &MetricLabels::of("match", match_name(join.verdict.matched)),
            1.0,
            Some("Auto-bans issued for a flagged join"),
        );
        tracing::warn!(name = %name, account = %join.account_id, servers = enforcement.servers, reason = %reason, "AUTO-BANNED");
        // Beside the join it responded to, where staff watch joins. Which list the flag came
        // from changes the answer to "why was this person banned".
        self.feeds.post(
            CONNECT,
            &format!(
                "[AUTO-BAN] {}  |  {detail} ({}){}",
                Sanitize::message(Some(&name)),
                if join.verdict.manual {
                    "set by an owner"
                } else {
                    "from a ban"
                },
                if already {
                    "  |  already banned, removed again"
                } else {
                    ""
                }
            ),
        );
        self.alert(
            &name,
            join,
            if already {
                "Already banned. The ban was enforced again on every server."
            } else {
                "Banned permanently, on every server."
            },
        )
        .await;
        if already {
            AutoBanOutcome::EnforcedExisting
        } else {
            AutoBanOutcome::Banned
        }
    }

    async fn release_served(&self, name: &str, join: &FlaggedJoin, detail: &str) -> AutoBanOutcome {
        tracing::warn!(name, account = %join.account_id, detail, "AUTO-BAN REFUSED - their own served ban's leftovers; lifting it rather than escalating to permanent");
        self.refused("served");
        // Never fatal: a failed lift only means the next connection takes this branch again.
        let _ = self.bans.lift(name, Some(&join.account_id)).await;
        if let Err(error) = self
            .audit
            .record(
                "autoban-released",
                "auto",
                name,
                Some(&format!(
                    "Served ban's flags cleared instead of re-banning - {detail}"
                )),
            )
            .await
        {
            tracing::error!(name, %error, "could not write the release to the audit log");
        }
        self.feeds.post(
            CONNECT,
            &format!(
                "[AUTO-BAN SKIPPED] {}  |  {detail}  |  their own served ban, lifted instead of re-banning",
                Sanitize::message(Some(name))
            ),
        );
        self.alert(name, join, "Their own served ban's leftovers, not evasion. The stale flags were lifted rather than re-banning.").await;
        AutoBanOutcome::Served
    }

    async fn alert(&self, name: &str, join: &FlaggedJoin, action: &str) {
        let Some(alerts) = self.alerts.as_ref().filter(|a| a.enabled()) else {
            return;
        };
        let source = if join.verdict.manual {
            "set by an owner"
        } else {
            "left by a previous ban"
        };
        let detail = join
            .verdict
            .detail
            .clone()
            .unwrap_or_else(|| match_name(join.verdict.matched).to_owned());
        let alert = SecurityAlert {
            kind: SecurityAlertKind::Evasion,
            player: name.to_owned(),
            headline: format!("Their join matched {detail} ({source})."),
            action: action.to_owned(),
            account_id: Some(join.account_id.clone()),
            ip: join.ip.clone(),
            server: None,
            alts: self
                .tracking
                .alts_of(&join.account_id)
                .into_iter()
                .map(|a| a.name().map_or_else(|| a.id.clone(), str::to_owned))
                .collect(),
            at: Some(self.clock.now()),
        };
        alerts.post(&alert).await;
    }
}
