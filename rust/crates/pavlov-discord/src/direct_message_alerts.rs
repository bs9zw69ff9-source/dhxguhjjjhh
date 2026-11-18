//! Security alerts as direct messages (C# `DirectMessageAlerts.cs`).
//!
//! A FAILED DELIVERY IS REPORTED, per recipient: a DM to somebody with closed DMs is refused
//! and the sending side otherwise looks perfect while nobody is being told anything, which is
//! indistinguishable from no detections happening.

use std::sync::Arc;

use chrono::Utc;
use futures_util::future::BoxFuture;
use pavlov_core::embed::{Embed, palette};
use pavlov_core::skin::{EmbedBudget, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::time::EasternTime;
use pavlov_host::metrics::{MetricLabels, MetricsRegistry};
use pavlov_host::moderation::{AlertDelivery, SecurityAlert, SecurityAlertKind, SecurityAlertSink};

use crate::guild_directory::GuildDirectory;

const CLOSED_INBOX: &str = "Discord would not deliver it - their DMs are probably closed, or they share no server with the bot";

fn known_value(value: Option<&str>) -> Option<&str> {
    value.filter(|v| !v.is_empty())
}

/// The card one alert becomes. It LEADS WITH WHAT WAS DONE: "VPN detected" and "VPN detected,
/// banned" call for different responses, and the alts are named, not counted.
#[must_use]
pub fn card(alert: &SecurityAlert) -> Embed {
    let who = Sanitize::message(Some(&alert.player));
    let at = alert.at.unwrap_or_else(Utc::now);
    let (glyph, title, color) = match alert.kind {
        SecurityAlertKind::Evasion => (theme::DENY, "Ban evasion", palette::BAN_RED),
        SecurityAlertKind::Alts => ("👥", "Alt accounts", palette::AMBER),
        SecurityAlertKind::Vpn => ("🛰️", "VPN or proxy", palette::AMBER),
    };
    let mut embed = Embed::new()
        .color(color)
        .title(EmbedBudget::truncate(
            &format!("{glyph} {title} — {who}"),
            EmbedBudget::TITLE_LIMIT,
        ))
        .description(EmbedBudget::truncate(
            &Sanitize::message(Some(&alert.headline)),
            2048,
        ));
    let mut budget = EmbedBudget::new(240);
    budget.add(
        &mut embed,
        "What happened",
        Some(&Sanitize::message(Some(&alert.action))),
        false,
    );
    budget.add(
        &mut embed,
        "Account ID",
        Some(&known_value(alert.account_id.as_deref()).map_or_else(
            || "unknown".to_owned(),
            |id| format!("`{}`", Sanitize::message(Some(id))),
        )),
        true,
    );
    budget.add(
        &mut embed,
        "Address",
        Some(
            &known_value(alert.ip.as_deref())
                .map_or_else(|| "unknown".to_owned(), |ip| format!("`{ip}`")),
        ),
        true,
    );
    budget.add(
        &mut embed,
        "Server",
        Some(
            &known_value(alert.server.as_deref())
                .map_or_else(|| "unknown".to_owned(), |s| Sanitize::message(Some(s))),
        ),
        true,
    );
    if !alert.alts.is_empty() {
        let listed: Vec<String> = alert
            .alts
            .iter()
            .take(20)
            .map(|a| format!("{} {}", theme::DOT, Sanitize::markdown(Some(a))))
            .collect();
        budget.add(
            &mut embed,
            &format!("Other accounts on that address ({})", alert.alts.len()),
            Some(&listed.join("\n")),
            false,
        );
    }
    // How to act on it: a DM is read away from a keyboard.
    let next = match alert.kind {
        SecurityAlertKind::Evasion => {
            "`/checkban <name>` for the record, `/unban <name>` if it is wrong."
        }
        SecurityAlertKind::Alts => {
            "`/alts <name>` for the full picture, `/ban <name>` if it is evasion."
        }
        SecurityAlertKind::Vpn => {
            "`/inspect <name>` for the screening, `/unban <name>` if it is a false positive."
        }
    };
    budget.add(&mut embed, "Next", Some(next), false);
    embed.brand(Some(&format!(
        "Security alert — {} Eastern",
        EasternTime::stamp(Some(at))
    )))
}

/// The sink.
pub struct DirectMessageAlerts {
    directory: Arc<dyn GuildDirectory>,
    recipients: Vec<u64>,
    metrics: Arc<MetricsRegistry>,
}

impl std::fmt::Debug for DirectMessageAlerts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DirectMessageAlerts")
            .field("recipients", &self.recipients.len())
            .finish_non_exhaustive()
    }
}

impl DirectMessageAlerts {
    #[must_use]
    pub fn new(
        directory: Arc<dyn GuildDirectory>,
        recipients: Vec<u64>,
        metrics: Arc<MetricsRegistry>,
    ) -> Self {
        Self {
            directory,
            recipients,
            metrics,
        }
    }

    #[must_use]
    pub fn recipients(&self) -> &[u64] {
        &self.recipients
    }

    #[must_use]
    pub fn enabled(&self) -> bool {
        !self.recipients.is_empty()
    }

    fn count(&self, outcome: &str) {
        self.metrics.increment(
            "security_alert_dms_total",
            &MetricLabels::of("outcome", outcome),
            1.0,
            Some("Security alert direct messages by outcome"),
        );
    }

    /// Deliver to every recipient, each tried whatever happened to the last.
    pub async fn deliver(&self, alert: &SecurityAlert) -> Vec<AlertDelivery> {
        if self.recipients.is_empty() {
            return Vec::new();
        }
        let card = card(alert);
        let mut results = Vec::with_capacity(self.recipients.len());
        for &user in &self.recipients {
            match self.directory.send_direct_message(user, card.clone()).await {
                Ok(true) => {
                    self.count("delivered");
                    results.push(AlertDelivery {
                        user,
                        delivered: true,
                        problem: None,
                    });
                }
                Ok(false) => {
                    self.count("refused");
                    // WARNING: a recipient who is never told is the feature not working.
                    tracing::warn!(kind = ?alert.kind, player = %alert.player, user, "security alert was NOT delivered - their DMs are closed to the bot, or they share no server with it");
                    results.push(AlertDelivery {
                        user,
                        delivered: false,
                        problem: Some(CLOSED_INBOX.to_owned()),
                    });
                }
                Err(error) => {
                    self.count("error");
                    tracing::warn!(kind = ?alert.kind, user, %error, "could not DM a security alert");
                    results.push(AlertDelivery {
                        user,
                        delivered: false,
                        problem: Some(error.to_string()),
                    });
                }
            }
        }
        results
    }
}

impl SecurityAlertSink for DirectMessageAlerts {
    fn post<'a>(&'a self, alert: &'a SecurityAlert) -> BoxFuture<'a, Vec<AlertDelivery>> {
        Box::pin(self.deliver(alert))
    }
}
