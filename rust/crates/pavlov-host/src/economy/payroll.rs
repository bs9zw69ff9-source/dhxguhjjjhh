//! Paying a faction for time on duty (C# `Economy/Payroll.cs`).
//!
//! ACCRUE ON DUTY, BANK ON LEAVING. A player's ledger must not be touched while they are in
//! game: the server holds that balance in memory and overwrites the file on its next save. So a
//! wage is owed while they play and banked on the first run after they log off.
//!
//! PAID FOR THEIR OWN TIME, IN WHOLE PERIODS, WITH THE REMAINDER CARRIED. Each member banks
//! their own seconds; every whole period converts to one wage and the rest counts toward the
//! next. Only time between two sightings counts, and one run credits at most one period, so an
//! outage can never mint a backlog nobody observed.
//!
//! NOTHING RUNS AGAINST A STALE ROSTER: it would accrue for people who left and, worse, bank a
//! wage for somebody still playing.

use std::sync::Arc;
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::data::SerializedStore;
use pavlov_core::economy::{BalanceStore, Ledger};
use pavlov_core::factions::FactionRegistry;
use pavlov_core::json::{Stamp, null_as_default};
use pavlov_core::text::case::{IgnoreCaseMap, IgnoreCaseSet};
use pavlov_core::time::Clock;
use pavlov_rcon::OnlineRoster;
use pavlov_storage::datasets::{PAYROLL_STATE, WAGES};
use serde::{Deserialize, Serialize};

use crate::factions::RosterService;

/// The most runs kept in the wage history.
pub const HISTORY_LIMIT: usize = 500;

/// The ledger payroll credits through.
pub type SharedLedger = Ledger<Arc<dyn BalanceStore>>;

/// One payroll run.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PayrollRun {
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub at: DateTime<Utc>,
    pub faction: String,
    /// Banked this run: money that moved.
    #[serde(default, deserialize_with = "null_as_default")]
    pub paid: IgnoreCaseMap<i64>,
    /// Owed this run: completed periods, banked when they log off.
    #[serde(default, deserialize_with = "null_as_default")]
    pub accrued: IgnoreCaseMap<i64>,
    #[serde(default)]
    pub skipped: Option<String>,
}

impl PayrollRun {
    fn nothing(at: DateTime<Utc>, faction: &str, why: impl Into<String>) -> Self {
        Self {
            at,
            faction: faction.to_owned(),
            paid: IgnoreCaseMap::new(),
            accrued: IgnoreCaseMap::new(),
            skipped: Some(why.into()),
        }
    }

    #[must_use]
    pub fn total(&self) -> i64 {
        self.paid.values().sum()
    }

    #[must_use]
    pub fn did_something(&self) -> bool {
        !self.paid.is_empty() || !self.accrued.is_empty()
    }
}

/// The stored state. EVERY MAP TOLERATES A MISSING OR `null` PROPERTY, and every map compares
/// names ignoring case after a round trip: a live row written before `owed` existed once took
/// `/stats` down, and an ordinal map stops "Alice" finding "alice".
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PayrollState {
    /// Faction to when its last run was.
    #[serde(default, deserialize_with = "null_as_default")]
    pub last_paid: IgnoreCaseMap<Stamp>,
    /// Player to wage accrued and not yet banked.
    #[serde(default, deserialize_with = "null_as_default")]
    pub owed: IgnoreCaseMap<i64>,
    /// Player to seconds carried toward the next period.
    #[serde(
        default,
        deserialize_with = "null_as_default",
        rename = "onDutySeconds"
    )]
    pub earned: IgnoreCaseMap<i64>,
    /// Faction to who was on duty at its last run.
    #[serde(default, deserialize_with = "null_as_default", rename = "lastOnDuty")]
    pub previous: IgnoreCaseMap<Vec<String>>,
}

/// The payroll.
pub struct Payroll {
    store: Arc<SerializedStore>,
    ledger: Arc<SharedLedger>,
    rosters: Arc<RosterService>,
    online: Arc<dyn OnlineRoster>,
    clock: Arc<dyn Clock>,
    amount: i64,
    period: Duration,
    faction: String,
}

impl std::fmt::Debug for Payroll {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Payroll")
            .field("faction", &self.faction)
            .field("amount", &self.amount)
            .field("period", &self.period)
            .finish_non_exhaustive()
    }
}

/// What the payroll is built from.
pub struct PayrollParts {
    pub store: Arc<SerializedStore>,
    pub ledger: Arc<SharedLedger>,
    pub rosters: Arc<RosterService>,
    pub online: Arc<dyn OnlineRoster>,
    pub clock: Arc<dyn Clock>,
    pub amount: i64,
    pub period: Duration,
    /// `PAYROLL_FACTION`; `None` is the police.
    pub faction: Option<String>,
}

impl std::fmt::Debug for PayrollParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PayrollParts")
            .field("faction", &self.faction)
            .finish_non_exhaustive()
    }
}

