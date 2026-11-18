//! From the log to the feeds, the VPN screening and the alerts (C# `Logs/FeedBridge.cs`).
//!
//! DETECTION NEVER WAITS ON A DISPLAY SETTING: VPN screening, the auto-ban, the alt alert and
//! the kill count all run whether or not a webhook is configured for them. An unset
//! `CONNECT_WEBHOOK_URL` once switched VPN auto-ban off entirely.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, PoisonError};

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::cards::connect::{self, ConnectCardInput};
use pavlov_core::logs::{KillEvent, StatsKill};

use super::{IpTrackingService, PlayerConfirmed, PlayerJoined, RconLogged};
use crate::feeds::{CONNECT, FeedWebhooks};
use crate::kill_stats::KillStats;
use crate::moderation::{
    MasterNames, SecurityAlert, SecurityAlertKind, SecurityAlerts, VpnResponder,
};
use crate::server_labels::ServerLabels;
use crate::vpn::VpnScreeningService;

/// One leave line per account per window: Pavlov writes several disconnect lines.
const CONFIRM_DEBOUNCE: TimeDelta = TimeDelta::seconds(20);
/// The same alt pair is reported once a day, not on every reconnect.
const ALT_ALERT_WINDOW: TimeDelta = TimeDelta::hours(24);
const REPORT_SWEEP_AT: usize = 1000;

/// Whether to report `key` now (and remember it). Case-insensitive; bounded.
pub fn report(
    reported: &mut HashMap<String, DateTime<Utc>>,
    key: &str,
    at: DateTime<Utc>,
    window: TimeDelta,
) -> bool {
    let key = key.to_lowercase();
    if reported.get(&key).is_some_and(|last| at - *last < window) {
        return false;
    }
    reported.insert(key, at);
    if reported.len() > REPORT_SWEEP_AT {
        reported.retain(|_, last| at - *last <= window);
    }
    true
}

/// What the bridge is wired to. Everything but the tracker, the feeds and the labels is
/// optional: a deployment can run with no VPN screening at all.
#[derive(Default)]
pub struct FeedBridgeParts {
    pub vpn: Option<Arc<VpnScreeningService>>,
    pub masters: Option<Arc<dyn MasterNames>>,
    pub vpn_bans: Option<Arc<VpnResponder>>,
    pub alerts: Option<Arc<SecurityAlerts>>,
    pub kill_stats: Option<Arc<KillStats>>,
}

impl std::fmt::Debug for FeedBridgeParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FeedBridgeParts")
            .field("vpn", &self.vpn.is_some())
            .field("vpn_bans", &self.vpn_bans.is_some())
            .finish_non_exhaustive()
    }
}

/// The bridge.
pub struct FeedBridge {
    tracking: Arc<IpTrackingService>,
    feeds: Arc<FeedWebhooks>,
    servers: Arc<ServerLabels>,
    parts: FeedBridgeParts,
    reported: Mutex<HashMap<String, DateTime<Utc>>>,
    alts_reported: Mutex<HashMap<String, DateTime<Utc>>>,
    announced: Mutex<HashMap<String, DateTime<Utc>>>,
    saw_disconnect: AtomicBool,
    warned_nameless: AtomicBool,
    warned_no_connect_feed: AtomicBool,
}

impl std::fmt::Debug for FeedBridge {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FeedBridge").finish_non_exhaustive()
    }
}

fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(PoisonError::into_inner)
}

impl FeedBridge {
    /// The bridge, subscribed to the tracker's joins, confirmations, kills and RCON lines.
    #[must_use]
    pub fn attach(
        tracking: Arc<IpTrackingService>,
        feeds: Arc<FeedWebhooks>,
        servers: Arc<ServerLabels>,
        parts: FeedBridgeParts,
    ) -> Arc<Self> {
        let bridge = Arc::new(Self {
            tracking: Arc::clone(&tracking),
            feeds,
            servers,
            parts,
            reported: Mutex::new(HashMap::new()),
            alts_reported: Mutex::new(HashMap::new()),
            announced: Mutex::new(HashMap::new()),
            saw_disconnect: AtomicBool::new(false),
            warned_nameless: AtomicBool::new(false),
            warned_no_connect_feed: AtomicBool::new(false),
        });
        tracking
            .joined
            .subscribe(with(&bridge, |b, join: PlayerJoined| async move {
                b.on_joined(join).await
            }));
        tracking
            .confirmed
            .subscribe(with(&bridge, |b, c: PlayerConfirmed| async move {
                b.on_confirmed(c).await
            }));
        tracking
            .kill
            .subscribe(with(&bridge, |b, kill: KillEvent| async move {
                b.on_kill(&kill)
            }));
        tracking.rcon.subscribe(with(
            &bridge,
            |b, r: RconLogged| async move { b.on_rcon(&r) },
        ));
        bridge
    }

