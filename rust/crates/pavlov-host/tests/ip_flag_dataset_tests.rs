//! Port of `IpFlagDatasetTests.cs`: the flags live in `ip_flags`, never over the Node bot's
//! `user_blacklist` ARRAY of barred Discord ids, which an older C# build wrote an object over.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;

use common::file_store;
use pavlov_core::data::KeyValueBackend;
use pavlov_core::data::SerializedStore;
use pavlov_core::evasion::FlagSet;
use pavlov_core::time::SystemClock;
use pavlov_host::logs::IpTrackingService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_storage::FileKeyValueBackend;
use pavlov_storage::datasets::{IP_FLAGS, USER_BLACKLIST};

struct Fixture {
    dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    tracking: IpTrackingService,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = file_store(dir.path());
        let tracking = IpTrackingService::new(
            Arc::clone(&store),
            Arc::new(MetricsRegistry::default()),
            Arc::new(SystemClock),
        );
        Self {
            dir,
            store,
            tracking,
        }
    }
}

fn flags(ips: &[&str], names: &[&str], ids: &[&str]) -> FlagSet {
    FlagSet {
        ips: ips.iter().map(|s| (*s).to_owned()).collect(),
        names: names.iter().map(|s| (*s).to_owned()).collect(),
        ids: ids.iter().map(|s| (*s).to_owned()).collect(),
        ..FlagSet::default()
    }
}

#[tokio::test]
async fn flags_are_written_to_ip_flags_not_over_the_node_blacklist() {
    let f = Fixture::new();
    f.tracking.flag_address_manually("203.0.113.9").await;

    let stored: FlagSet = f.store.read(IP_FLAGS, FlagSet::default()).unwrap();
    assert!(stored.manual_ips.contains("203.0.113.9"));
    // The critical half: user_blacklist untouched, or Node calls .map on an object.
    assert_eq!(
        FileKeyValueBackend::new(f.dir.path())
            .unwrap()
            .read(USER_BLACKLIST)
            .unwrap(),
        None
    );
}

#[tokio::test]
async fn the_node_barred_user_list_survives_a_flag_write() {
    let f = Fixture::new();
    let barred = vec![
        "1014251293159731310".to_owned(),
        "678362059905171471".to_owned(),
    ];
    f.store.write(USER_BLACKLIST, &barred).await.unwrap();

    f.tracking.flag_address_manually("203.0.113.9").await;

    assert_eq!(
        f.store
            .read::<Vec<String>>(USER_BLACKLIST, Vec::new())
            .unwrap(),
        barred
    );
}

#[tokio::test]
async fn nodes_array_is_never_mistaken_for_legacy_flags() {
    let f = Fixture::new();
    f.store
        .write(USER_BLACKLIST, &vec!["123", "456"])
        .await
        .unwrap();

    let flags = f.tracking.load_flags();

    assert!(flags.ips.is_empty());
    assert!(flags.names.is_empty());
    assert!(flags.ids.is_empty());
    // And reading it never marked a perfectly good array as an unreadable dataset.
    assert!(f.store.unreadable().is_empty());
}

#[tokio::test]
async fn flags_left_under_the_old_name_are_still_honoured() {
    let f = Fixture::new();
    f.store
        .write(
            USER_BLACKLIST,
            &flags(&["198.51.100.4"], &["evader"], &["76561198000000000"]),
        )
        .await
        .unwrap();

    let loaded = f.tracking.load_flags();

    assert!(loaded.ips.contains("198.51.100.4"));
    assert!(loaded.names.contains("evader"));
    assert!(loaded.ids.contains("76561198000000000"));
}

#[tokio::test]
async fn the_new_dataset_wins_over_the_old_one() {
    let f = Fixture::new();
    f.store
        .write(USER_BLACKLIST, &flags(&["198.51.100.4"], &[], &[]))
        .await
        .unwrap();
    f.store
        .write(IP_FLAGS, &flags(&["203.0.113.9"], &[], &[]))
        .await
        .unwrap();

    let loaded = f.tracking.load_flags();

    assert!(loaded.ips.contains("203.0.113.9"));
    assert!(!loaded.ips.contains("198.51.100.4"));
}

#[tokio::test]
async fn migrated_flags_are_carried_forward_on_the_next_write() {
    let f = Fixture::new();
    f.store
        .write(USER_BLACKLIST, &flags(&["198.51.100.4"], &[], &[]))
        .await
        .unwrap();

    f.tracking.flag_address_manually("203.0.113.9").await;

    let loaded = f.tracking.load_flags();
    assert!(loaded.ips.contains("198.51.100.4"));
    assert!(loaded.manual_ips.contains("203.0.113.9"));
}

#[tokio::test]
async fn migration_survives_the_seeded_empty_dataset() {
    // Rust-only: a fresh database seeds ip_flags as {}. The C# then read {} as the base for the
    // first write and dropped every migrated flag; here the empty set gives way to the legacy one.
    let f = Fixture::new();
    f.store
        .write(IP_FLAGS, &serde_json::json!({}))
        .await
        .unwrap();
    f.store
        .write(USER_BLACKLIST, &flags(&["198.51.100.4"], &[], &[]))
        .await
        .unwrap();

    f.tracking.flag_address_manually("203.0.113.9").await;

    let stored: FlagSet = f.store.read(IP_FLAGS, FlagSet::default()).unwrap();
    assert!(stored.ips.contains("198.51.100.4"));
    assert!(stored.manual_ips.contains("203.0.113.9"));
}
