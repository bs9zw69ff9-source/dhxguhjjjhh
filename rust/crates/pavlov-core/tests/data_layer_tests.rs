//! Port of `DataLayerTests.cs`: SerializedStoreTests and RosterWriteGuardTests.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod serialized_store_tests {
    //! Ported from test/database.test.js by way of the C#, plus the concurrency properties
    //! the JS suite could only assert indirectly. The real codec (`serde_json`) is used: the
    //! C# file warns about a local codec stub that once shadowed the real one and hid the Node
    //! interop bug, and there is no stub here either.

    use std::sync::{Arc, Barrier, Mutex};
    use std::time::Duration;

    use indexmap::IndexMap;
    use pavlov_core::data::{KeyValueBackend, MemoryBackend, SerializedStore, StoreError};
    use pavlov_core::text::case::IgnoreCaseMap;

    fn new_store() -> (SerializedStore, Arc<MemoryBackend>) {
        let backend = Arc::new(MemoryBackend::new());
        (SerializedStore::new(backend.clone()), backend)
    }

    fn push(mut list: Vec<String>, item: &str) -> Option<Vec<String>> {
        list.push(item.to_owned());
        Some(list)
    }

    #[test]
    fn missing_dataset_returns_the_fallback() {
        let (store, _) = new_store();
        assert!(store.read("bans", Vec::<String>::new()).unwrap().is_empty());
    }

    #[test]
    fn corrupt_json_returns_the_fallback_rather_than_throwing() {
        // One bad character must not take down every command that touches bans.
        let (store, backend) = new_store();
        backend.seed("bans", "{ this is not json");
        assert!(store.read("bans", Vec::<String>::new()).unwrap().is_empty());
    }

    #[tokio::test]
    async fn an_unreadable_dataset_is_never_overwritten() {
        /* The data-loss bug: an unparseable dataset read as the empty fallback, the update was
        applied to that, and the result saved - replacing every ban with one. */
        let (store, backend) = new_store();
        backend.seed("bans", "{ this is not json");

        let result = store
            .update("bans", Vec::new, |list| push(list, "Griefer"))
            .await
            .unwrap();

        assert!(!result.ok);
        assert!(result.error.unwrap().contains("cannot be read"));
        assert_eq!(
            store.read_raw("bans").unwrap().as_deref(),
            Some("{ this is not json")
        );
        assert_eq!(backend.writes(), 0);
    }

    #[tokio::test]
    async fn an_unreadable_dataset_is_reported_once_until_it_recovers() {
        let reported = Arc::new(Mutex::new(Vec::<String>::new()));
        let backend = Arc::new(MemoryBackend::new());
        let sink = reported.clone();
        let store = SerializedStore::new(backend.clone())
            .with_unreadable_hook(move |key| sink.lock().unwrap().push(key.to_owned()));
        backend.seed("bans", "{ broken");

        store.read("bans", Vec::<String>::new()).unwrap();
        store.read("bans", Vec::<String>::new()).unwrap();
        store
            .update("bans", Vec::<String>::new, Some)
            .await
            .unwrap();
        assert_eq!(*reported.lock().unwrap(), ["bans"]);
        assert_eq!(store.unreadable(), ["bans"]);

        backend.seed("bans", "[]");
        store.read("bans", Vec::<String>::new()).unwrap();
        assert!(store.unreadable().is_empty());

        backend.seed("bans", "{ broken again");
        store.read("bans", Vec::<String>::new()).unwrap();
        assert_eq!(*reported.lock().unwrap(), ["bans", "bans"]);
    }

    #[tokio::test]
    async fn an_absent_or_null_dataset_is_still_created_by_an_update() {
        // Absent is not unreadable: the first write to a new dataset must still happen.
        let (store, backend) = new_store();
        backend.seed("warrants", "null");

        let result = store
            .update("warrants", Vec::new, |list| push(list, "x"))
            .await
            .unwrap();

        assert!(result.ok);
        assert_eq!(store.read("warrants", Vec::<String>::new()).unwrap(), ["x"]);
    }

    #[tokio::test]
    async fn update_reads_modifies_and_persists() {
        let (store, _) = new_store();
        let result = store
            .update("bans", Vec::new, |list| push(list, "Griefer"))
            .await
            .unwrap();

        assert!(result.ok);
        assert_eq!(
            store.read("bans", Vec::<String>::new()).unwrap(),
            ["Griefer"]
        );
    }

    #[tokio::test]
    async fn reads_are_isolated_mutating_what_you_read_cannot_corrupt_the_store() {
        /* In JS this needed an explicit structuredClone on every read; here it falls out of
        deserialising per read. The property is what matters, not the mechanism. */
        let (store, _) = new_store();
        store
            .update("bans", Vec::new, |list| push(list, "Griefer"))
            .await
            .unwrap();

        let mut mine = store.read("bans", Vec::<String>::new()).unwrap();
        mine.push("NotReallyBanned".into());

        assert_eq!(store.read("bans", Vec::<String>::new()).unwrap().len(), 1);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn concurrent_updates_are_serialised_no_lost_writes() {
        /* The bug this prevents: two bans land at once, the second overwrites the first,
        and the missing ban surfaces days later as "we banned them, they're still on". */
        let (store, _) = new_store();
        let store = Arc::new(store);
        const WRITERS: usize = 40;

        let tasks: Vec<_> = (0..WRITERS)
            .map(|i| {
                let store = store.clone();
                tokio::spawn(async move {
                    store
                        .update("bans", Vec::new, |list| push(list, &format!("player{i}")))
                        .await
                        .unwrap()
                })
            })
            .collect();
        for task in tasks {
            assert!(task.await.unwrap().ok);
        }

        assert_eq!(
            store.read("bans", Vec::<String>::new()).unwrap().len(),
            WRITERS
        );
    }

    #[tokio::test]
    async fn a_throwing_mutator_does_not_poison_the_queue() {
        /* The critical resilience property. Without it one bad mutator wedges every later
        write to that dataset, and the bot appears to hang on any command that saves. */
        let (store, _) = new_store();
        store
            .update("bans", Vec::new, |list| push(list, "first"))
            .await
            .unwrap();

        let failed = store
            .try_update("bans", Vec::<String>::new, |_| Err("boom"))
            .await
            .unwrap();
        assert!(!failed.ok);
        assert!(failed.error.unwrap().contains("boom"));
        assert_eq!(failed.value, ["first"]); // the value still stored

        let after = store
            .update("bans", Vec::new, |list| push(list, "second"))
            .await
            .unwrap();
        assert!(after.ok);
        assert_eq!(
            store.read("bans", Vec::<String>::new()).unwrap(),
            ["first", "second"]
        );
    }

    #[tokio::test]
    async fn a_vetoing_mutator_leaves_the_dataset_untouched_and_writes_nothing() {
        // Returning None is how "insufficient funds" is expressed - it is not an error.
        let (store, backend) = new_store();
        store
            .update("balance", || 100, |_| Some(100))
            .await
            .unwrap();
        let writes_before = backend.writes();

        let vetoed = store
            .update(
                "balance",
                || 100,
                |mut balance: i64| {
                    balance -= 1_000; // changed, then declined: the store reports what is STORED
                    (balance >= 0).then_some(balance)
                },
            )
            .await
            .unwrap();

        assert!(!vetoed.ok);
        assert!(vetoed.is_vetoed());
        assert_eq!(vetoed.value, 100);
        assert_eq!(backend.writes(), writes_before);
        assert_eq!(store.read("balance", 0).unwrap(), 100);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn different_datasets_do_not_block_each_other() {
        /* Locking is per key. The mutator runs SYNCHRONOUSLY inside the lock, so the blocking
        one holds a worker thread until released; the other dataset's update must finish
        on another worker meanwhile. */
        let (store, _) = new_store();
        let store = Arc::new(store);
        let release = Arc::new(Barrier::new(2));

        let slow = {
            let (store, release) = (store.clone(), release.clone());
            tokio::spawn(async move {
                store
                    .update("bans", Vec::<String>::new, |list| {
                        release.wait();
                        Some(list)
                    })
                    .await
                    .unwrap()
            })
        };
        let fast = {
            let store = store.clone();
            tokio::spawn(async move {
                store
                    .update("playtime", Vec::new, |list| push(list, "x"))
                    .await
                    .unwrap()
            })
        };

        let finished = tokio::time::timeout(Duration::from_secs(3), fast).await;
        // Let the slow one go whatever happened, so a failure cannot hang the test.
        tokio::task::spawn_blocking(move || release.wait())
            .await
            .unwrap();
        slow.await.unwrap();

        assert!(finished.is_ok(), "the second dataset waited on the first");
        assert_eq!(store.read("playtime", Vec::<String>::new()).unwrap(), ["x"]);
    }

    // ---- the comparer a stored dictionary comes back with ----

    /// The C# pinned a BUG here: a plain read handed back an ordinal dictionary whatever
    /// comparer the fallback had, because JSON carries none. In Rust the comparer is the
    /// TYPE, so the bug cannot be written: what this pins instead is that an ordinal map
    /// stays ordinal and an `IgnoreCaseMap` read of the same row does not.
    #[tokio::test]
    async fn plain_read_loses_the_case_insensitive_comparer() {
        let (store, _) = new_store();
        store
            .update("exempt", IgnoreCaseMap::<i32>::new, |mut map| {
                map.insert("Holosight1", 1);
                Some(map)
            })
            .await
            .unwrap();

        let ordinal: IndexMap<String, i32> = store.read("exempt", IndexMap::new()).unwrap();
        assert!(ordinal.contains_key("Holosight1"));
        assert!(!ordinal.contains_key("holosight1"));

        let folded: IgnoreCaseMap<i32> = store.read("exempt", IgnoreCaseMap::new()).unwrap();
        assert!(folded.contains_key("holosight1"));
    }

    #[tokio::test]
    async fn read_map_keeps_the_comparer_the_caller_asked_for() {
        let (store, _) = new_store();
        store
            .update("exempt", IgnoreCaseMap::<i32>::new, |mut map| {
                map.insert("Holosight1", 1);
                Some(map)
            })
            .await
            .unwrap();

        let read: IgnoreCaseMap<i32> = store.read("exempt", IgnoreCaseMap::new()).unwrap();

        assert!(read.contains_key("holosight1"));
        assert!(read.contains_key("HOLOSIGHT1"));
    }

    #[tokio::test]
    async fn update_map_replaces_an_entry_rather_than_adding_a_case_variant() {
        /* The write half. Handed an ordinal dictionary, `map[name] = value` adds a SECOND
        entry differing only in case, and Remove misses - so an exemption granted as
        "Holosight1" is never cleared by an unban filed as "holosight1". */
        let (store, _) = new_store();
        let set = |name: &'static str, value: i32| {
            move |mut map: IgnoreCaseMap<i32>| {
                map.insert(name, value);
                Some(map)
            }
        };

        store
            .update("exempt", IgnoreCaseMap::new, set("Holosight1", 1))
            .await
            .unwrap();
        store
            .update("exempt", IgnoreCaseMap::new, set("holosight1", 2))
            .await
            .unwrap();

        let read: IgnoreCaseMap<i32> = store.read("exempt", IgnoreCaseMap::new()).unwrap();
        assert_eq!(read.len(), 1);
        assert_eq!(read.iter().next(), Some(("Holosight1", &2)));

        store
            .update("exempt", IgnoreCaseMap::<i32>::new, |mut map| {
                map.remove("HOLOSIGHT1");
                Some(map)
            })
            .await
            .unwrap();
        assert!(
            store
                .read("exempt", IgnoreCaseMap::<i32>::new())
                .unwrap()
                .is_empty()
        );
    }

    // ---- beyond the C# suite ----

    /// A backend that cannot write is an ERROR to the caller, never a quiet success.
    #[tokio::test]
    async fn a_failing_backend_is_reported_not_swallowed() {
        struct Broken;
        impl KeyValueBackend for Broken {
            fn read(&self, _: &str) -> std::io::Result<Option<String>> {
                Ok(None)
            }
            fn write(&self, _: &str, _: &str) -> std::io::Result<()> {
                Err(std::io::Error::other("disk full"))
            }
        }
        let store = SerializedStore::new(Arc::new(Broken));

        let error = store
            .update("bans", Vec::new, |list| push(list, "x"))
            .await
            .unwrap_err();

        assert!(matches!(error, StoreError::Backend { ref key, .. } if key == "bans"));
        assert!(error.to_string().contains("disk full"));
    }

    #[tokio::test]
    async fn a_blank_dataset_name_is_refused() {
        let (store, backend) = new_store();
        assert!(matches!(
            store.write("  ", &1).await,
            Err(StoreError::BlankKey)
        ));
        assert_eq!(backend.writes(), 0);
    }

    #[tokio::test]
    async fn rows_are_written_indented_as_the_csharp_writes_them() {
        let (store, backend) = new_store();
        store.write("bans", &vec!["a"]).await.unwrap();
        assert_eq!(
            backend.read("bans").unwrap().as_deref(),
            Some("[\n  \"a\"\n]")
        );
    }
}

