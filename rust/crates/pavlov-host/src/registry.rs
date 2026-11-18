//! The bot's RCON clients, one per configured server, plus the shared roster cache (C#
//! `Rcon/RconRegistry.cs`).
//!
//! Owning the clients in one place is what makes the persistent session worth having. The
//! roster cache holds the answer across the minute between sweeps, so "who is online" does not
//! cost a round trip on the game thread every time a command wants it.

use std::collections::{BTreeMap, HashMap};
use std::fmt;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError, RwLock};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use futures_util::future::join_all;
use pavlov_config::RconOptions;
use pavlov_core::text::case::{IgnoreCaseSet, eq_ignore_case};
use pavlov_core::time::{Clock, SystemClock};
use pavlov_rcon::reply::{self, PavlovPlayer};
use pavlov_rcon::{OnlineRoster, RconClient, RconError, ServerLifecycle};

use crate::confirmations::RconConfirmations;
use crate::health::HealthResult;
use crate::metrics::{MetricLabels, MetricsRegistry};

/// How long after sending the log may still confirm a command. The log is polled every 1.5 s.
pub const LOG_CONFIRM_WINDOW: Duration = Duration::from_secs(10);
/// A roster older than this is stale - reported, not silently served as current.
pub const ROSTER_FRESHNESS: TimeDelta = TimeDelta::seconds(90);

/// Whether a roster was taken within [`ROSTER_FRESHNESS`] of `now`. Never taken is stale.
#[must_use]
pub fn is_fresh(roster: &RosterSnapshot, now: DateTime<Utc>) -> bool {
    roster
        .taken_at
        .is_some_and(|at| now - at <= ROSTER_FRESHNESS)
}

/// Everybody on a FRESH roster, deduplicated ignoring case, blanks dropped. A frozen roster
/// from a crashed server is excluded: paying against it pays people who are not there.
#[must_use]
pub fn confirmed_online<'a>(
    rosters: impl IntoIterator<Item = &'a RosterSnapshot>,
    now: DateTime<Utc>,
) -> Vec<String> {
    RconRegistry::names(rosters.into_iter().filter(|r| is_fresh(r, now)))
}
const COMMAND_HELP: &str = "RCON command duration in milliseconds";

/// One server's roster, with when it was taken. `taken_at = None` is "no roster yet".
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RosterSnapshot {
    pub server: String,
    pub players: Vec<PavlovPlayer>,
    pub taken_at: Option<DateTime<Utc>>,
}

impl RosterSnapshot {
    #[must_use]
    pub fn empty(server: &str) -> Self {
        Self {
            server: server.to_owned(),
            players: Vec::new(),
            taken_at: None,
        }
    }
}

/// Where a server's Pavlov.log is, for log confirmation.
pub type LogFor = Arc<dyn Fn(&str) -> Option<String> + Send + Sync>;

#[derive(Default)]
struct State {
    rosters: HashMap<String, RosterSnapshot>,
    /// `Some(None)` is "probed, answered"; `Some(Some(e))` is "probed, failed".
    last_error: HashMap<String, Option<String>>,
    roster_problem: HashMap<String, String>,
}

/// The registry.
pub struct RconRegistry {
    clients: BTreeMap<String, RconClient>,
    state: Mutex<State>,
    metrics: Arc<MetricsRegistry>,
    clock: Arc<dyn Clock>,
    lifecycle: RwLock<Option<Arc<dyn ServerLifecycle>>>,
    confirmations: RwLock<Option<(Arc<RconConfirmations>, LogFor)>>,
}

impl fmt::Debug for RconRegistry {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("RconRegistry")
            .field("servers", &self.clients.keys().collect::<Vec<_>>())
            .finish_non_exhaustive()
    }
}

/// An idle timeout the health probe is guaranteed to land inside: at least three probes wide.
/// Otherwise the session is only persistent by accident - raise the probe interval past the
/// recycle and every probe would reconnect, forever, with nothing saying so.
#[must_use]
pub fn hold_open(configured: Duration, probe_interval: Duration) -> Duration {
    configured.max(probe_interval.saturating_mul(3))
}

