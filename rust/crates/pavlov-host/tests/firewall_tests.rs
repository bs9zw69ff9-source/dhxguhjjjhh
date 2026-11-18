//! Port of `FirewallTests.cs` (`FirewallArgvTests`): a deny must go IN FRONT of the port
//! allow rules, or ufw's first-match evaluation lets the "blocked" address straight through.
//! Plus the deny/undeny flows against a recording runner, which the C# could not drive.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;

use common::{FakeRunner, exited};
use pavlov_host::firewall::{
    Firewall, UfwFirewall, append_deny_argv, delete_deny_argv, insert_deny_argv, invocation,
    sudoers_advice,
};

fn strings(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| (*s).to_owned()).collect()
}

#[test]
fn as_root_ufw_is_called_directly() {
    let (file, argv) = invocation(&strings(&["status", "numbered"]), true);
    assert_eq!(file, "ufw");
    assert_eq!(argv, strings(&["status", "numbered"]));
}

#[test]
fn unprivileged_ufw_goes_through_non_interactive_sudo() {
    // -n makes a missing grant fail instead of hang.
    let (file, argv) = invocation(&strings(&["insert", "1", "deny", "from", "1.2.3.4"]), false);
    assert_eq!(file, "sudo");
    assert_eq!(
        argv,
        strings(&["-n", "ufw", "insert", "1", "deny", "from", "1.2.3.4"])
    );
}

#[test]
fn the_sudo_advice_names_only_ufw() {
    assert!(sudoers_advice("pavlov").contains("pavlov ALL=(root) NOPASSWD: /usr/sbin/ufw"));
}

#[test]
fn a_deny_is_inserted_at_rule_one_not_appended() {
    // A bare `deny from` lands after `allow <gameport>` and never fires.
    assert_eq!(
        insert_deny_argv("1.2.3.4"),
        strings(&["insert", "1", "deny", "from", "1.2.3.4"])
    );
}

#[test]
fn the_empty_ruleset_fallback_appends() {
    assert_eq!(
        append_deny_argv("1.2.3.4"),
        strings(&["deny", "from", "1.2.3.4"])
    );
}

#[test]
fn a_delete_matches_by_rule_spec_not_position() {
    assert_eq!(
        delete_deny_argv("1.2.3.4"),
        strings(&["delete", "deny", "from", "1.2.3.4"])
    );
}

// ---- the flows (Rust-only) ----

fn firewall(runner: &Arc<FakeRunner>, privileged: bool) -> UfwFirewall {
    UfwFirewall::with(Arc::clone(runner) as _, privileged, "pavlov")
}

#[tokio::test]
async fn a_deny_deletes_any_old_rule_then_inserts_at_the_top() {
    let runner = Arc::new(FakeRunner::default());

    let result = firewall(&runner, true).deny(" 203.0.113.9 ").await;

    assert!(result.ok);
    assert_eq!(
        runner.calls(),
        vec![
            (
                "ufw".to_owned(),
                strings(&["delete", "deny", "from", "203.0.113.9"])
            ),
            (
                "ufw".to_owned(),
                strings(&["insert", "1", "deny", "from", "203.0.113.9"])
            ),
        ]
    );
}

#[tokio::test]
async fn an_empty_ruleset_falls_back_to_appending() {
    let runner = Arc::new(FakeRunner::default());
    runner.answer(
        "ufw",
        &["insert"],
        exited(1, "ERROR: Invalid position '1'", ""),
    );

    let result = firewall(&runner, true).deny("203.0.113.9").await;

    assert!(result.ok);
    assert_eq!(
        runner.calls().last().unwrap().1,
        strings(&["deny", "from", "203.0.113.9"])
    );
}

#[tokio::test]
async fn an_address_is_canonicalised_and_garbage_runs_nothing() {
    let runner = Arc::new(FakeRunner::default());
    let fw = firewall(&runner, true);

    let _ = fw.undeny("2001:0db8:0000:0000:0000:0000:0000:0001").await;
    assert_eq!(
        runner.calls()[0].1,
        strings(&["delete", "deny", "from", "2001:db8::1"])
    );

    for bad in ["", "1.2.3", "1.2.3.4; reboot", "not-an-ip"] {
        let result = fw.deny(bad).await;
        assert!(!result.ok);
        assert!(
            result.detail.starts_with("not an address"),
            "{bad}: {}",
            result.detail
        );
    }
    assert_eq!(runner.calls().len(), 1);
}

#[tokio::test]
async fn a_refused_sudo_says_how_to_grant_exactly_ufw() {
    let runner = Arc::new(FakeRunner::default());
    runner.answer("sudo", &[], exited(1, "", "sudo: a password is required"));

    let result = firewall(&runner, false).status().await;

    assert!(!result.ok);
    assert!(
        result
            .detail
            .contains("pavlov ALL=(root) NOPASSWD: /usr/sbin/ufw"),
        "{}",
        result.detail
    );
    assert_eq!(
        runner.calls()[0],
        (
            "sudo".to_owned(),
            strings(&["-n", "ufw", "status", "numbered"])
        )
    );
}
