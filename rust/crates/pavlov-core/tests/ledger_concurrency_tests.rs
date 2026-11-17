//! Port of `LedgerConcurrencyTests.cs`: the ledger under concurrent mutation of ONE player.
//!
//! The store deliberately WIDENS the race: a read-modify-write only loses an update when a
//! second writer lands between the read and the write, and against an in-memory map that
//! window is nanoseconds wide, so a broken lock would pass by luck. The sleep holds it open.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use pavlov_core::concurrency::{KeyComparison, KeyedLock};
use pavlov_core::economy::{BalanceStore, Ledger};
use pavlov_core::text::case::fold_case;

#[derive(Default)]
struct WideningStore {
    balances: Mutex<HashMap<String, i64>>,
}

impl WideningStore {
    fn current(&self, player_id: &str) -> i64 {
        self.balances
            .lock()
            .unwrap()
            .get(&fold_case(player_id))
            .copied()
            .unwrap_or(0)
    }
}

impl BalanceStore for WideningStore {
    fn read(&self, player_id: &str) -> Option<i64> {
        let value = self
            .balances
            .lock()
            .unwrap()
            .get(&fold_case(player_id))
            .copied();
        // The window. Without it a broken lock passes by luck.
        std::thread::sleep(Duration::from_millis(1));
        value
    }

    fn write(&self, player_id: &str, balance: i64) -> bool {
        self.balances
            .lock()
            .unwrap()
            .insert(fold_case(player_id), balance);
        true
    }
}

fn ledger() -> Arc<Ledger<WideningStore>> {
    Arc::new(Ledger::new(WideningStore::default()))
}

#[tokio::test(flavor = "multi_thread", worker_threads = 8)]
async fn concurrent_credits_to_one_player_are_all_applied() {
    // Fifty concurrent +1 credits must leave exactly 50: the "bot ate my payout" report.
    let ledger = ledger();
    let tasks: Vec<_> = (0..50)
        .map(|_| {
            let ledger = Arc::clone(&ledger);
            tokio::spawn(async move { ledger.credit("Alice", 1).await })
        })
        .collect();
    for t in tasks {
        t.await.unwrap();
    }
    assert_eq!(ledger.store().current("Alice"), 50);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn player_ids_that_differ_only_by_case_are_the_same_player() {
    // If the lock disagreed with the store about case, the spellings would race on one entry.
    let ledger = ledger();
    let tasks: Vec<_> = ["Alice", "alice", "ALICE"]
        .into_iter()
        .map(|name| {
            let ledger = Arc::clone(&ledger);
            tokio::spawn(async move { ledger.credit(name, 1).await })
        })
        .collect();
    for t in tasks {
        t.await.unwrap();
    }
    assert_eq!(ledger.store().current("Alice"), 3);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 8)]
async fn different_players_do_not_serialise_behind_each_other() {
    // Catches a GLOBAL lock: fifty 1 ms round trips end to end would be at least 50 ms per
    // worker; the bound is generous on purpose.
    let ledger = ledger();
    let started = Instant::now();
    let tasks: Vec<_> = (0..50)
        .map(|i| {
            let ledger = Arc::clone(&ledger);
            tokio::spawn(async move { ledger.credit(&format!("player{i}"), 1).await })
        })
        .collect();
    for t in tasks {
        t.await.unwrap();
    }
    assert!(
        started.elapsed() < Duration::from_millis(500),
        "50 distinct players serialised, so the lock is global rather than per key"
    );
}

#[tokio::test]
async fn a_throwing_mutator_propagates_rather_than_looking_like_a_veto() {
    let ledger = ledger();
    assert_eq!(ledger.mutate("Alice", |_| Err("bug")).await, Err("bug"));
}

#[tokio::test]
async fn the_lock_is_released_after_a_mutator_throws() {
    // A stripe left held would wedge every later caller that hashes to it.
    let ledger = ledger();
    assert!(ledger.mutate("Alice", |_| Err("bug")).await.is_err());
    let after = tokio::time::timeout(Duration::from_secs(5), ledger.credit("Alice", 5))
        .await
        .unwrap();
    assert!(after.ok);
    assert_eq!(ledger.store().current("Alice"), 5);
}

/// THE OLD SCHEME, shown deterministically where its flaw lives: a map of per-key locks that
/// reclaims idle entries admits two holders for one key. Kept as a regression test against
/// anyone "fixing" the fixed stripe set by reintroducing reclamation.
#[tokio::test]
async fn the_old_reclaiming_lock_map_admits_two_holders_for_one_key() {
    use tokio::sync::Semaphore;

    let gates: Mutex<HashMap<String, Arc<Semaphore>>> = Mutex::new(HashMap::new());
    let get = || {
        Arc::clone(
            gates
                .lock()
                .unwrap()
                .entry("alice".to_owned())
                .or_insert_with(|| Arc::new(Semaphore::new(1))),
        )
    };

    // A takes the lock.
    let a = get();
    let a_permit = Arc::clone(&a).acquire_owned().await.unwrap();

    // B reads the SAME lock and is descheduled before acquiring it.
    let b = get();

    // A finishes and reclaims, as the old finally block did.
    drop(a_permit);
    assert_eq!(a.available_permits(), 1); // "nobody is waiting"
    gates.lock().unwrap().remove("alice");

    // B resumes and acquires the lock it captured.
    let _b_permit = Arc::clone(&b).acquire_owned().await.unwrap();

    // C arrives, finds no entry, and builds a second lock for the same key.
    let c = get();
    assert!(!Arc::ptr_eq(&b, &c));

    // Both are now inside the critical section for one player.
    assert!(
        c.try_acquire().is_ok(),
        "C should not have been able to enter while B holds the key - two locks, one key"
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 8)]
async fn keyed_lock_admits_one_holder_per_key() {
    let locks = Arc::new(KeyedLock::new(KeyComparison::Ordinal));
    let concurrent = Arc::new(AtomicUsize::new(0));
    let peak = Arc::new(AtomicUsize::new(0));

    let tasks: Vec<_> = (0..40)
        .map(|_| {
            let (locks, concurrent, peak) = (
                Arc::clone(&locks),
                Arc::clone(&concurrent),
                Arc::clone(&peak),
            );
            tokio::spawn(async move {
                let _handle = locks.acquire("k").await;
                let now = concurrent.fetch_add(1, Ordering::SeqCst) + 1;
                peak.fetch_max(now, Ordering::SeqCst);
                tokio::time::sleep(Duration::from_millis(2)).await;
                concurrent.fetch_sub(1, Ordering::SeqCst);
            })
        })
        .collect();
    for t in tasks {
        t.await.unwrap();
    }
    assert_eq!(peak.load(Ordering::SeqCst), 1);
}

#[tokio::test]
async fn disposing_a_keyed_lock_handle_twice_does_not_admit_a_second_holder() {
    // In the C# a double Dispose could raise the semaphore above one. In Rust a guard is
    // released exactly once, when it is dropped; dropping it twice does not compile. What is
    // left to assert is the consequence: after a release, one holder again excludes the next.
    let locks = KeyedLock::new(KeyComparison::Ordinal);
    drop(locks.acquire("k").await);

    let _first = locks.acquire("k").await;
    let second = tokio::time::timeout(Duration::from_millis(50), locks.acquire("k")).await;
    assert!(
        second.is_err(),
        "a second holder got in while the first held the key"
    );
}
