//! The server-layout gate (C# `ServerLayoutGate`, which had no tests of its own): one change at
//! a time, and none at all once `.env` changed under a bot that has not restarted onto it.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_host::layout_gate::ServerLayoutGate;
use pavlov_host::provisioner::{ProvisionOutcome, ProvisionStatus, ProvisionStep};

fn outcome(env_step: ProvisionStatus) -> ProvisionOutcome {
    ProvisionOutcome {
        steps: vec![ProvisionStep {
            name: "Wire into the bot (.env)".into(),
            status: env_step,
            detail: String::new(),
        }],
        restart_queued: false,
    }
}

#[test]
fn a_second_change_waits_for_the_first() {
    let gate = ServerLayoutGate::new();
    assert_eq!(gate.try_enter("/provisionserver"), None);

    let refused = gate.try_enter("/deleteserver").unwrap();
    assert!(
        refused.contains("/provisionserver is still running"),
        "{refused}"
    );
}

#[test]
fn a_change_that_touched_env_shuts_the_gate_until_the_restart() {
    let gate = ServerLayoutGate::new();
    assert_eq!(gate.try_enter("/provisionserver"), None);
    gate.exit(Some(&outcome(ProvisionStatus::Ok)));

    let refused = gate.try_enter("/deleteserver").unwrap();
    assert!(
        refused.contains("has not restarted onto it yet"),
        "{refused}"
    );
}

#[test]
fn a_change_that_failed_before_env_reopens_the_gate() {
    let gate = ServerLayoutGate::new();
    assert_eq!(gate.try_enter("/provisionserver"), None);
    gate.exit(Some(&outcome(ProvisionStatus::Skipped)));

    assert_eq!(gate.try_enter("/provisionserver"), None);
}

#[test]
fn a_run_that_died_counts_as_having_changed_the_layout() {
    // No outcome: nobody knows how far it got, so the disk may not match memory.
    let gate = ServerLayoutGate::new();
    assert_eq!(gate.try_enter("/deleteserver"), None);
    gate.exit(None);

    assert!(gate.try_enter("/provisionserver").is_some());
}

#[test]
fn abandoning_reopens_without_a_reload() {
    let gate = ServerLayoutGate::new();
    assert_eq!(gate.try_enter("/provisionserver"), None);
    gate.abandon();

    assert_eq!(gate.try_enter("/provisionserver"), None);
}