    fn on_rcon(&self, logged: &RconLogged) {
        let a = &logged.action;
        self.feeds.post_rcon(
            a.plus,
            &a.verb,
            a.instigator.as_deref(),
            a.argument.as_deref(),
            Some(&self.servers.of(&logged.file)),
            logged.at,
            a.successful,
        );
    }

    /// A kill from Stats.log (the better source, when it is read).
    pub fn on_stats_kill(&self, kill: &StatsKill) {
        if let Some(stats) = &self.parts.kill_stats {
            stats.record(
                &KillEvent {
                    killer: Some(kill.killer.clone()),
                    killed: Some(kill.killed.clone()),
                    killed_by: kill.weapon.clone(),
                },
                kill.at,
            );
        }
        self.feeds.post_kill(
            Some(&kill.killer),
            &kill.killed,
            kill.weapon.as_deref(),
            kill.at,
            kill.headshot,
        );
    }

    async fn on_joined(&self, join: PlayerJoined) {
        let name = join.name.clone().unwrap_or_else(|| join.account_id.clone());
        // One line per arrival: Pavlov writes several login lines. Gates the screening too.
        if !report(&mut lock(&self.announced), &name, join.at, CONFIRM_DEBOUNCE) {
            return;
        }
        self.feeds
            .post_join(&name, Some(&self.servers.of(&join.file)), join.at);
        let (Some(vpn), Some(vpn_bans)) = (&self.parts.vpn, &self.parts.vpn_bans) else {
            return;
        };
        let (Some(named), Some(ip)) = (
            join.name.as_deref().filter(|n| !n.is_empty()),
            join.ip.as_deref().filter(|i| !i.is_empty()),
        ) else {
            return;
        };
        if !join.confident {
            return;
        }
        // Cached per address: a lookup the first time, free for every join after.
        if let Some(screening) = vpn.check(ip).await {
            vpn_bans
                .respond(named, Some(&screening), Some(&join.account_id), "join")
                .await;
        }
    }

    async fn on_confirmed(&self, confirmed: PlayerConfirmed) {
        if !self.saw_disconnect.swap(true, Ordering::Relaxed) {
            tracing::info!(
                "first disconnect line seen - leave lines and the connection feed are live"
            );
        }
        /* No recorded name is a partial connection (dropped before a login line named them):
        "unknown left Server 1" is noise. Said once, because from the channel "leaves are
        broken" and "this account is genuinely anonymous" look the same. */
        let Some(name) = confirmed.name.clone().filter(|n| !n.is_empty()) else {
            if !self.warned_nameless.swap(true, Ordering::Relaxed) {
                tracing::info!(account = %confirmed.account_id, "disconnect skipped - no name recorded for it yet; normal for a player already connected when the bot started, but if EVERY leave is missing, no login line has ever matched this account id");
            }
            return;
        };
        if !report(
            &mut lock(&self.reported),
            &confirmed.account_id,
            confirmed.at,
            CONFIRM_DEBOUNCE,
        ) {
            return;
        }
        let server = self.servers.of(&confirmed.file);
        self.feeds.post_leave(&name, Some(&server), confirmed.at);

        // SCREENED UNCONDITIONALLY, before anything can return. The address is certain here.
        let screening = match &self.parts.vpn {
            Some(vpn) => vpn.check(&confirmed.ip).await,
            None => None,
        };
        // A detection, not a display setting: outside the connect-feed check.
        self.alert_on_alts(&confirmed, &name, &server).await;

        if self.feeds.is_configured(CONNECT) {
            self.post_card(&confirmed, &name, &server, screening.as_ref());
        } else if !self.warned_no_connect_feed.swap(true, Ordering::Relaxed) {
            tracing::warn!(
                "a player disconnected and the connection card was not posted: CONNECT_WEBHOOK_URL is not set (or not a valid webhook address). Leave lines and VPN screening - auto-ban included - are unaffected. Run /feeds to see every feed's state"
            );
        }

        /* ACT on the verdict, after the card (if the ban fails, staff still have the evidence),
        never conditional on it. The backstop for a join whose address was not confidently
        correlated; where the join already acted, the responder's window makes this a no-op. */
        if let (Some(vpn_bans), Some(screening)) = (&self.parts.vpn_bans, &screening) {
            vpn_bans
                .respond(
                    &name,
                    Some(screening),
                    Some(&confirmed.account_id),
                    "disconnect",
                )
                .await;
        }
    }