/// The reply returned for a command the log confirmed, shaped like Pavlov's own.
#[must_use]
pub fn log_confirmed_reply(command: &str) -> String {
    let verb = RconClient::verb(command);
    format!(
        "{{\"Command\":{},\"Successful\":true,\"ConfirmedBy\":\"Pavlov.log\"}}",
        serde_json::Value::String(verb.to_owned())
    )
}

/// What the server actually sent, quoted and bounded, for a report.
#[must_use]
pub fn excerpt(raw: Option<&str>) -> String {
    let text = raw.unwrap_or_default().trim();
    if text.is_empty() {
        return "it was empty".into();
    }
    let text = text.replace('`', "'");
    let text = if text.chars().count() > 200 {
        format!("{}...", text.chars().take(200).collect::<String>())
    } else {
        text
    };
    format!("\"{text}\"")
}

impl RconRegistry {
    /// One client per server, each idle timeout widened to [`hold_open`].
    #[must_use]
    pub fn new(
        servers: &[RconOptions],
        health_interval: Duration,
        metrics: Arc<MetricsRegistry>,
        clock: Arc<dyn Clock>,
    ) -> Self {
        let clients: BTreeMap<String, RconClient> = servers
            .iter()
            .map(|s| {
                let mut options = s.clone();
                options.idle_timeout = hold_open(s.idle_timeout, health_interval);
                (s.name.clone(), RconClient::new(options))
            })
            .collect();
        tracing::info!(
            servers = clients.len(),
            probe_seconds = health_interval.as_secs(),
            "RCON: one connection per server, held open"
        );
        Self {
            clients,
            state: Mutex::new(State::default()),
            metrics,
            clock,
            lifecycle: RwLock::new(None),
            confirmations: RwLock::new(None),
        }
    }

