//! Port of `RosterProblemExcerptTests.cs`: how a roster sweep's unparseable reply is quoted in
//! the problem the boards and `/players` show.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_host::registry::excerpt;

#[test]
fn the_reply_is_quoted_so_the_cause_is_in_the_message() {
    assert_eq!(
        excerpt(Some("Authentication required")),
        "\"Authentication required\""
    );
}

#[test]
fn an_empty_reply_is_said_rather_than_quoted_as_nothing() {
    assert_eq!(excerpt(Some("")), "it was empty");
    assert_eq!(excerpt(Some("   \r\n ")), "it was empty");
    assert_eq!(excerpt(None), "it was empty");
}

#[test]
fn backticks_cannot_break_out_of_the_code_span_it_lands_in() {
    assert!(!excerpt(Some("nope ``` **not really**")).contains('`'));
}

#[test]
fn a_very_long_reply_is_truncated_rather_than_pasted_whole() {
    let long = "x".repeat(5000);
    let excerpt = excerpt(Some(&long));
    assert!(
        excerpt.chars().count() < 250,
        "excerpt was {}",
        excerpt.chars().count()
    );
    assert!(excerpt.ends_with("...\""));
}

#[test]
fn the_head_is_kept_because_that_is_where_the_cause_is() {
    let reply = format!("Error: unknown command RefreshList{}", ".".repeat(500));
    assert!(excerpt(Some(&reply)).starts_with("\"Error: unknown command"));
}
