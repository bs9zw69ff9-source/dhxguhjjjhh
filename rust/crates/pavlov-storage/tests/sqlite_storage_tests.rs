//! Port of `SqliteStorageTests.cs` (C# class `SqliteKeyValueBackendTests`).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::collections::HashSet;
use std::fs;
use std::sync::Arc;

use futures_util::future::join_all;
use pavlov_core::data::{KeyValueBackend, SerializedStore};
use pavlov_storage::{SqliteKeyValueBackend, datasets};

fn backend(dir: &tempfile::TempDir) -> SqliteKeyValueBackend {
    SqliteKeyValueBackend::open(dir.path().join("bot.db")).unwrap()
}

#[test]
fn a_round_trip_preserves_the_value() {
    let dir = tempfile::tempdir().unwrap();
    let b = backend(&dir);
    b.write("tempbans", r#"[{"playerId":"alice"}]"#).unwrap();
    assert_eq!(
        b.read("tempbans").unwrap().as_deref(),
        Some(r#"[{"playerId":"alice"}]"#)
    );
}

#[test]
fn an_absent_dataset_reads_as_null_which_is_not_the_same_as_empty() {
    let dir = tempfile::tempdir().unwrap();
    let b = backend(&dir);
    assert_eq!(b.read("never_written").unwrap(), None);
    assert!(!b.has("never_written").unwrap());

    b.write("written_empty", "[]").unwrap();
    assert!(b.has("written_empty").unwrap());
}

#[test]
fn data_survives_reopening_the_database() {
    let dir = tempfile::tempdir().unwrap();
    backend(&dir).write("tempbans", "[1,2,3]").unwrap();
    assert_eq!(
        backend(&dir).read("tempbans").unwrap().as_deref(),
        Some("[1,2,3]")
    );
}

#[test]
fn seeding_only_applies_to_an_absent_dataset() {
    let dir = tempfile::tempdir().unwrap();
    let b = backend(&dir);
    assert!(b.seed_if_missing("tempbans", "[]").unwrap());
    b.write("tempbans", r#"["real data"]"#).unwrap();
    assert!(!b.seed_if_missing("tempbans", "[]").unwrap());
    assert_eq!(
        b.read("tempbans").unwrap().as_deref(),
        Some(r#"["real data"]"#)
    );
}

#[test]
fn a_corrupt_legacy_file_is_not_imported() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("legacy.json");
    fs::write(&path, "{ this is not json").unwrap();

    let b = backend(&dir);
    assert!(!b.import_json_file("legacy", &path).unwrap());
    assert_eq!(b.read("legacy").unwrap(), None);
}

#[test]
fn a_valid_legacy_file_is_imported_once() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("legacy.json");
    fs::write(&path, r#"{"a":1}"#).unwrap();

    let b = backend(&dir);
    assert!(b.import_json_file("legacy", &path).unwrap());
    assert_eq!(b.read("legacy").unwrap().as_deref(), Some(r#"{"a":1}"#));

    // The database has moved on and must not be overwritten by a stale file.
    fs::write(&path, r#"{"a":999}"#).unwrap();
    assert!(!b.import_json_file("legacy", &path).unwrap());
    assert_eq!(b.read("legacy").unwrap().as_deref(), Some(r#"{"a":1}"#));
}

#[test]
fn export_writes_every_dataset_as_readable_json() {
    let dir = tempfile::tempdir().unwrap();
    let b = backend(&dir);
    b.write("tempbans", r#"[{"playerId":"alice"}]"#).unwrap();
    b.write("mutes", "{}").unwrap();
    b.write("broken", "{ nope").unwrap();

    let output = dir.path().join("export");
    assert_eq!(b.export_to_json(&output, &["mutes".into()]).unwrap(), 3);
    assert!(
        fs::read_to_string(output.join("tempbans.json"))
            .unwrap()
            .contains('\n')
    );
    // A damaged dataset never replaces its backup: it goes beside it.
    assert!(output.join("broken.json.corrupt").exists());
    assert!(output.join("mutes.json.corrupt").exists());
    assert!(!output.join("broken.json").exists());
    let temps = fs::read_dir(&output)
        .unwrap()
        .filter(|e| {
            e.as_ref()
                .unwrap()
                .file_name()
                .to_string_lossy()
                .ends_with(".tmp")
        })
        .count();
    assert_eq!(temps, 0);
}

#[tokio::test(flavor = "multi_thread")]
async fn the_store_serialises_over_sqlite_too() {
    let dir = tempfile::tempdir().unwrap();
    let store = SerializedStore::new(Arc::new(backend(&dir)));

    let updates = (0..50).map(|_| {
        store.update("counter", Vec::<i32>::new, |mut current| {
            current.push(1);
            Some(current)
        })
    });
    join_all(updates).await;

    assert_eq!(store.read("counter", Vec::<i32>::new()).unwrap().len(), 50);
}

#[test]
fn every_dataset_has_a_seed_and_every_seed_is_valid_json() {
    assert!(datasets::all().count() > 0);
    for name in datasets::all() {
        let seed: serde_json::Value = serde_json::from_str(datasets::seed(name).unwrap()).unwrap();
        assert!(seed.is_array() || seed.is_object(), "{name}");
    }
}

#[test]
fn dataset_names_are_unique_and_file_safe() {
    let mut seen = HashSet::new();
    for name in datasets::all() {
        assert!(seen.insert(name.to_lowercase()), "{name} twice");
        assert!(!name.contains(['/', '\0']), "{name}");
    }
}
