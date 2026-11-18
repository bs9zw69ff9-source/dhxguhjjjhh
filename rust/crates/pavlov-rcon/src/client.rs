//! The RCON surface the rest of the bot uses, one per server (C# `RconClient.cs`).
//!
//! - PERSISTENT SESSION: the Node client opened a connection and MD5 handshake per command,
//!   which the Pavlov docs advise against.
//! - READ COALESCING: several timers want RefreshList within the same second. Concurrent callers
//!   share one in-flight read, and a completed read is reused briefly.
//! - JITTERED RETRY, and A COMMAND THAT CHANGES STATE IS NOT SENT TWICE unless it provably never
//!   arrived.

use std::collections::HashMap;
use std::fmt;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use futures_util::FutureExt;
use futures_util::future::{BoxFuture, Shared};
use pavlov_config::RconOptions;
use pavlov_core::text::case::{eq_ignore_case, fold_case};
use tokio::time::{Instant, sleep, timeout};

use crate::connection::{Connection, ExchangeProgress};
use crate::{RconError, backoff, reply};

/// Commands that only read state. Anything absent is assumed to MUTATE, the safe default:
/// caching a Kick would be a correctness bug, failing to cache a read a wasted round trip.
const READ_ONLY_COMMANDS: [&str; 8] = [
    "RefreshList",
    "ServerInfo",
    "ItemList",
    "MapList",
    "Banlist",
    "ModeratorList",
    "InspectAll",
    "UGCModList",
];

/// The verbs the bot is known to send, for METRIC LABELS only - nothing is rejected by this.
/// `/manual` sends whatever an owner types, and a verbatim label minted a time series per typo.
const MUTATING_VERBS: [&str; 13] = [
    "Ban",
    "Unban",
    "Kick",
    "Notify",
    "SetPin",
    "RemovePin",
    "RotateMap",
    "SwitchMap",
    "GiveItem",
    "GiveCash",
    "GiveVehicle",
    "SetPlayerSkin",
    "Slap",
];

/// How long a sent command is remembered as the bot's own.
const ISSUED_RETENTION: Duration = Duration::from_secs(120);
/// Size above which the issued map is worth sweeping at all.
const ISSUED_SOFT_CAP: usize = 200;

type SharedRead = Shared<BoxFuture<'static, Result<String, RconError>>>;

struct InFlight {
    id: u64,
    read: SharedRead,
}

struct CacheEntry {
    value: String,
    at: Instant,
}

struct Issued {
    at: HashMap<String, Instant>,
    last_sweep: Option<Instant>,
}

struct Inner {
    options: RconOptions,
    connection: Connection,
    /// Keyed by the folded, trimmed command (C# `OrdinalIgnoreCase`).
    read_cache: Mutex<HashMap<String, CacheEntry>>,
    in_flight: Mutex<HashMap<String, InFlight>>,
    next_flight: AtomicU64,
    /// Commands sent recently, so log-derived audit can tell the bot's own traffic apart.
    issued: Mutex<Issued>,
}

/// One server's RCON client. Cheap to clone: clones share the session and the caches.
#[derive(Clone)]
pub struct RconClient {
    inner: Arc<Inner>,
}

impl fmt::Debug for RconClient {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("RconClient")
            .field("name", &self.inner.options.name)
            .finish_non_exhaustive()
    }
}

/// A std mutex whose data stays usable after a panic elsewhere: every critical section here is
/// a map insert or lookup, which leaves nothing half-done.
fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

impl RconClient {
    #[must_use]
    pub fn new(options: RconOptions) -> Self {
        Self {
            inner: Arc::new(Inner {
                connection: Connection::new(options.clone()),
                options,
                read_cache: Mutex::new(HashMap::new()),
                in_flight: Mutex::new(HashMap::new()),
                next_flight: AtomicU64::new(0),
                issued: Mutex::new(Issued {
                    at: HashMap::new(),
                    last_sweep: None,
                }),
            }),
        }
    }

    /// The server's label, e.g. "server1".
    #[must_use]
    pub fn name(&self) -> &str {
        &self.inner.options.name
    }

    /// The verb of a command: the text before the first space.
    #[must_use]
    pub fn verb(command: &str) -> &str {
        let trimmed = command.trim();
        trimmed.split_once(' ').map_or(trimmed, |(verb, _)| verb)
    }

    /// Whether a command only reads state (RefreshList, ServerInfo, ...).
    #[must_use]
    pub fn is_read_only_command(command: &str) -> bool {
        let verb = Self::verb(command);
        READ_ONLY_COMMANDS.iter().any(|c| eq_ignore_case(c, verb))
    }

