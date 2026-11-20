//! Accounts, addresses and evasion flags from the log (C# `Logs/IpTrackingService.cs`).
//!
//! A CONFIRMED address is one seen on the same line as the account id (a disconnect): certain,
//! and the only kind that may flag or ban. A GUESSED address is correlated from a nearby accept
//! line: display only, because promoting one bans whoever else was connecting at that moment.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, PoisonError};

use chrono::{DateTime, TimeDelta, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::evasion::{
    AccountRecord, FlagMatch, FlagMatching, FlagOutcome, FlagSet, FlagVerdict, PendingFlag,
};
use pavlov_core::logs::JoinCorrelator;
use pavlov_core::logs::pavlov_log::{self, ConfirmedPairing, KillEvent, LoginRequest, RconAction};
use pavlov_core::text::case::{IgnoreCaseMap, IgnoreCaseSet, eq_ignore_case};
use pavlov_core::time::Clock;
use pavlov_storage::datasets::{AUTOBAN_EXEMPT, IP_FLAGS, KNOWN_PLAYERS, USER_BLACKLIST};

use super::LogLine;
use crate::handlers::Handlers;
use crate::metrics::{MetricLabels, MetricsRegistry};
use crate::moderation::{BanEvidence, MasterNames};

const PENDING_FLAG_WINDOW: TimeDelta = TimeDelta::minutes(5);
/// A flagged player retrying logs in every few seconds; each must not fire a fresh ban.
const AUTO_BAN_DEBOUNCE: TimeDelta = TimeDelta::minutes(5);

fn pending_dataset() -> String {
    format!("{AUTOBAN_EXEMPT}_pending")
}

/// A login, with the correlated address when there is one.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlayerJoined {
    pub file: String,
    pub account_id: String,
    pub name: Option<String>,
    pub ip: Option<String>,
    /// The address is the only connection that was pending.
    pub confident: bool,
    pub at: DateTime<Utc>,
}

/// An address confirmed against an account.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlayerConfirmed {
    pub file: String,
    pub account_id: String,
    pub name: Option<String>,
    pub ip: String,
    pub at: DateTime<Utc>,
}

/// A join that matched a standing flag.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FlaggedJoin {
    pub account_id: String,
    pub name: Option<String>,
    pub ip: Option<String>,
    pub verdict: FlagVerdict,
    pub at: DateTime<Utc>,
}

/// An RCON or RCON+ command the game logged.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RconLogged {
    pub file: String,
    pub at: DateTime<Utc>,
    pub action: RconAction,
}

type Accounts = IgnoreCaseMap<Option<AccountRecord>>;

fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(PoisonError::into_inner)
}

fn flags_empty(flags: &FlagSet) -> bool {
    flags.ips.is_empty()
        && flags.names.is_empty()
        && flags.ids.is_empty()
        && flags.manual_ips.is_empty()
}

/// The service. The log tailer feeds it one line at a time.
pub struct IpTrackingService {
    store: Arc<SerializedStore>,
    metrics: Arc<MetricsRegistry>,
    clock: Arc<dyn Clock>,
    masters: Option<Arc<dyn MasterNames>>,
    correlator: Mutex<JoinCorrelator>,
    recently_auto_banned: Mutex<HashMap<String, DateTime<Utc>>>,
    partial_kills: Mutex<HashMap<String, KillEvent>>,
    pending_rcon_plus: Mutex<HashMap<String, (DateTime<Utc>, RconAction)>>,
    stats_log_kills: AtomicBool,
    untracked: Mutex<IgnoreCaseSet>,
    pub joined: Handlers<PlayerJoined>,
    pub confirmed: Handlers<PlayerConfirmed>,
    pub flagged: Handlers<FlaggedJoin>,
    pub kill: Handlers<KillEvent>,
    pub rcon: Handlers<RconLogged>,
}

impl std::fmt::Debug for IpTrackingService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("IpTrackingService").finish_non_exhaustive()
    }
}

impl IpTrackingService {
    #[must_use]
    pub fn new(
        store: Arc<SerializedStore>,
        metrics: Arc<MetricsRegistry>,
        clock: Arc<dyn Clock>,
    ) -> Self {
        Self {
            store,
            metrics,
            clock,
            masters: None,
            correlator: Mutex::new(JoinCorrelator::default()),
            recently_auto_banned: Mutex::new(HashMap::new()),
            partial_kills: Mutex::new(HashMap::new()),
            pending_rcon_plus: Mutex::new(HashMap::new()),
            stats_log_kills: AtomicBool::new(false),
            untracked: Mutex::new(IgnoreCaseSet::default()),
            joined: Handlers::default(),
            confirmed: Handlers::default(),
            flagged: Handlers::default(),
            kill: Handlers::default(),
            rcon: Handlers::default(),
        }
    }

