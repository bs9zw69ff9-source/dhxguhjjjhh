//! Live player counts as voice channel names (C# `PlayerCountChannels.cs`).
//!
//! A voice channel sits in the sidebar, readable without opening anything, and carries no
//! names. DISCORD ALLOWS TWO RENAMES PER TEN MINUTES PER CHANNEL, which shapes all of this:
//! - the tick is slow (six minutes), with room for a retry;
//! - an UNCHANGED NAME IS NOT WRITTEN, so an idle server spends nothing;
//! - a STALE ROSTER IS NOT PUBLISHED: a blip would rename to "0/24" and back, spending both
//!   renames on a number that was never true.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};

use chrono::TimeDelta;
use futures_util::future::BoxFuture;
use pavlov_core::servers::listing::ServerListing;
use pavlov_host::master_list::MasterServerList;
use pavlov_host::registry::RconRegistry;
use pavlov_host::service_control::{UnitControl, UnitState, rcon_name_for};

/// Older than this and a roster is not evidence of anything.
pub const ROSTER_FRESHNESS: TimeDelta = TimeDelta::minutes(10);

/// The capacity shown when a server has never told us one.
pub const DEFAULT_CAPACITY: i32 = 24;

/// What a rename attempt did. A bool was not enough: rate limited is the most likely answer to
/// a manual `/counts`, and it read exactly like a missing permission.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RenameOutcome {
    Renamed,
    NotVisible,
    Forbidden,
    RateLimited,
    Failed,
}

/// Renames voice channels; the gateway adapter implements it.
pub trait ChannelRenamer: Send + Sync {
    /// The channel's current name, or `None` when the bot cannot see it.
    fn name_of(&self, channel_id: u64) -> BoxFuture<'_, Option<String>>;
    fn rename<'a>(&'a self, channel_id: u64, name: &'a str) -> BoxFuture<'a, RenameOutcome>;
}

/// The channels: one per server, in server order, and the platform total.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Targets {
    pub server_channels: Vec<u64>,
    pub total_channel: Option<u64>,
}

/// `Server 1: 3/24`. A denominator is always shown: "Server 1: 0" reads as half working,
/// and appears exactly when a server is empty or unreachable.
#[must_use]
pub fn server_name(number: i32, players: usize, max_players: i32) -> String {
    format!("Server {number}: {players}/{max_players}")
}

/// `Server 2: Offline`: one word for every way of being down, for players reading the sidebar.
#[must_use]
pub fn offline_name(number: i32) -> String {
    format!("Server {number}: Offline")
}

/// Only definite states publish "Offline". Starting is temporary and Unknown is unknowable;
/// both fall through to the roster, which keeps the last good count.
#[must_use]
pub fn is_offline(state: UnitState) -> bool {
    matches!(state, UnitState::Inactive | UnitState::Failed)
}

/// `Pavlov Shack: 287`: no denominator, since the sum of every listed server's slots is not a
/// fraction anybody is trying to fill.
#[must_use]
pub fn total_name(players: i64) -> String {
    format!("Pavlov Shack: {players}")
}

/// Players across every listed server.
#[must_use]
pub fn total_players(servers: &[ServerListing]) -> i64 {
    servers.iter().map(|s| i64::from(s.players)).sum()
}

/// Live, else the last known, else the fallback (else 24). LIVE OUTRANKS THE CACHE, or a
/// `MaxPlayers` change hides until a restart; zero and below are a failed parse, not a size.
#[must_use]
pub fn resolve_capacity(live: Option<i32>, last_known: Option<i32>, fallback: i32) -> i32 {
    live.filter(|c| *c > 0)
        .or(last_known.filter(|c| *c > 0))
        .unwrap_or(if fallback > 0 {
            fallback
        } else {
            DEFAULT_CAPACITY
        })
}

/// The half of the feature that is not configured, or `None` when both are (C#
/// `CountsCommand.MissingHalf`). A per-channel report cannot mention a channel that was never
/// configured, so half a setup used to show a green tick over half a report.
#[must_use]
pub fn missing_half(targets: &Targets) -> Option<(&'static str, &'static str)> {
    if targets.server_channels.is_empty() {
        return Some((
            "Per-server channels not configured",
            "Only the platform total updates. Set one voice channel id per server, **in server \
             order**, then restart:\n\
             ```\nPLAYER_COUNT_CHANNELS=111,222,333\n```",
        ));
    }
    if targets.total_channel.is_none() {
        return Some((
            "Platform total not configured",
            "`SHACK_TOTAL_CHANNEL` is empty.",
        ));
    }
    None
}

/// The updater.
pub struct PlayerCountChannels {
    channels: Arc<dyn ChannelRenamer>,
    rcon: Arc<RconRegistry>,
    master: Arc<MasterServerList>,
    services: Arc<dyn UnitControl>,
    default_capacity: i32,
    /// The last capacity each server reported: never PREFERRED, only a stand-in when a server
    /// that answered before is not answering now.
    last_known: Mutex<HashMap<String, i32>>,
}

impl std::fmt::Debug for PlayerCountChannels {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PlayerCountChannels")
            .field("default_capacity", &self.default_capacity)
            .finish_non_exhaustive()
    }
}

impl PlayerCountChannels {
    #[must_use]
    pub fn new(
        channels: Arc<dyn ChannelRenamer>,
        rcon: Arc<RconRegistry>,
        master: Arc<MasterServerList>,
        services: Arc<dyn UnitControl>,
        default_capacity: i32,
    ) -> Self {
        Self {
            channels,
            rcon,
            master,
            services,
            default_capacity: if default_capacity > 0 {
                default_capacity
            } else {
                DEFAULT_CAPACITY
            },
            last_known: Mutex::new(HashMap::new()),
        }
    }

