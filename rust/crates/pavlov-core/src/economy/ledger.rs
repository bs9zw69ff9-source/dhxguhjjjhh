//! Serialised balance changes (C# `Core/Economy/Ledger.cs`).
//!
//! The store is a plain read-then-write. Two concurrent payouts would both read 500 and both
//! write 600, and one payout would vanish with nothing erroring anywhere. Every change to one
//! player's balance therefore runs under that player's stripe of a [`KeyedLock`].

use std::convert::Infallible;

use crate::concurrency::{KeyComparison, KeyedLock};

/// The outcome of one change. `before == after` when nothing was written.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BalanceChange {
    pub ok: bool,
    pub before: i64,
    pub after: i64,
}

impl BalanceChange {
    /// `after - before`.
    #[must_use]
    pub fn delta(&self) -> i64 {
        self.after - self.before
    }
}

/// Where balances live. `read` keeps "never had a balance" (`None`) distinct from zero.
pub trait BalanceStore: Send + Sync {
    fn read(&self, player_id: &str) -> Option<i64>;
    /// `false` when the write did not happen.
    fn write(&self, player_id: &str, balance: i64) -> bool;
}

/// The ledger over a store.
#[derive(Debug)]
pub struct Ledger<S> {
    store: S,
    locks: KeyedLock,
}

impl<S: BalanceStore> Ledger<S> {
    #[must_use]
    pub fn new(store: S) -> Self {
        Self {
            store,
            locks: KeyedLock::new(KeyComparison::IgnoreCase),
        }
    }

    /// The underlying store.
    pub fn store(&self) -> &S {
        &self.store
    }

    /// Apply `mutate` to the player's balance under their lock.
    ///
    /// `mutate` returns `Ok(None)` to VETO (how "insufficient funds" is expressed: an ordinary
    /// outcome, not an error) and `Err` for a bug in the caller, which PROPAGATES. The C# once
    /// reported a throwing mutator as an ordinary failed change, which made every such bug look
    /// like "insufficient funds" and nobody ever looked. Either way nothing is written and the
    /// lock is released. A failed write reports the OLD balance, never the intended one: that is
    /// how a bot tells a player they were paid when they were not.
    ///
    /// # Errors
    /// Whatever `mutate` returns.
    pub async fn mutate<E>(
        &self,
        player_id: &str,
        mutate: impl FnOnce(i64) -> Result<Option<i64>, E>,
    ) -> Result<BalanceChange, E> {
        let _guard = self.locks.acquire(player_id).await;
        // Absent reads as zero HERE, at the point of use, so the store keeps the distinction.
        let before = self.store.read(player_id).unwrap_or(0);
        let Some(after) = mutate(before)? else {
            return Ok(BalanceChange {
                ok: false,
                before,
                after: before,
            });
        };
        let ok = self.store.write(player_id, after);
        Ok(BalanceChange {
            ok,
            before,
            after: if ok { after } else { before },
        })
    }

    /// Add `amount`. A sum that would overflow is VETOED rather than wrapped: the C# `long`
    /// wrapped silently into a huge negative balance.
    pub async fn credit(&self, player_id: &str, amount: i64) -> BalanceChange {
        let result = self
            .mutate::<Infallible>(player_id, |before| Ok(before.checked_add(amount)))
            .await;
        match result {
            Ok(change) => change,
            Err(never) => match never {},
        }
    }

    /// Take `amount`, refusing (a veto) when the balance cannot cover it. Exactly zero is fine.
    pub async fn debit(&self, player_id: &str, amount: i64) -> BalanceChange {
        let result = self
            .mutate::<Infallible>(player_id, |before| {
                Ok(if before >= amount {
                    before.checked_sub(amount)
                } else {
                    None
                })
            })
            .await;
        match result {
            Ok(change) => change,
            Err(never) => match never {},
        }
    }
}