    /// The verb, or "other" when it is not one the bot issues. Bounded, so safe as a metric label.
    #[must_use]
    pub fn metric_verb(command: &str) -> String {
        let verb = Self::verb(command);
        let known = READ_ONLY_COMMANDS
            .iter()
            .chain(MUTATING_VERBS.iter())
            .any(|c| eq_ignore_case(c, verb));
        if known {
            verb.to_owned()
        } else {
            "other".to_owned()
        }
    }

    /// Refuse a command that would be more than one line on the wire, or none. Every call site
    /// sanitises already; this is the one place that can guarantee it.
    ///
    /// # Errors
    /// [`RconError::InvalidCommand`].
    pub fn validate_command(command: &str) -> Result<(), RconError> {
        if command.trim().is_empty() {
            return Err(RconError::InvalidCommand(
                "an RCON command must not be blank",
            ));
        }
        if command.contains(['\r', '\n']) {
            return Err(RconError::InvalidCommand(
                "an RCON command must be a single line",
            ));
        }
        Ok(())
    }

    /// Whether a failed exchange could have been carried out by the server anyway: anything
    /// after the write, except a dead connection that sent nothing back.
    #[must_use]
    pub fn may_have_been_applied(failure: &RconError, progress: &ExchangeProgress) -> bool {
        if !progress.written {
            return false;
        }
        !(failure.is_connection_lost() && progress.reply_bytes == 0)
    }

    /// Send a command, coalescing it with concurrent identical reads where safe.
    ///
    /// # Errors
    /// Any [`RconError`].
    pub async fn send(&self, command: &str) -> Result<String, RconError> {
        Self::validate_command(command)?;
        self.note_issued(command);

        if self.inner.options.read_cache_duration.is_zero() || !Self::is_read_only_command(command)
        {
            return self.inner.send_uncached(command).await;
        }

        let key = command.trim();
        let folded = fold_case(key);
        if let Some(hit) = lock(&self.inner.read_cache).get(&folded) {
            if hit.at.elapsed() < self.inner.options.read_cache_duration {
                return Ok(hit.value.clone());
            }
        }
        self.coalesced(key, folded).await
    }

    /// Join the in-flight read for this command, or start it.
    ///
    /// THE SHARED READ IS NOT BOUND TO ONE CALLER. It runs as its own task, so a caller that goes
    /// away (its future dropped - a cancelled slash command) detaches from it without cancelling
    /// it for everybody else, and a read nobody waits on still completes and fills the cache.
    async fn coalesced(&self, key: &str, folded: String) -> Result<String, RconError> {
        let read = {
            let mut flights = lock(&self.inner.in_flight);
            if let Some(flight) = flights.get(&folded) {
                flight.read.clone()
            } else {
                let id = self.inner.next_flight.fetch_add(1, Ordering::Relaxed);
                let inner = Arc::clone(&self.inner);
                let command = key.to_owned();
                let flight_key = folded.clone();
                // Spawned while the map is locked, so the task cannot remove its entry before
                // the entry exists.
                let task =
                    tokio::spawn(async move { inner.start_read(command, flight_key, id).await });
                let read = async move {
                    task.await.unwrap_or_else(|e| {
                        Err(RconError::Transport(format!(
                            "the shared read stopped: {e}"
                        )))
                    })
                }
                .boxed()
                .shared();
                flights.insert(
                    folded,
                    InFlight {
                        id,
                        read: read.clone(),
                    },
                );
                read
            }
        };
        read.await
    }

    /// Drop cached reads, so the next read goes to the server.
    pub fn invalidate_reads(&self) {
        lock(&self.inner.read_cache).clear();
    }

    /// A real round trip, never from or into the read cache. FOR HEALTH PROBES: a cached reply
    /// reports no round-trip time and, worse, a success for a server that has just gone down.
    ///
    /// # Errors
    /// Any [`RconError`].
    pub async fn send_uncached_probe(&self, command: &str) -> Result<String, RconError> {
        Self::validate_command(command)?;
        self.inner.send_uncached(command).await
    }

    /// Drop the session so the next command reconnects. FOR A REPLY THAT ARRIVED AND MADE NO
    /// SENSE: only the caller knows the answer was unusable.
    pub async fn reset_session(&self) {
        self.invalidate_reads();
        self.inner.connection.reset().await;
    }

    /// Close the session (shutdown), waiting a bounded time for an exchange in flight.
    pub async fn shutdown(&self) {
        self.inner.connection.shutdown().await;
    }