mod roster_write_guard_tests {
    use pavlov_core::data::{RosterWriteDecision, RosterWriteGuard};

    fn roster(n: usize) -> Vec<String> {
        (0..n).map(|i| format!("player{i}")).collect()
    }

    #[test]
    fn a_small_change_is_allowed() {
        let current = roster(20);
        let mut proposed: Vec<String> = current.iter().take(19).cloned().collect();
        proposed.push("newMember".into());
        let v = RosterWriteGuard::evaluate(Some(&current), Some(&proposed), true, false);
        assert!(v.is_allowed());
        assert_eq!(v.dropped, 1);
    }

    #[test]
    fn a_wipe_is_refused() {
        // An empty roster is VALID to Pavlov; a parse bug producing one strips a faction silently.
        let v = RosterWriteGuard::evaluate(Some(&roster(20)), Some(&[]), true, false);
        assert!(!v.is_allowed());
        assert_eq!(v.decision, RosterWriteDecision::RefusedBulkDrop);
        assert_eq!(v.dropped, 20);
        assert!(v.explanation.unwrap().contains("suspected corruption"));
    }

    #[test]
    fn the_limit_boundary_is_exact() {
        let current = roster(20);
        assert!(
            RosterWriteGuard::evaluate(Some(&current), Some(&current[..15]), true, false)
                .is_allowed()
        ); // 5 dropped
        assert!(
            !RosterWriteGuard::evaluate(Some(&current), Some(&current[..14]), true, false)
                .is_allowed()
        ); // 6 dropped
    }