    /// A registry on the real clock.
    #[must_use]
    pub fn with_system_clock(
        servers: &[RconOptions],
        health_interval: Duration,
        metrics: Arc<MetricsRegistry>,
    ) -> Self {
        Self::new(servers, health_interval, metrics, Arc::new(SystemClock))
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Supply the lifecycle source after construction - set afterwards to break the cycle with
    /// the systemd type, which needs the registry to warn players before it stops a server.
    pub fn use_lifecycle(&self, lifecycle: Arc<dyn ServerLifecycle>) {
        *self
            .lifecycle
            .write()
            .unwrap_or_else(PoisonError::into_inner) = Some(lifecycle);
    }

    /// Confirm state-changing commands from each server's Pavlov.log.
    pub fn use_log_confirmation(&self, confirmations: Arc<RconConfirmations>, log_for: LogFor) {
        *self
            .confirmations
            .write()
            .unwrap_or_else(PoisonError::into_inner) = Some((confirmations, log_for));
    }

    /// The configured server names, in order.
    #[must_use]
    pub fn servers(&self) -> Vec<String> {
        self.clients.keys().cloned().collect()
    }

    #[must_use]
    pub fn client(&self, server: &str) -> Option<&RconClient> {
        self.clients.get(server)
    }

    fn client_or_err(&self, server: &str) -> Result<&RconClient, RconError> {
        self.clients
            .get(server)
            .ok_or_else(|| RconError::Protocol(format!("unknown server \"{server}\"")))
    }

    /// Send a command to one server, timed and counted under a bounded label.
    ///
    /// # Errors
    /// Any [`RconError`], or a Protocol error for an unknown server.
    pub async fn send(&self, server: &str, command: &str) -> Result<String, RconError> {
        let client = self.client_or_err(server)?.clone();
        let labels = MetricLabels::of2(
            "server",
            server,
            "command",
            &RconClient::metric_verb(command),
        );
        self.metrics
            .time(
                "rcon_command_duration_ms",
                &labels,
                Some(COMMAND_HELP),
                self.send_confirmed(server, client, command),
            )
            .await
    }

    /// For a state-changing command, whichever comes first - the reply or the command in that
    /// server's Pavlov.log - is the answer. THE RCON EXCHANGE STILL FINISHES, in a task of its
    /// own: walking away from a reply would hand it to the next command. A reply that arrives
    /// first wins, refusal included; reads are never confirmed from the log.
    async fn send_confirmed(
        &self,
        server: &str,
        client: RconClient,
        command: &str,
    ) -> Result<String, RconError> {
        let watch = self
            .confirmations
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .as_ref()
            .and_then(|(c, log_for)| log_for(server).map(|log| (Arc::clone(c), log)));
        let Some((confirmations, log)) =
            watch.filter(|_| !RconClient::is_read_only_command(command))
        else {
            return client.send(command).await;
        };

        let sent_at = self.clock.now();
        let owned = command.to_owned();
        let mut reply = tokio::spawn(async move { client.send(&owned).await });
        let logged = confirmations.confirmed(Some(&log), command, sent_at, LOG_CONFIRM_WINDOW);
        tokio::pin!(logged);

        let join = |r: Result<Result<String, RconError>, tokio::task::JoinError>| {
            r.unwrap_or_else(|e| Err(RconError::Transport(format!("the RCON send stopped: {e}"))))
        };

        tokio::select! {
            outcome = &mut reply => {
                let outcome = join(outcome);
                // A reply, or a failure that proves the command never reached the server: the
                // log cannot show a command the server never got.
                if !matches!(outcome, Err(RconError::Unconfirmed { .. })) {
                    return outcome;
                }
                if logged.await {
                    self.note_log_confirmed(server, command, "the RCON reply failed");
                    return Ok(log_confirmed_reply(command));
                }
                outcome
            }
            seen = &mut logged => {
                if seen {
                    self.note_log_confirmed(server, command, "before the RCON reply");
                    // Nobody awaits the reply now; its failure, if any, is logged when it lands.
                    let server = server.to_owned();
                    let verb = RconClient::metric_verb(command);
                    tokio::spawn(async move {
                        if let Ok(Err(error)) = reply.await {
                            tracing::debug!(%server, %verb, %error, "the RCON reply after the log confirmation failed");
                        }
                    });
                    return Ok(log_confirmed_reply(command));
                }
                // No sighting in the window: the RCON outcome is the answer.
                join(reply.await)
            }
        }
    }

    fn note_log_confirmed(&self, server: &str, command: &str, how: &str) {
        self.metrics.increment(
            "rcon_log_confirmed_total",
            &MetricLabels::of("server", server),
            1.0,
            Some("State-changing RCON commands confirmed from Pavlov.log before (or without) an RCON reply"),
        );
        tracing::info!(server, command, how, "confirmed in Pavlov.log");
    }

    /// An uncached round trip, for a health probe that must measure real latency and reach.
    ///
    /// # Errors
    /// Any [`RconError`].
    pub async fn probe(&self, server: &str, command: &str) -> Result<String, RconError> {
        let client = self.client_or_err(server)?.clone();
        let labels = MetricLabels::of2(
            "server",
            server,
            "command",
            &RconClient::metric_verb(command),
        );
        self.metrics
            .time(
                "rcon_command_duration_ms",
                &labels,
                Some(COMMAND_HELP),
                client.send_uncached_probe(command),
            )
            .await
    }

    /// Send a command and refuse to call it done when the server said it was not. TRI-STATE:
    /// only `"Successful": false` is a refusal; absent or non-JSON is unknown, not failure.
    ///
    /// # Errors
    /// [`RconError::Rejected`] for a refusal, or any send error.
    pub async fn send_verified(&self, server: &str, command: &str) -> Result<String, RconError> {
        let raw = self.send(server, command).await?;
        if reply::try_parse(Some(&raw)).is_some_and(|root| reply::successful(&root) == Some(false))
        {
            return Err(RconError::Rejected {
                command: command.to_owned(),
                reply: raw.trim().to_owned(),
            });
        }
        Ok(raw)
    }

    /// The roster this server last reported. Never blocks; the timestamp says how old it is.
    #[must_use]
    pub fn roster(&self, server: &str) -> RosterSnapshot {
        self.lock()
            .rosters
            .get(server)
            .cloned()
            .unwrap_or_else(|| RosterSnapshot::empty(server))
    }

    /// Forget every cached roster: for a RESTART, after which the last snapshot describes a
    /// reality that has been replaced. Cleared rather than zeroed - "no roster yet" is honest.
    pub fn invalidate_rosters(&self) {
        self.lock().rosters.clear();
        for client in self.clients.values() {
            client.invalidate_reads();
        }
    }

    /// The display name behind a unique id, from the cached rosters - online only, by design.
    #[must_use]
    pub fn name_of(&self, unique_id: Option<&str>) -> Option<String> {
        let id = unique_id.filter(|i| !i.trim().is_empty())?;
        self.lock().rosters.values().find_map(|r| {
            r.players
                .iter()
                .find(|p| eq_ignore_case(&p.unique_id, id) && !p.name.is_empty())
                .map(|p| p.name.clone())
        })
    }

    fn fresh(&self, roster: &RosterSnapshot) -> bool {
        is_fresh(roster, self.clock.now())
    }

    fn names<'a>(rosters: impl Iterator<Item = &'a RosterSnapshot>) -> Vec<String> {
        let mut seen = IgnoreCaseSet::new();
        for roster in rosters {
            for player in &roster.players {
                if !player.name.is_empty() {
                    seen.insert(player.name.clone());
                }
            }
        }
        seen.iter().map(str::to_owned).collect()
    }

