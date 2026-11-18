//! Port of `DebugPassRegressionTests.cs`: three bugs a debugging pass found, pinned so they
//! stay fixed. A login hang-up read as a bad password; two layout changes overlapping; and a
//! damaged dataset overwriting its last good export.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use pavlov_config::{RconOptions, Secret};
use pavlov_core::data::KeyValueBackend;
use pavlov_host::layout_gate::ServerLayoutGate;
use pavlov_host::provisioner::{ProvisionOutcome, ProvisionStatus, ProvisionStep};
use pavlov_rcon::{RconClient, RconError};
use pavlov_storage::SqliteKeyValueBackend;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

// ---- a hang-up during login is a transport failure, not a bad password ----

#[tokio::test]
async fn a_server_that_hangs_up_during_login_is_retried_not_reported_as_a_bad_password() {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = listener.local_addr().unwrap().port();
    let connections = Arc::new(AtomicUsize::new(0));
    let counted = Arc::clone(&connections);
    let serve = tokio::spawn(async move {
        loop {
            let Ok((mut socket, _)) = listener.accept().await else {
                return;
            };
            counted.fetch_add(1, Ordering::SeqCst);
            socket.write_all(b"Password: ").await.unwrap();
            let mut hex = [0u8; 32];
            let _ = socket.read_exact(&mut hex).await;
            // ...and hang up without a word.
        }
    });

    let mut options = RconOptions::new("test", "127.0.0.1", i32::from(port), Secret::new("x"));
    options.command_timeout = Duration::from_secs(2);
    options.max_attempts = 2;
    options.read_cache_duration = Duration::ZERO;
    let rcon = RconClient::new(options);

    let failure = rcon.send("ServerInfo").await.unwrap_err();

    assert!(!matches!(failure, RconError::Auth(_)), "{failure:?}");
    assert!(
        connections.load(Ordering::SeqCst) >= 2,
        "a transport failure is retried; a bad password is not"
    );
    serve.abort();
}

// ---- the layout gate ----

fn outcome(steps: &[(&str, ProvisionStatus)]) -> ProvisionOutcome {
    ProvisionOutcome {
        steps: steps
            .iter()
            .map(|(name, status)| ProvisionStep {
                name: (*name).to_owned(),
                status: *status,
                detail: String::new(),
            })
            .collect(),
        restart_queued: false,
    }
}

#[test]
fn a_second_layout_change_waits_for_the_first() {
    let gate = ServerLayoutGate::new();
    assert!(gate.try_enter("provisioning a server").is_none());
    assert!(
        gate.try_enter("deleting server 3")
            .unwrap()
            .contains("provisioning a server")
    );
}

#[test]
fn after_env_changes_nothing_else_runs_until_a_restart() {
    let gate = ServerLayoutGate::new();
    gate.try_enter("provisioning a server");
    gate.exit(Some(&outcome(&[
        ("Pre-flight checks", ProvisionStatus::Ok),
        ("Wire into the bot (.env)", ProvisionStatus::Ok),
    ])));
    assert!(
        gate.try_enter("provisioning a server")
            .unwrap()
            .to_lowercase()
            .contains("restart")
    );
}

#[test]
fn a_run_that_failed_before_env_leaves_the_gate_open() {
    let gate = ServerLayoutGate::new();
    gate.try_enter("provisioning a server");
    gate.exit(Some(&outcome(&[
        ("SteamCMD install", ProvisionStatus::Failed),
        ("Wire into the bot (.env)", ProvisionStatus::Skipped),
    ])));
    assert!(gate.try_enter("provisioning a server").is_none());
}

#[test]
fn a_run_that_threw_is_treated_as_a_change() {
    let gate = ServerLayoutGate::new();
    gate.try_enter("deleting server 2");
    gate.exit(None);
    assert!(gate.try_enter("deleting server 1").is_some());
}

#[test]
fn a_refused_run_releases_the_gate() {
    let gate = ServerLayoutGate::new();
    gate.try_enter("deleting server 2");
    gate.abandon();
    assert!(gate.try_enter("deleting server 2").is_none());
}

// ---- the JSON export ----

#[test]
fn a_damaged_dataset_does_not_overwrite_its_last_good_export() {
    let dir = tempfile::tempdir().unwrap();
    let backend = SqliteKeyValueBackend::open(dir.path().join("bot.db")).unwrap();
    let output = dir.path().join("export");

    backend
        .write("tempbans", r#"[{"playerId":"alice"}]"#)
        .unwrap();
    backend.export_to_json(&output, &[]).unwrap();
    backend.write("tempbans", r#"[{"playerId":"al"#).unwrap(); // truncated
    backend.export_to_json(&output, &[]).unwrap();

    assert!(
        std::fs::read_to_string(output.join("tempbans.json"))
            .unwrap()
            .contains("alice")
    );
    assert_eq!(
        std::fs::read_to_string(output.join("tempbans.json.corrupt")).unwrap(),
        r#"[{"playerId":"al"#
    );
}

#[test]
fn a_dataset_the_store_cannot_read_is_exported_aside_even_when_it_is_valid_json() {
    let dir = tempfile::tempdir().unwrap();
    let backend = SqliteKeyValueBackend::open(dir.path().join("bot.db")).unwrap();
    let output = dir.path().join("export");

    backend.write("roles", r#"{"adminRole":1}"#).unwrap();
    backend.export_to_json(&output, &[]).unwrap();
    backend.write("roles", "\"the wrong shape\"").unwrap();
    backend
        .export_to_json(&output, &["roles".to_owned()])
        .unwrap();

    assert!(
        std::fs::read_to_string(output.join("roles.json"))
            .unwrap()
            .contains("adminRole")
    );
    assert!(output.join("roles.json.corrupt").exists());
}