    fn post_card(
        &self,
        confirmed: &PlayerConfirmed,
        name: &str,
        server: &str,
        screening: Option<&pavlov_core::vpn::VpnRecord>,
    ) {
        // Read only for the card, so a deployment with no connect feed never loads it.
        let flags = self.tracking.load_flags();
        let flagged = flags.ips.contains(&confirmed.ip)
            || flags.ids.contains(&confirmed.account_id)
            || flags.names.contains(name);
        let account = self.tracking.account(&confirmed.account_id);
        let alts = self.tracking.alts_of(&confirmed.account_id);
        let card = connect::build(&ConnectCardInput {
            name,
            account_id: &confirmed.account_id,
            ip: Some(&confirmed.ip),
            confident_ip: true,
            server,
            account: account.as_ref(),
            alts: &alts,
            vpn: screening,
            flagged,
            master: self
                .parts
                .masters
                .as_ref()
                .is_some_and(|m| m.is_master(name)),
            at: confirmed.at,
        });
        self.feeds.post_embed(CONNECT, card);
    }

    async fn alert_on_alts(&self, confirmed: &PlayerConfirmed, name: &str, server: &str) {
        let Some(alerts) = self.parts.alerts.as_ref().filter(|a| a.enabled()) else {
            return;
        };
        let alts = self.tracking.alts_of(&confirmed.account_id);
        if alts.is_empty()
            || !report(
                &mut lock(&self.alts_reported),
                &confirmed.account_id,
                confirmed.at,
                ALT_ALERT_WINDOW,
            )
        {
            return;
        }
        let alert = SecurityAlert {
            kind: SecurityAlertKind::Alts,
            player: name.to_owned(),
            headline: format!("They share an address with {} other account(s).", alts.len()),
            action: "Nothing. Linked accounts are not evasion on their own - this is for a human to look at.".into(),
            account_id: Some(confirmed.account_id.clone()),
            ip: Some(confirmed.ip.clone()),
            server: Some(server.to_owned()),
            alts: alts.iter().map(|a| a.name().map_or_else(|| a.id.clone(), str::to_owned)).collect(),
            at: Some(confirmed.at),
        };
        alerts.post(&alert).await;
    }

    fn on_kill(&self, kill: &KillEvent) {
        // Assembled from fragments, so it has no timestamp of its own: now is within a poll.
        let Some(killed) = kill.killed.as_deref().filter(|k| !k.is_empty()) else {
            return;
        };
        let now = Utc::now();
        // COUNTED BEFORE IT IS POSTED, outside the feed guard.
        if let Some(stats) = &self.parts.kill_stats {
            stats.record(kill, now);
        }
        self.feeds.post_kill(
            kill.killer.as_deref(),
            killed,
            kill.killed_by.as_deref(),
            now,
            false,
        );
    }
}

/// A subscriber holding the bridge STRONGLY: the tracker keeps it alive, so a caller that
/// drops the handle cannot silently unsubscribe it (the C# once shipped with Flagged unheard).
/// Both live for the whole process, so the cycle costs nothing.
fn with<T, F, Fut>(
    bridge: &Arc<FeedBridge>,
    f: F,
) -> impl Fn(T) -> futures_util::future::BoxFuture<'static, ()> + Send + Sync + 'static
where
    T: Send + 'static,
    F: Fn(Arc<FeedBridge>, T) -> Fut + Send + Sync + 'static + Clone,
    Fut: std::future::Future<Output = ()> + Send + 'static,
{
    let bridge = Arc::clone(bridge);
    move |value: T| {
        let bridge = Arc::clone(&bridge);
        let f = f.clone();
        Box::pin(async move { f(bridge, value).await })
    }
}
