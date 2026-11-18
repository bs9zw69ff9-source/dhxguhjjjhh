//! Port of `RecentErrorsTests.cs`: a failure found again by the id the user was shown.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_host::recent_errors::{CAPACITY, RecentErrors};

#[derive(Debug)]
struct InvalidOperation(String);

impl std::fmt::Display for InvalidOperation {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for InvalidOperation {}

fn thrown(message: &str) -> InvalidOperation {
    InvalidOperation(message.into())
}

#[test]
fn an_error_is_found_by_the_id_the_caller_was_shown() {
    let errors = RecentErrors::new();
    errors.record("ada985e1", "/counts", 42, &thrown("boom"));

    let found = errors.find(Some("ada985e1")).unwrap();

    assert_eq!(found.operation, "/counts");
    assert_eq!(found.kind, "InvalidOperation");
    assert_eq!(found.message, "boom");
    // Where it was recorded, by function, in the trace.
    assert!(
        found.trace.contains("recent_errors_tests"),
        "{}",
        found.trace
    );
}

#[test]
fn the_lookup_is_forgiving_about_how_the_id_was_typed() {
    for typed in ["ADA985E1", "  ada985e1  "] {
        let errors = RecentErrors::new();
        errors.record("ada985e1", "/counts", 42, &thrown("boom"));
        assert!(errors.find(Some(typed)).is_some(), "{typed}");
    }
}

#[test]
fn an_unknown_id_is_null_rather_than_an_error() {
    let errors = RecentErrors::new();
    errors.record("aaaaaaaa", "/counts", 42, &thrown("boom"));

    assert!(errors.find(Some("bbbbbbbb")).is_none());
    assert!(errors.find(None).is_none());
    assert!(errors.find(Some("   ")).is_none());
}

#[test]
fn the_newest_are_listed_first() {
    let errors = RecentErrors::new();
    errors.record("first", "/a", 1, &thrown("1"));
    errors.record("second", "/b", 1, &thrown("2"));

    assert_eq!(errors.all()[0].correlation_id, "second");
}

#[test]
fn old_errors_age_out_and_recent_ones_survive() {
    let errors = RecentErrors::new();
    for i in 0..CAPACITY + 10 {
        errors.record(&format!("id{i}"), "/a", 1, &thrown(&i.to_string()));
    }

    assert_eq!(errors.all().len(), CAPACITY);
    assert!(errors.find(Some("id0")).is_none());
    assert!(errors.find(Some(&format!("id{}", CAPACITY + 9))).is_some());
}

#[test]
fn concurrent_recording_stays_within_the_cap() {
    let errors = Arc::new(RecentErrors::new());
    let threads: Vec<_> = (0..200)
        .map(|i| {
            let errors = Arc::clone(&errors);
            std::thread::spawn(move || errors.record(&format!("id{i}"), "/a", 1, &thrown("x")))
        })
        .collect();
    for thread in threads {
        thread.join().unwrap();
    }

    assert_eq!(errors.all().len(), CAPACITY);
}