    /// Masters still get a join line (with no address) when they are untracked.
    #[must_use]
    pub fn with_masters(mut self, masters: Arc<dyn MasterNames>) -> Self {
        self.masters = Some(masters);
        self
    }

    #[must_use]
    pub fn with_correlator(self, correlator: JoinCorrelator) -> Self {
        *lock(&self.correlator) = correlator;
        self
    }

    /// Stats.log supplies the kills: the Pavlov.log kill lines are then skipped, or every kill
    /// is posted and counted twice.
    pub fn use_stats_log_kills(&self) {
        self.stats_log_kills.store(true, Ordering::Relaxed);
    }

    /// Never record this name: no address, no alt trail.
    pub fn untrack(&self, name: &str) {
        lock(&self.untracked).insert(name.to_owned());
    }

    /// Record a name's addresses again (`/configure` un-ignore).
    pub fn retrack(&self, name: &str) {
        lock(&self.untracked).remove(name);
    }

    /// Whether a name is on the ignore list (its addresses are not recorded).
    #[must_use]
    pub fn is_untracked(&self, name: &str) -> bool {
        lock(&self.untracked).contains(name)
    }

    // ---- storage ----

    fn load_accounts(&self) -> IgnoreCaseMap<AccountRecord> {
        let stored: Accounts = self
            .store
            .read(KNOWN_PLAYERS, Accounts::default())
            .unwrap_or_else(|error| {
                tracing::warn!(%error, "could not read known_players");
                Accounts::default()
            });
        let total = stored.len();
        let usable: IgnoreCaseMap<AccountRecord> = stored
            .iter()
            .filter_map(|(k, v)| v.clone().map(|v| (k.to_owned(), v)))
            .collect();
        if usable.len() != total {
            tracing::warn!(
                count = total - usable.len(),
                "known_players holds empty rows - skipping them; most likely another writer of this database"
            );
        }
        usable
    }

    /// The flags, read through to the legacy key an older build wrote them under. Only an
    /// OBJECT there can be legacy data: Node keeps an ARRAY of barred Discord ids under it.
    #[must_use]
    pub fn load_flags(&self) -> FlagSet {
        let stored: FlagSet = self
            .store
            .read(IP_FLAGS, FlagSet::default())
            .unwrap_or_default();
        if !flags_empty(&stored) {
            return stored;
        }
        let raw = self
            .store
            .read_raw(USER_BLACKLIST)
            .ok()
            .flatten()
            .unwrap_or_default();
        if !raw.trim_start().starts_with('{') {
            return stored;
        }
        let legacy: FlagSet = self
            .store
            .read(USER_BLACKLIST, FlagSet::default())
            .unwrap_or_default();
        if flags_empty(&legacy) { stored } else { legacy }
    }

    #[must_use]
    pub fn account(&self, account_id: &str) -> Option<AccountRecord> {
        self.load_accounts().get(account_id).cloned()
    }

    #[must_use]
    pub fn accounts(&self) -> IgnoreCaseMap<AccountRecord> {
        self.load_accounts()
    }

    /// Every name ever used, most recently seen accounts first.
    #[must_use]
    pub fn known_names(&self) -> Vec<String> {
        let mut accounts: Vec<AccountRecord> = self
            .load_accounts()
            .iter()
            .map(|(_, a)| a.clone())
            .collect();
        accounts.sort_by_key(|a| std::cmp::Reverse(a.last_seen));
        let mut seen = IgnoreCaseSet::default();
        accounts
            .into_iter()
            .flat_map(|a| a.names)
            .filter(|n| seen.insert(n.clone()))
            .collect()
    }

    #[must_use]
    pub fn account_by_name(&self, name: &str) -> Option<AccountRecord> {
        self.load_accounts()
            .iter()
            .map(|(_, a)| a)
            .find(|a| a.names.iter().any(|n| eq_ignore_case(n, name)))
            .cloned()
    }

    #[must_use]
    pub fn accounts_with_address(&self, ip: &str) -> Vec<AccountRecord> {
        self.load_accounts()
            .iter()
            .map(|(_, a)| a)
            .filter(|a| a.confirmed_ips.iter().any(|i| i == ip))
            .cloned()
            .collect()
    }