    /// Everybody on a server whose roster is FRESH - the only list that may be paid against.
    #[must_use]
    pub fn confirmed_online_players(&self) -> Vec<String> {
        confirmed_online(self.lock().rosters.values(), self.clock.now())
    }

    /// Everybody on any server, a stale roster included - the conservative answer for a guard.
    #[must_use]
    pub fn all_online_players(&self) -> Vec<String> {
        Self::names(self.lock().rosters.values())
    }

    #[must_use]
    pub fn roster_problem(&self, server: &str) -> Option<String> {
        self.lock().roster_problem.get(server).cloned()
    }

    #[must_use]
    pub fn last_error(&self, server: &str) -> Option<String> {
        self.lock().last_error.get(server).cloned().flatten()
    }

    #[must_use]
    pub fn roster_is_fresh(&self, server: &str) -> bool {
        self.fresh(&self.roster(server))
    }

    /// Refresh every roster, IN PARALLEL: one dead server must not make every live roster late.
    pub async fn refresh_rosters(&self) {
        join_all(self.clients.keys().map(|s| self.refresh_roster(s))).await;
    }

    async fn refresh_roster(&self, server: &str) {
        let raw = match self.send(server, "RefreshList").await {
            Ok(raw) => raw,
            Err(error) => {
                self.problem(server, &error.to_string());
                return;
            }
        };
        let Some(root) = reply::try_parse(Some(&raw)) else {
            /* SAY WHAT THE SERVER ACTUALLY SENT - that is the whole diagnosis - AND DROP THE
            SESSION: an exchange that completed gives the layer below no reason to suspect it,
            so the same useless answer would come back every tick. */
            self.problem(
                server,
                &format!(
                    "the server's reply to RefreshList was not JSON: {}",
                    excerpt(Some(&raw))
                ),
            );
            self.reset(server).await;
            return;
        };
        // Only a SUCCESSFUL reply replaces the cache: an unsuccessful one is not "nobody is on".
        if reply::successful(&root) != Some(true) {
            self.problem(server, "the server refused RefreshList");
            self.reset(server).await;
            return;
        }
        let players = reply::players(&root);
        let count = players.len();
        {
            let mut state = self.lock();
            state.rosters.insert(
                server.to_owned(),
                RosterSnapshot {
                    server: server.to_owned(),
                    players,
                    taken_at: Some(self.clock.now()),
                },
            );
            if state.roster_problem.remove(server).is_some() {
                tracing::info!(server, "roster refresh is working again");
            }
        }
        self.metrics.gauge(
            "players_online",
            count as f64,
            &MetricLabels::of("server", server),
            Some("Players currently on a server"),
        );
    }

    async fn reset(&self, server: &str) {
        if let Some(client) = self.clients.get(server) {
            client.reset_session().await;
        }
    }

