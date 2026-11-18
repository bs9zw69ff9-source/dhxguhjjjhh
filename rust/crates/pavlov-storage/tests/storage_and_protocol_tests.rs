//! Port of `StorageAndProtocolTests.cs`, the `FileKeyValueBackendTests` half (the protocol half
//! is in the RCON crate).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::fs;
use std::sync::Arc;

use futures_util::future::join_all;
use pavlov_core::data::{KeyValueBackend, SerializedStore};
use pavlov_storage::FileKeyValueBackend;

fn backend(dir: &tempfile::TempDir) -> FileKeyValueBackend {
    FileKeyValueBackend::new(dir.path()).unwrap()
}

#[test]
fn a_round_trip_preserves_the_value() {
    let dir = tempfile::tempdir().unwrap();
    let b = backend(&dir);
    b.write("bans", "{\"a\":1}").unwrap();
    assert_eq!(b.read("bans").unwrap().as_deref(), Some("{\"a\":1}"));
}

#[test]
fn an_absent_dataset_reads_as_null_not_an_exception() {
    let dir = tempfile::tempdir().unwrap();
    assert_eq!(backend(&dir).read("never-written").unwrap(), None);
}

#[test]
fn a_write_over_an_existing_dataset_leaves_no_temp_file_behind() {
    let dir = tempfile::tempdir().unwrap();
    let b = backend(&dir);
    b.write("bans", "first").unwrap();
    b.write("bans", "second").unwrap();

    assert_eq!(b.read("bans").unwrap().as_deref(), Some("second"));
    let leftovers = fs::read_dir(dir.path())
        .unwrap()
        .filter(|e| {
            e.as_ref()
                .unwrap()
                .file_name()
                .to_string_lossy()
                .ends_with(".tmp")
        })
        .count();
    assert_eq!(leftovers, 0);
}

#[test]
fn keys_that_could_escape_the_data_directory_are_refused() {
    // Rejected rather than sanitised: a silently-renamed dataset reads back as empty.
    let dir = tempfile::tempdir().unwrap();
    for key in ["../escape", "nested/path", "..", "", "nul\0byte"] {
        let error = backend(&dir).write(key, "x").unwrap_err();
        assert_eq!(error.kind(), std::io::ErrorKind::InvalidInput, "{key:?}");
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn the_store_serialises_concurrent_updates_to_one_dataset() {
    let dir = tempfile::tempdir().unwrap();
    let store = SerializedStore::new(Arc::new(backend(&dir)));

    let updates = (0..50).map(|_| {
        store.update("counter", Vec::<i32>::new, |mut current| {
            current.push(1);
            Some(current)
        })
    });
    for result in join_all(updates).await {
        assert!(result.unwrap().ok);
    }

    assert_eq!(store.read("counter", Vec::<i32>::new()).unwrap().len(), 50);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_corrupt_dataset_reads_as_the_fallback_rather_than_throwing() {
    let dir = tempfile::tempdir().unwrap();
    let b = backend(&dir);
    b.write("bans", "{ this is not json").unwrap();

    let store = SerializedStore::new(Arc::new(b));
    assert!(store.read("bans", Vec::<String>::new()).unwrap().is_empty());

    // But an update is REFUSED rather than written over it.
    let result = store
        .update("bans", Vec::<String>::new, |mut current| {
            current.push("player".into());
            Some(current)
        })
        .await
        .unwrap();
    assert!(!result.ok);
    assert_eq!(
        store.read_raw("bans").unwrap().as_deref(),
        Some("{ this is not json")
    );
}

#[test]
fn a_read_that_fails_is_an_error_not_an_absent_dataset() {
    // The C# read any I/O failure as absent, so the next update wrote the fallback over it.
    let dir = tempfile::tempdir().unwrap();
    fs::create_dir(dir.path().join("bans.json")).unwrap(); // a directory where the file belongs

    assert!(backend(&dir).read("bans").is_err());
}
