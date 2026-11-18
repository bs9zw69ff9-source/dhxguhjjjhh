//! Port of `StoredShapeTests.cs`: stored rows written before a field existed - or by the Node
//! bot, which shares the database - come back with the field ABSENT or `null`, and every one of
//! those used to be a NullReferenceException on the login path.
//!
//! In Rust a `Vec` cannot be null, so the C# "built from nulls" and "with-expression" cases are
//! guaranteed by the type; they are ported as the equivalent claim - the empty value is empty -
//! and the deserialisation cases, which are the real risk, are asserted as written.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::evasion::{AccountRecord, FlagSet};
use pavlov_core::text::case::IgnoreCaseMap;

fn store(key: &str, json: &str) -> SerializedStore {
    let backend = Arc::new(MemoryBackend::new());
    backend.seed(key, json);
    SerializedStore::new(backend)
}

fn account(json: &str) -> AccountRecord {
    let accounts: IgnoreCaseMap<AccountRecord> = store("known_players", json)
        .read("known_players", IgnoreCaseMap::new())
        .unwrap();
    accounts.get("ABC").unwrap().clone()
}

// ---- the shapes that used to throw ----

#[test]
fn an_account_row_missing_its_arrays_reads_as_empty_not_null() {
    let account = account(r#"{"abc":{"id":"abc"}}"#);

    assert!(account.names.is_empty());
    assert!(account.confirmed_ips.is_empty());
    assert!(account.guessed_ips.is_empty());
    assert_eq!(account.name(), None);
}

#[test]
fn an_account_row_with_explicit_nulls_reads_as_empty() {
    let account =
        account(r#"{"abc":{"id":"abc","names":null,"confirmedIps":null,"guessedIps":null}}"#);

    assert!(account.names.is_empty());
    assert!(account.confirmed_ips.is_empty());
    assert!(account.guessed_ips.is_empty());
}

#[test]
fn the_exact_calls_record_async_makes_do_not_throw() {
    let existing = account(r#"{"abc":{"id":"abc"}}"#);

    let names = existing.names.clone();
    let confirmed = existing.confirmed_ips.clone();
    let guessed = existing.guessed_ips.clone();

    assert!(names.is_empty() && confirmed.is_empty() && guessed.is_empty());
}

#[test]
fn flags_missing_their_arrays_read_as_empty_not_null() {
    let flags: FlagSet = store("ip_flags", r#"{"ips":["1.2.3.4"]}"#)
        .read("ip_flags", FlagSet::default())
        .unwrap();

    assert_eq!(flags.ips.iter().collect::<Vec<_>>(), ["1.2.3.4"]);
    assert!(flags.names.is_empty());
    assert!(flags.ids.is_empty());
    assert!(flags.manual_ips.is_empty());
}

#[test]
fn a_flag_set_built_from_nulls_is_empty_not_null() {
    let flags: FlagSet =
        serde_json::from_str(r#"{"ips":null,"names":null,"ids":null,"manualIps":null}"#).unwrap();
    assert!(flags.ips.is_empty());
    assert!(flags.names.is_empty());
    assert!(flags.ids.is_empty());
    assert!(flags.manual_ips.is_empty());
}

#[test]
fn an_account_record_built_from_nulls_is_empty_not_null() {
    let account: AccountRecord =
        serde_json::from_str(r#"{"id":null,"names":null,"confirmedIps":null,"guessedIps":null}"#)
            .unwrap();
    assert!(account.names.is_empty());
    assert!(account.confirmed_ips.is_empty());
    assert!(account.guessed_ips.is_empty());
    assert_eq!(account.id, "");
}

#[test]
fn with_expressions_keep_the_guard() {
    // The C# `with` copy could reintroduce a null; a Rust struct update cannot.
    let account = AccountRecord {
        names: Vec::new(),
        confirmed_ips: Vec::new(),
        ..AccountRecord::empty("abc")
    };
    assert!(account.names.is_empty());
    assert!(account.confirmed_ips.is_empty());
    assert_eq!(account.id, "abc");
}