    fn note_issued(&self, command: &str) {
        let now = Instant::now();
        let mut issued = lock(&self.inner.issued);
        issued.at.insert(fold_case(command.trim()), now);
        if issued.at.len() <= ISSUED_SOFT_CAP {
            return;
        }
        // Rate limited: the entries a sweep removes are the ones the next would have removed.
        if issued
            .last_sweep
            .is_some_and(|last| now.duration_since(last) < ISSUED_RETENTION)
        {
            return;
        }
        issued.last_sweep = Some(now);
        issued
            .at
            .retain(|_, at| now.duration_since(*at) <= ISSUED_RETENTION);
    }

    /// True when this client sent that exact command recently (case and padding aside). Pavlov
    /// logs the bot's own RCON traffic alongside everyone else's. Not consumed: one command fanned
    /// out to several servers appears in the log more than once.
    #[must_use]
    pub fn was_issued_by_bot(&self, command: &str) -> bool {
        let key = fold_case(command.trim());
        let mut issued = lock(&self.inner.issued);
        match issued.at.get(&key) {
            Some(at) if at.elapsed() <= ISSUED_RETENTION => true,
            Some(_) => {
                issued.at.remove(&key);
                false
            }
            None => false,
        }
    }
}

impl Inner {
    async fn start_read(&self, command: String, key: String, id: u64) -> Result<String, RconError> {
        let result = self.send_uncached(&command).await;
        if let Ok(value) = &result {
            // Only a SUCCESS is cached. A failure must never be replayed as an answer.
            lock(&self.read_cache).insert(
                key.clone(),
                CacheEntry {
                    value: value.clone(),
                    at: Instant::now(),
                },
            );
        }
        let mut flights = lock(&self.in_flight);
        if flights.get(&key).is_some_and(|f| f.id == id) {
            flights.remove(&key);
        }
        result
    }

    async fn send_uncached(&self, command: &str) -> Result<String, RconError> {
        let o = &self.options;
        let read_only = RconClient::is_read_only_command(command);
        let verb = RconClient::verb(command);
        // At least one attempt: zero would fail every command without trying.
        let attempts = o.max_attempts.max(1);
        let mut last = None;

        for attempt in 0..attempts {
            let mut progress = ExchangeProgress::default();
            let outcome = timeout(
                o.command_timeout,
                self.connection.send(command, read_only, &mut progress),
            )
            .await
            .unwrap_or(Err(RconError::Timeout(o.command_timeout)));

            let failure = match outcome {
                Ok(reply) => match mismatch(command, &reply) {
                    /* THE REPLY HAS TO BE THE ONE WE ASKED FOR. Production saw BanList answered
                    with a ServerInfo document and, later, a RefreshList one. The exchange looked
                    perfect, so the session is reset here: if replies are offset, every later
                    command on it is wrong too. */
                    Some(mismatched) => {
                        self.connection.reset().await;
                        mismatched
                    }
                    None => {
                        // Anything not a pure read may change who is on, so cached reads go.
                        if !read_only {
                            lock(&self.read_cache).clear();
                        }
                        return Ok(reply);
                    }
                },
                Err(error) => error,
            };

            // A wrong password will still be wrong on the third attempt.
            if matches!(failure, RconError::Auth(_)) {
                return Err(failure);
            }

            if !read_only && RconClient::may_have_been_applied(&failure, &progress) {
                return Err(RconError::Unconfirmed {
                    message: format!(
                        "{}: \"{verb}\" got no confirmation ({failure}). It may have been applied, so it was not sent again",
                        o.name
                    ),
                    cause: Box::new(failure),
                });
            }

            last = Some(failure);
            if attempt + 1 < attempts {
                sleep(backoff::default_delay(attempt)).await;
            }
        }

        let cause = last.unwrap_or_else(|| RconError::Transport("no attempt was made".into()));
        Err(RconError::Failed {
            message: format!(
                "{}: \"{verb}\" failed after {attempts} attempt(s): {cause}",
                o.name
            ),
            cause: Box::new(cause),
        })
    }
}

/// The reply's own account of which command it answers, when it says and it is not ours.
/// ABSENT IS NOT A MISMATCH: plenty of replies carry no Command field, and some are not JSON.
fn mismatch(command: &str, raw: &str) -> Option<RconError> {
    let root = reply::try_parse(Some(raw))?;
    let answered = reply::text(&root, &["Command"])?;
    let asked = RconClient::verb(command);
    (!eq_ignore_case(asked, &answered)).then(|| RconError::Mismatched {
        asked: asked.to_owned(),
        answered,
    })
}