    /// Other accounts that share a CONFIRMED address with this one.
    #[must_use]
    pub fn alts_of(&self, account_id: &str) -> Vec<AccountRecord> {
        let accounts = self.load_accounts();
        let Some(subject) = accounts.get(account_id) else {
            return Vec::new();
        };
        if subject.confirmed_ips.is_empty() {
            return Vec::new();
        }
        accounts
            .iter()
            .map(|(_, a)| a)
            .filter(|a| !eq_ignore_case(&a.id, account_id))
            .filter(|a| {
                a.confirmed_ips
                    .iter()
                    .any(|ip| subject.confirmed_ips.contains(ip))
            })
            .cloned()
            .collect()
    }

    // ---- ingest ----

    /// One log line.
    pub async fn ingest(&self, line: &LogLine) {
        let at = pavlov_log::timestamp_of(&line.text).unwrap_or_else(|| self.clock.now());
        if let Some(accepted) = pavlov_log::accepted_address(&line.text) {
            lock(&self.correlator).accepted(&line.file, &accepted, at);
            return;
        }
        if let Some(pairing) = pavlov_log::confirmed(&line.text) {
            self.on_confirmed(&line.file, pairing, at).await;
            return;
        }
        if let Some(login) = pavlov_log::login(&line.text) {
            self.on_login(&line.file, login, at).await;
            return;
        }
        // The RCON+ verdict on the command buffered from the line before.
        if let Some(success) = pavlov_log::rcon_result(&line.text) {
            let pending = lock(&self.pending_rcon_plus).remove(&line.file);
            if let Some((at, mut action)) = pending {
                action.successful = Some(success);
                self.emit_rcon(&line.file, at, action).await;
            }
            return;
        }
        if let Some(action) = pavlov_log::rcon(&line.text) {
            // Anything still buffered is flushed unresolved: only a truncated tail does that.
            let previous = lock(&self.pending_rcon_plus).remove(&line.file);
            if let Some((at, action)) = previous {
                self.emit_rcon(&line.file, at, action).await;
            }
            if action.plus {
                lock(&self.pending_rcon_plus).insert(line.file.clone(), (at, action));
            } else {
                self.emit_rcon(&line.file, at, action).await;
            }
            return;
        }
        // Stats.log describes the same kill, better: reading both counts every kill twice.
        if self.stats_log_kills.load(Ordering::Relaxed) {
            return;
        }
        if let Some(kill) = pavlov_log::kill(&line.text) {
            self.on_kill(&line.file, kill).await;
        }
    }

    async fn emit_rcon(&self, file: &str, at: DateTime<Utc>, action: RconAction) {
        self.rcon
            .emit(RconLogged {
                file: file.to_owned(),
                at,
                action,
            })
            .await;
    }

    async fn on_login(&self, file: &str, login: LoginRequest, at: DateTime<Utc>) {
        let placeholder = pavlov_log::is_placeholder_id(Some(&login.id));
        let guess = lock(&self.correlator).login(file, at, placeholder);
        // A pre-authentication line identifies nobody.
        if placeholder {
            return;
        }
        if let Some(named) = login.name.as_deref().filter(|n| self.is_untracked(n)) {
            /* Unrecorded, not invisible: a MASTER still gets the public join line (with no
            address), or the join log reads as dead to the one person watching it. */
            if self.masters.as_ref().is_some_and(|m| m.is_master(named)) {
                self.joined
                    .emit(PlayerJoined {
                        file: file.to_owned(),
                        account_id: login.id.clone(),
                        name: Some(named.to_owned()),
                        ip: None,
                        confident: false,
                        at,
                    })
                    .await;
            }
            return;
        }
        let guessed = if guess.confident {
            guess.ip.clone()
        } else {
            None
        };
        self.record(
            &login.id,
            login.name.as_deref(),
            guessed.as_deref(),
            None,
            at,
        )
        .await;
        self.joined
            .emit(PlayerJoined {
                file: file.to_owned(),
                account_id: login.id.clone(),
                name: login.name.clone(),
                ip: guess.ip.clone(),
                confident: guess.confident,
                at,
            })
            .await;
        self.check_flags(&login.id, login.name.as_deref(), guessed.as_deref(), at)
            .await;
    }