    /// The background tick: [`run`](Self::run), then ONE log line saying what happened to
    /// every channel, since "not updating" has four different fixes.
    pub async fn tick(&self, targets: &Targets) {
        let report = self.run(targets).await;
        if !report.is_empty() {
            tracing::info!(report = %report.join("  |  "), "player-count channels");
        }
    }

    /// Do the work and say what happened, one line per channel (`/counts` shows this).
    pub async fn run(&self, targets: &Targets) -> Vec<String> {
        // SWEEP FIRST: the rosters' own timer would publish a count up to a minute old and
        // hold it for six. A failed sweep keeps the previous snapshot and its timestamp.
        self.rcon.refresh_rosters().await;
        let mut report = Vec::new();
        for (index, channel) in targets.server_channels.iter().enumerate() {
            let number = i32::try_from(index + 1).unwrap_or(i32::MAX);
            // BY NUMBER, NOT POSITION: RCON slots may have gaps.
            match self.services.unit_for(number) {
                None => report.push(format!(
                    "#{number} no such server ({} unit(s) configured)",
                    self.services.units().len()
                )),
                Some(unit) => {
                    let unit = unit.to_owned();
                    report.push(self.update_server(*channel, number, &unit).await);
                }
            }
        }
        if let Some(total) = targets.total_channel {
            report.push(self.update_total(total).await);
        }
        report
    }

    async fn update_server(&self, channel: u64, number: i32, unit: &str) -> String {
        let server = rcon_name_for(number);
        // systemd knows down from stale for certain; RCON only reports a silence.
        let state = self.services.state(unit).await;
        if is_offline(state) {
            let offline = offline_name(number);
            return format!("\"{offline}\" {}", self.apply(channel, &offline).await);
        }
        let roster = self.rcon.roster(&server);
        let capacity = self.capacity(&server).await;
        let now = chrono::Utc::now();
        match roster.taken_at {
            // NEVER FETCHED IS NOT STALE: there is no count to preserve, and the channel would
            // keep an hours-old number from the last process.
            None if state == UnitState::Active => {
                let name = server_name(number, 0, capacity);
                format!(
                    "\"{name}\" {} (no roster yet)",
                    self.apply(channel, &name).await
                )
            }
            Some(at) if now - at <= ROSTER_FRESHNESS => {
                let name = server_name(number, roster.players.len(), capacity);
                format!("\"{name}\" {}", self.apply(channel, &name).await)
            }
            taken => {
                let why = match state {
                    UnitState::Starting => "the unit is still starting up".to_owned(),
                    UnitState::Unknown => {
                        "systemd could not be asked, and RCON is not answering either".to_owned()
                    }
                    _ => self
                        .rcon
                        .roster_problem(&server)
                        .unwrap_or_else(|| match taken {
                            None => "the roster has never been fetched".to_owned(),
                            Some(at) => {
                                format!("the roster is {} minutes old", (now - at).num_minutes())
                            }
                        }),
                };
                format!("{server} skipped ({why})")
            }
        }
    }

    async fn update_total(&self, channel: u64) -> String {
        // FORCED: the master list's 60-second cache would publish a figure seeded by whoever
        // last ran /server lookup, a tick behind.
        let snapshot = self.master.get(true).await;
        if snapshot.servers.is_empty() {
            return "Pavlov Shack skipped (master list empty)".to_owned();
        }
        let name = total_name(total_players(&snapshot.servers));
        format!("\"{name}\" {}", self.apply(channel, &name).await)
    }

    /// Rename only when the name would change, and name every outcome: this string is the
    /// whole diagnostic for a channel that stopped moving.
    async fn apply(&self, channel: u64, name: &str) -> String {
        let Some(current) = self.channels.name_of(channel).await else {
            tracing::warn!(
                channel,
                "channel is not visible to the bot: the id is wrong, the bot is not in that \
                 server, or it cannot see the channel"
            );
            return format!("FAILED - channel {channel} not visible");
        };
        if current == name {
            return "unchanged".to_owned();
        }
        match self.channels.rename(channel, name).await {
            RenameOutcome::Renamed => "renamed".to_owned(),
            RenameOutcome::RateLimited => "RATE LIMITED - Discord allows two renames per ten \
                minutes per channel, and the background tick has already spent them. It will \
                catch up on its own"
                .to_owned(),
            RenameOutcome::Forbidden => {
                "FAILED - the bot needs Manage Channels on this channel".to_owned()
            }
            RenameOutcome::NotVisible => {
                format!("FAILED - channel {channel} is not a server channel")
            }
            RenameOutcome::Failed => "FAILED - see the log for the reason".to_owned(),
        }
    }

    async fn capacity(&self, server: &str) -> i32 {
        let live = self.max_players(server).await;
        let mut known = self
            .last_known
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        if let Some(reported) = live.filter(|c| *c > 0) {
            known.insert(server.to_owned(), reported);
        }
        resolve_capacity(live, known.get(server).copied(), self.default_capacity)
    }

    /// The capacity as the server reports it now. Decoration: a failure never stops the rename.
    async fn max_players(&self, server: &str) -> Option<i32> {
        match self.rcon.send(server, "ServerInfo").await {
            Ok(reply) => pavlov_rcon::reply::try_parse(Some(&reply))
                .and_then(|root| pavlov_rcon::reply::server_info(&root).max_players),
            Err(error) => {
                tracing::debug!(server, %error, "could not read MaxPlayers");
                None
            }
        }
    }
}