impl Payroll {
    #[must_use]
    pub fn new(parts: PayrollParts) -> Self {
        Self {
            store: parts.store,
            ledger: parts.ledger,
            rosters: parts.rosters,
            online: parts.online,
            clock: parts.clock,
            amount: parts.amount,
            period: parts.period,
            faction: parts
                .faction
                .unwrap_or_else(|| FactionRegistry::POLICE_NAME.to_owned()),
        }
    }

    #[must_use]
    pub fn enabled(&self) -> bool {
        self.amount > 0 && !self.period.is_zero() && self.rosters.enabled()
    }

    #[must_use]
    pub fn amount(&self) -> i64 {
        self.amount
    }

    #[must_use]
    pub fn period(&self) -> Duration {
        self.period
    }

    #[must_use]
    pub fn faction(&self) -> &str {
        &self.faction
    }

    fn period_seconds(&self) -> i64 {
        i64::try_from(self.period.as_secs())
            .unwrap_or(i64::MAX)
            .max(1)
    }

    fn state(&self) -> PayrollState {
        self.store
            .read(PAYROLL_STATE, PayrollState::default())
            .unwrap_or_else(|error| {
                tracing::error!(%error, "cannot read the payroll state");
                PayrollState::default()
            })
    }

    /// One run: bank what is owed to those who left, then credit those on duty.
    pub async fn run(&self) -> PayrollRun {
        let now = self.clock.now();
        if !self.enabled() {
            return PayrollRun::nothing(now, &self.faction, "payroll is not configured");
        }
        let Some(faction) = self.rosters.factions().get(Some(&self.faction)).cloned() else {
            return PayrollRun::nothing(
                now,
                &self.faction,
                format!("no faction named \"{}\"", self.faction),
            );
        };
        // The CURRENT name from here on: a configured former name would split the pay clock.
        let name = faction.name.clone();

        // Before ANYTHING, settlement included: it needs to know who is offline too.
        if !self.online.is_trustworthy() {
            tracing::warn!(faction = %name, "payroll SKIPPED - no server has a fresh player list; nothing accrued, nothing banked, no period consumed");
            return PayrollRun::nothing(now, &name, "no server has a fresh player list");
        }

        let mut present = IgnoreCaseSet::new();
        for player in self.online.confirmed_online() {
            present.insert(player);
        }
        let roster = self.rosters.roster(&faction);
        let paid = self.settle(&present).await;

        let state = self.state();
        let was_on_duty: IgnoreCaseSet = state
            .previous
            .get(&name)
            .cloned()
            .unwrap_or_default()
            .into_iter()
            .collect();
        // CAPPED AT ONE PERIOD: nobody was watching during an outage.
        let since_last = state
            .last_paid
            .get(&name)
            .map_or(TimeDelta::zero(), |last| now - last.0)
            .num_seconds()
            .max(0);
        let credited = since_last.min(self.period_seconds());

        let on_duty: Vec<String> = roster
            .into_iter()
            .map(|m| m.player)
            .filter(|p| present.contains(p))
            .collect();

        let mut accrued = IgnoreCaseMap::new();
        let mut carried = IgnoreCaseMap::new();
        for player in &on_duty {
            // Only time between two sightings counts: a first sighting starts from now.
            let mut seconds = state.earned.get(player).copied().unwrap_or(0);
            if was_on_duty.contains(player) {
                seconds += credited;
            }
            let periods = seconds / self.period_seconds();
            if periods > 0 {
                accrued.insert(player.clone(), periods.saturating_mul(self.amount));
                seconds -= periods * self.period_seconds();
            }
            carried.insert(player.clone(), seconds);
        }

        self.stamp(now, &name, &accrued, carried, &on_duty).await;

        if !accrued.is_empty() {
            tracing::info!(
                faction = %name,
                total = accrued.values().sum::<i64>(),
                members = accrued.len(),
                "payroll accrued for members who completed a full period - banked when they log off"
            );
        }
        let skipped = if on_duty.is_empty() {
            Some(format!("no {name} member is online"))
        } else if accrued.is_empty() {
            Some("nobody has completed a full period yet".into())
        } else {
            None
        };
        let run = PayrollRun {
            at: now,
            faction: name,
            paid,
            accrued,
            skipped,
        };
        if !run.paid.is_empty() {
            self.record(&run).await;
        }
        run
    }

    async fn record(&self, run: &PayrollRun) {
        let run = run.clone();
        let result = self
            .store
            .update(
                WAGES,
                Vec::new,
                move |mut history: Vec<Option<PayrollRun>>| {
                    history.push(Some(run));
                    if history.len() > HISTORY_LIMIT {
                        let excess = history.len() - HISTORY_LIMIT;
                        history.drain(..excess);
                    }
                    Some(history)
                },
            )
            .await;
        if let Err(error) = result {
            tracing::error!(%error, "payroll paid but could not record the run in the wage history");
        }
    }