    async fn on_confirmed(&self, file: &str, pairing: ConfirmedPairing, at: DateTime<Utc>) {
        if pavlov_log::is_placeholder_id(Some(&pairing.id)) {
            return;
        }
        let known = self
            .account(&pairing.id)
            .and_then(|a| a.name().map(str::to_owned))
            .or_else(|| pairing.name.clone());
        if known.as_deref().is_some_and(|n| self.is_untracked(n)) {
            return;
        }
        /* RECORD FIRST, then read the name back: a close line may carry ?Name=, so an account
        first seen on a disconnect gets its name from this very line. */
        self.record(
            &pairing.id,
            pairing.name.as_deref(),
            None,
            Some(&pairing.ip),
            at,
        )
        .await;
        // A ban issued while they were online has been waiting for exactly this line.
        self.resolve_pending_flag(&pairing.id, &pairing.ip, at)
            .await;
        let name = self
            .account(&pairing.id)
            .and_then(|a| a.name().map(str::to_owned))
            .or(known);
        /* THE ONLY CERTAIN-ADDRESS FLAG CHECK: at login the address is a guess, and on a busy
        server almost never a confident one, so a manual block matched nothing in practice. */
        self.check_manual_address(&pairing.id, name.as_deref(), &pairing.ip, at)
            .await;
        self.confirmed
            .emit(PlayerConfirmed {
                file: file.to_owned(),
                account_id: pairing.id.clone(),
                name,
                ip: pairing.ip.clone(),
                at,
            })
            .await;
        self.metrics.increment(
            "ip_confirmations_total",
            &MetricLabels::none(),
            1.0,
            Some("Addresses confirmed against an account"),
        );
    }

    async fn on_kill(&self, file: &str, kill: KillEvent) {
        /* The record arrives whole or split across lines. Complete once the victim AND the
        cause are known; the killer is optional (a fall has none). Reporting on the "Killed"
        line was too early: the next kill's Killer merged into the orphan, and every kill
        after the first carried the previous kill's weapon. */
        let (flush, complete) = {
            let mut partials = lock(&self.partial_kills);
            let mut flush = None;
            let mut partial = partials.remove(file);
            // A second Killer or Killed line means a new block: emit the old one as it is.
            if let Some(old) = &partial
                && ((kill.killer.is_some() && old.killer.is_some())
                    || (kill.killed.is_some() && old.killed.is_some()))
            {
                flush = partial.take().filter(|p| p.killed.is_some());
            }
            let merged = match partial {
                Some(p) => KillEvent {
                    killer: kill.killer.or(p.killer),
                    killed: kill.killed.or(p.killed),
                    killed_by: kill.killed_by.or(p.killed_by),
                },
                None => kill,
            };
            if merged.killed.is_none() || merged.killed_by.is_none() {
                partials.insert(file.to_owned(), merged);
                (flush, None)
            } else {
                (flush, Some(merged))
            }
        };
        if let Some(old) = flush {
            self.kill.emit(old).await;
        }
        if let Some(kill) = complete {
            self.kill.emit(kill).await;
        }
    }

    async fn record(
        &self,
        id: &str,
        name: Option<&str>,
        guessed_ip: Option<&str>,
        confirmed_ip: Option<&str>,
        at: DateTime<Utc>,
    ) {
        let result = self
            .store
            .update(
                KNOWN_PLAYERS,
                Accounts::default,
                |mut accounts: Accounts| {
                    let mut account = accounts
                        .get(id)
                        .cloned()
                        .flatten()
                        .unwrap_or_else(|| AccountRecord::empty(id));
                    // Most recent name first: the one staff search by.
                    if let Some(name) = name.filter(|n| !n.is_empty()) {
                        account.names.retain(|n| !eq_ignore_case(n, name));
                        account.names.insert(0, name.to_owned());
                    }
                    if let Some(ip) = confirmed_ip
                        && !account.confirmed_ips.iter().any(|i| i == ip)
                    {
                        account.confirmed_ips.push(ip.to_owned());
                    }
                    if let Some(ip) = guessed_ip
                        && !account.confirmed_ips.iter().any(|i| i == ip)
                        && !account.guessed_ips.iter().any(|i| i == ip)
                    {
                        account.guessed_ips.push(ip.to_owned());
                    }
                    // A confirmed guess leaves the guess list: one address, one confidence.
                    let confirmed = account.confirmed_ips.clone();
                    account.guessed_ips.retain(|g| !confirmed.contains(g));
                    account.first_seen = account.first_seen.or(Some(at));
                    account.last_seen = Some(at);
                    accounts.insert(id, Some(account));
                    Some(accounts)
                },
            )
            .await;
        if let Err(error) = result {
            tracing::warn!(id, %error, "could not record the account");
        }
    }