    fn problem(&self, server: &str, reason: &str) {
        let mut state = self.lock();
        if state
            .roster_problem
            .get(server)
            .is_some_and(|r| r == reason)
        {
            tracing::debug!(server, reason, "roster refresh still failing");
            return;
        }
        state
            .roster_problem
            .insert(server.to_owned(), reason.to_owned());
        tracing::warn!(
            server,
            reason,
            "roster refresh FAILED - the cached roster is now frozen"
        );
    }

    /// Probe every server, IN PARALLEL, each recording its own failure.
    pub async fn probe_all(&self) {
        join_all(self.clients.keys().map(|server| async move {
            let labels = MetricLabels::of("server", server);
            match self.probe(server, "ServerInfo").await {
                Ok(_) => {
                    self.lock().last_error.insert(server.clone(), None);
                    self.metrics.gauge(
                        "rcon_up",
                        1.0,
                        &labels,
                        Some("1 when a server answered its last probe"),
                    );
                }
                Err(error) => {
                    self.lock()
                        .last_error
                        .insert(server.clone(), Some(error.to_string()));
                    self.metrics.gauge("rcon_up", 0.0, &labels, None);
                    tracing::warn!(%server, %error, "RCON probe failed");
                }
            }
        }))
        .await;
    }

    /// RCON health. A server somebody STOPPED is not an RCON fault, and rosters are only
    /// expected from servers that are running.
    pub async fn health(&self) -> HealthResult {
        if self.clients.is_empty() {
            return HealthResult::unhealthy("no RCON servers configured");
        }
        let (probed, mut down): (usize, Vec<String>) = {
            let state = self.lock();
            let down = state
                .last_error
                .iter()
                .filter(|(_, e)| e.is_some())
                .map(|(s, _)| s.clone())
                .collect();
            (state.last_error.len(), down)
        };
        // Nothing probed yet is UNKNOWN, not healthy.
        if probed == 0 {
            return HealthResult::degraded("no RCON probe has completed yet");
        }
        down.sort();

        let mut expected = Vec::new();
        let lifecycle = self
            .lifecycle
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .clone();
        if let Some(lifecycle) = lifecycle {
            let mut still_down = Vec::new();
            for server in down {
                if lifecycle.is_intentionally_stopped(&server).await {
                    expected.push(server);
                } else {
                    still_down.push(server);
                }
            }
            down = still_down;
        }
        let note = if expected.is_empty() {
            String::new()
        } else {
            format!(" ({} stopped on purpose)", expected.join(", "))
        };

        if down.is_empty() {
            let live: Vec<&String> = self
                .clients
                .keys()
                .filter(|s| !expected.contains(s))
                .collect();
            let any_rosters = !self.lock().rosters.is_empty();
            let stale = live.iter().filter(|s| !self.roster_is_fresh(s)).count();
            return if stale == live.len() && !live.is_empty() && any_rosters {
                HealthResult::degraded(format!("every roster is stale{note}"))
            } else {
                HealthResult::healthy(Some(format!("{} server(s) reachable{note}", live.len())))
            };
        }
        let detail = format!(
            "{}{note}",
            down.iter()
                .map(|s| format!("{s}: {}", self.last_error(s).unwrap_or_default()))
                .collect::<Vec<_>>()
                .join("; ")
        );
        if down.len() >= self.clients.len() {
            HealthResult::unhealthy(detail)
        } else {
            HealthResult::degraded(detail)
        }
    }

    /// Close every session (shutdown).
    pub async fn shutdown(&self) {
        join_all(self.clients.values().map(RconClient::shutdown)).await;
    }
}

impl OnlineRoster for RconRegistry {
    fn online(&self) -> Vec<String> {
        self.all_online_players()
    }

    fn confirmed_online(&self) -> Vec<String> {
        self.confirmed_online_players()
    }

    /// ANY server fresh, not all: one unreachable box does not make the players confirmed on
    /// the others imaginary.
    fn is_trustworthy(&self) -> bool {
        self.clients.keys().any(|s| self.roster_is_fresh(s))
    }
}