    #[test]
    fn an_unreadable_file_is_refused_not_treated_as_empty() {
        let v = RosterWriteGuard::evaluate(None, Some(&["someone".to_owned()]), true, false);
        assert!(!v.is_allowed());
        assert_eq!(v.decision, RosterWriteDecision::RefusedUnreadable);
    }

    #[test]
    fn a_missing_file_is_allowed_creating_a_roster_is_legitimate() {
        assert!(
            RosterWriteGuard::evaluate(None, Some(&["someone".to_owned()]), false, false)
                .is_allowed()
        );
    }

    #[test]
    fn an_empty_existing_roster_can_be_filled() {
        assert!(RosterWriteGuard::evaluate(Some(&[]), Some(&roster(30)), true, false).is_allowed());
    }

    #[test]
    fn a_deliberate_wipe_is_allowed_when_asked_for() {
        assert!(RosterWriteGuard::evaluate(Some(&roster(50)), Some(&[]), true, true).is_allowed());
    }

    #[test]
    fn a_null_payload_is_refused() {
        assert_eq!(
            RosterWriteGuard::evaluate(Some(&roster(10)), None, true, false).decision,
            RosterWriteDecision::RefusedInvalidPayload
        );
    }

    #[test]
    fn comparison_is_case_insensitive() {
        let current = roster(20);
        let proposed: Vec<String> = current.iter().map(|n| n.to_uppercase()).collect();
        let v = RosterWriteGuard::evaluate(Some(&current), Some(&proposed), true, false);
        assert!(v.is_allowed());
        assert_eq!(v.dropped, 0);
    }

    #[test]
    fn split_lines_normalises_the_way_the_game_reads_it() {
        assert_eq!(
            RosterWriteGuard::split_lines(Some("alice\r\n  bob  \n\n\ncarol\n")),
            ["alice", "bob", "carol"]
        );
    }
}