    // ---- flags ----

    /// Debounced per account: true when this hit should be acted on.
    fn debounce(&self, account_id: &str, at: DateTime<Utc>) -> bool {
        let mut recent = lock(&self.recently_auto_banned);
        let key = account_id.to_lowercase();
        if recent
            .get(&key)
            .is_some_and(|last| at - *last < AUTO_BAN_DEBOUNCE)
        {
            return false;
        }
        recent.insert(key, at);
        true
    }

    async fn raise_flag(
        &self,
        account_id: &str,
        name: Option<&str>,
        ip: Option<&str>,
        verdict: FlagVerdict,
        at: DateTime<Utc>,
    ) {
        let matched = match verdict.matched {
            FlagMatch::None => "None",
            FlagMatch::Ip => "Ip",
            FlagMatch::Name => "Name",
            FlagMatch::AccountId => "AccountId",
        };
        self.metrics.increment(
            "flagged_joins_total",
            &MetricLabels::of("match", matched),
            1.0,
            Some("Joins that matched a standing flag"),
        );
        self.flagged
            .emit(FlaggedJoin {
                account_id: account_id.to_owned(),
                name: name.map(str::to_owned),
                ip: ip.map(str::to_owned),
                verdict,
                at,
            })
            .await;
    }

    async fn check_flags(
        &self,
        account_id: &str,
        name: Option<&str>,
        ip: Option<&str>,
        at: DateTime<Utc>,
    ) {
        let verdict = FlagMatching::check(&self.load_flags(), ip, name, Some(account_id));
        if !verdict.hit() || !self.debounce(account_id, at) {
            return;
        }
        tracing::warn!(
            name = name.unwrap_or("unknown"),
            account_id,
            ip = ip.unwrap_or("unknown"),
            detail = verdict.detail.as_deref().unwrap_or_default(),
            "FLAGGED JOIN"
        );
        self.raise_flag(account_id, name, ip, verdict, at).await;
    }

    async fn check_manual_address(
        &self,
        account_id: &str,
        name: Option<&str>,
        ip: &str,
        at: DateTime<Utc>,
    ) {
        if !self.load_flags().manual_ips.contains(ip) || !self.debounce(account_id, at) {
            return;
        }
        let verdict = FlagVerdict {
            matched: FlagMatch::Ip,
            detail: Some(format!("blacklisted ip {ip}")),
            manual: true,
        };
        tracing::warn!(
            name = name.unwrap_or("unknown"),
            account_id,
            ip,
            "FLAGGED (manual block)"
        );
        self.raise_flag(account_id, name, Some(ip), verdict, at)
            .await;
    }

    /// Flag what a ban should flag: the account's CONFIRMED addresses, and its id when the ban
    /// is permanent. No confirmed address yet (they are almost certainly still online) records
    /// the intent, resolved by the disconnect the ban itself causes.
    pub async fn request_flag(&self, account_id: &str, flag_account_id: bool) -> FlagOutcome {
        let account = self.account(account_id);
        let confirmed = account
            .as_ref()
            .map(|a| a.confirmed_ips.clone())
            .unwrap_or_default();
        let names = account.map(|a| a.names).unwrap_or_default();
        let ids = if flag_account_id {
            vec![account_id.to_owned()]
        } else {
            Vec::new()
        };
        if confirmed.is_empty() {
            let request = PendingFlag {
                account_id: account_id.to_owned(),
                requested_at: self.clock.now(),
                flag_account_id,
            };
            if let Err(error) = self
                .store
                .update(
                    &pending_dataset(),
                    IgnoreCaseMap::default,
                    |mut p: IgnoreCaseMap<PendingFlag>| {
                        p.insert(account_id, request.clone());
                        Some(p)
                    },
                )
                .await
            {
                tracing::error!(account_id, %error, "could not record the pending flag");
            }
            tracing::warn!(
                account_id,
                "no confirmed address yet - it will be flagged the moment a disconnect confirms it (pending, persisted across restarts)"
            );
            return FlagOutcome {
                ips: Vec::new(),
                ids,
                alt_names: names,
                pending: true,
            };
        }
        self.apply_flags(&confirmed, &ids).await;
        FlagOutcome {
            ips: confirmed,
            ids,
            alt_names: names,
            pending: false,
        }
    }