    /// Bank every wage owed to somebody who is not on a fresh roster.
    async fn settle(&self, present: &IgnoreCaseSet) -> IgnoreCaseMap<i64> {
        let mut paid = IgnoreCaseMap::new();
        let owed: Vec<(String, i64)> = self
            .state()
            .owed
            .iter()
            .filter(|(player, wage)| **wage > 0 && !present.contains(player))
            .map(|(p, w)| (p.to_owned(), *w))
            .collect();

        for (player, wage) in owed {
            let change = self.ledger.credit(&player, wage).await;
            if !change.ok {
                tracing::debug!(%player, wage, "payroll could not bank the wage yet - still owed");
                continue;
            }
            paid.insert(player.clone(), wage);

            // NOT CANCELLABLE: the money has moved, this only records it. Spawned, so dropping
            // the run here cannot leave the wage owed after it was banked (and paid again).
            let store = Arc::clone(&self.store);
            let who = player.clone();
            let settled = tokio::spawn(async move {
                store
                    .update(
                        PAYROLL_STATE,
                        PayrollState::default,
                        move |mut state: PayrollState| {
                            // Subtract what was banked: a period may have accrued since the read.
                            let current = *state.owed.get(&who)?;
                            let remaining = current - wage;
                            if remaining > 0 {
                                state.owed.insert(who, remaining);
                            } else {
                                state.owed.remove(&who);
                            }
                            Some(state)
                        },
                    )
                    .await
            })
            .await;
            let failure = match settled {
                Ok(Ok(r)) if r.ok || r.is_vetoed() => None,
                Ok(Ok(r)) => r.error,
                Ok(Err(error)) => Some(error.to_string()),
                Err(error) => Some(error.to_string()),
            };
            if let Some(error) = failure {
                tracing::error!(%player, wage, %error, "payroll BANKED a wage but could not clear the debt - it will be paid AGAIN next run unless payroll state is fixed");
            }
            tracing::info!(%player, wage, before = change.before, after = change.after, "payroll banked a wage");
        }
        paid
    }

    async fn stamp(
        &self,
        now: DateTime<Utc>,
        faction: &str,
        accrued: &IgnoreCaseMap<i64>,
        carried: IgnoreCaseMap<i64>,
        on_duty: &[String],
    ) {
        let faction = faction.to_owned();
        let accrued: Vec<(String, i64)> = accrued.iter().map(|(p, w)| (p.to_owned(), *w)).collect();
        let on_duty = on_duty.to_vec();
        let result = self
            .store
            .update(
                PAYROLL_STATE,
                PayrollState::default,
                move |mut state: PayrollState| {
                    state.last_paid.insert(faction.clone(), Stamp(now));
                    for (player, wage) in accrued {
                        let total = state
                            .owed
                            .get(&player)
                            .copied()
                            .unwrap_or(0)
                            .saturating_add(wage);
                        state.owed.insert(player, total);
                    }
                    // Kept for members who go offline too, so logging off banks the partial period.
                    // A zero is removed: an absent key already means "nothing carried".
                    for (player, seconds) in carried.iter() {
                        if *seconds > 0 {
                            state.earned.insert(player.to_owned(), *seconds);
                        } else {
                            state.earned.remove(player);
                        }
                    }
                    state.previous.insert(faction, on_duty);
                    Some(state)
                },
            )
            .await;
        match result {
            Ok(r) if r.ok => {}
            Ok(r) => tracing::error!(
                error = r.error.as_deref().unwrap_or(""),
                "payroll could not record the run - this period's accrual is lost"
            ),
            Err(error) => {
                tracing::error!(%error, "payroll could not record the run - this period's accrual is lost")
            }
        }
    }

    /// Seconds carried toward this player's next period.
    #[must_use]
    pub fn earned_toward_next_period(&self, player: &str) -> Duration {
        let seconds = self.state().earned.get(player.trim()).copied().unwrap_or(0);
        Duration::from_secs(u64::try_from(seconds).unwrap_or(0))
    }

    /// Accrued and not yet banked.
    #[must_use]
    pub fn owed_to(&self, player: &str) -> i64 {
        self.state().owed.get(player.trim()).copied().unwrap_or(0)
    }

    /// Everybody carrying an unbanked wage, most first.
    #[must_use]
    pub fn outstanding(&self) -> Vec<(String, i64)> {
        let mut rows: Vec<(String, i64)> = self
            .state()
            .owed
            .iter()
            .filter(|(_, w)| **w > 0)
            .map(|(p, w)| (p.to_owned(), *w))
            .collect();
        rows.sort_by_key(|row| std::cmp::Reverse(row.1));
        rows
    }

    fn wages(&self) -> Vec<PayrollRun> {
        self.store
            .read::<Vec<Option<PayrollRun>>>(WAGES, Vec::new())
            .unwrap_or_default()
            .into_iter()
            .flatten()
            .collect()
    }

    /// The latest runs that moved money, newest first.
    #[must_use]
    pub fn history(&self, take: usize) -> Vec<PayrollRun> {
        let mut runs = self.wages();
        runs.sort_by_key(|run| std::cmp::Reverse(run.at));
        runs.truncate(take);
        runs
    }

    /// Everything ever banked for this player.
    #[must_use]
    pub fn total_paid_to(&self, player: &str) -> i64 {
        self.wages().iter().filter_map(|r| r.paid.get(player)).sum()
    }
}