    async fn resolve_pending_flag(&self, account_id: &str, ip: &str, at: DateTime<Utc>) {
        let pending: IgnoreCaseMap<PendingFlag> = self
            .store
            .read(&pending_dataset(), IgnoreCaseMap::default())
            .unwrap_or_default();
        let Some(request) = pending.get(account_id).cloned() else {
            return;
        };
        if request.is_live(at, PENDING_FLAG_WINDOW) {
            let ids = if request.flag_account_id {
                vec![account_id.to_owned()]
            } else {
                Vec::new()
            };
            self.apply_flags(&[ip.to_owned()], &ids).await;
            tracing::info!(account_id, ip, "pending flag resolved");
        }
        // Resolved, or too late: after a few minutes any disconnect is somebody else's.
        let _ = self
            .store
            .update(
                &pending_dataset(),
                IgnoreCaseMap::default,
                |mut p: IgnoreCaseMap<PendingFlag>| {
                    p.remove(account_id);
                    Some(p)
                },
            )
            .await;
    }

    /// Update the flags, starting from the migrated legacy set when nothing is stored yet - or
    /// only the seeded `{}` - so the first write cannot drop what the read-through found.
    async fn update_flags(&self, change: impl FnOnce(&mut FlagSet)) {
        if let Err(error) = self.edit_flags(change).await {
            tracing::error!(%error, "could not update the evasion flags");
        }
    }

    /// Change the flags under the store's lock, starting from the migrated legacy set when
    /// nothing is stored yet (see [`load_flags`](Self::load_flags)).
    ///
    /// # Errors
    /// When the dataset cannot be written.
    pub async fn edit_flags(
        &self,
        change: impl FnOnce(&mut FlagSet),
    ) -> Result<(), pavlov_core::data::StoreError> {
        let base = self.load_flags();
        let fallback = base.clone();
        let result = self
            .store
            .update(
                IP_FLAGS,
                move || fallback.clone(),
                move |current: FlagSet| {
                    let mut flags = if flags_empty(&current) { base } else { current };
                    change(&mut flags);
                    Some(flags)
                },
            )
            .await;
        result.map(|_| ())
    }

    async fn apply_flags(&self, ips: &[String], ids: &[String]) {
        self.update_flags(|flags| {
            flags.ips.extend(ips.iter().cloned());
            for id in ids {
                flags.ids.insert(id.clone());
            }
        })
        .await;
    }

    /// Undo what a ban flagged: its addresses, names and id. A MANUAL block stays: an unban
    /// lifts what that ban created, never a standing administrative block.
    pub async fn clear_flags(&self, account_id: &str) -> FlagOutcome {
        let account = self.account(account_id);
        let ips = account
            .as_ref()
            .map(|a| a.confirmed_ips.clone())
            .unwrap_or_default();
        let names = account.map(|a| a.names).unwrap_or_default();
        self.update_flags(|flags| {
            flags.ips.retain(|ip| !ips.contains(ip));
            for name in &names {
                flags.names.remove(name);
            }
            flags.ids.remove(account_id);
        })
        .await;
        // A pending intent too, or a later disconnect would re-flag them.
        let _ = self
            .store
            .update(
                &pending_dataset(),
                IgnoreCaseMap::default,
                |mut p: IgnoreCaseMap<PendingFlag>| {
                    p.remove(account_id);
                    Some(p)
                },
            )
            .await;
        lock(&self.recently_auto_banned).remove(&account_id.to_lowercase());
        FlagOutcome {
            ips,
            ids: vec![account_id.to_owned()],
            alt_names: names,
            pending: false,
        }
    }

    /// Flag an account id alone.
    pub async fn flag_account(&self, account_id: &str) {
        if account_id.trim().is_empty() {
            return;
        }
        self.apply_flags(&[], &[account_id.to_owned()]).await;
    }

    /// An owner's standing block on an address.
    pub async fn flag_address_manually(&self, ip: &str) {
        self.update_flags(|flags| {
            flags.manual_ips.insert(ip.to_owned());
        })
        .await;
    }
}

impl BanEvidence for IpTrackingService {
    fn account_id_for(&self, name: &str) -> Option<String> {
        self.account_by_name(name).map(|a| a.id)
    }

    fn clear_flags<'a>(&'a self, account_id: &'a str) -> BoxFuture<'a, Result<(), String>> {
        Box::pin(async move {
            IpTrackingService::clear_flags(self, account_id).await;
            Ok(())
        })
    }

    fn flag_banned<'a>(&'a self, account_id: &'a str, permanent: bool) -> BoxFuture<'a, ()> {
        Box::pin(async move {
            let _ = self.request_flag(account_id, permanent).await;
        })
    }
}
