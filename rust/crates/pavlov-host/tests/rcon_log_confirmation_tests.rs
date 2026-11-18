//! Port of `RconLogConfirmationTests.cs`: for a state-changing command, whichever comes first
//! - the RCON reply or the command in that server's Pavlov.log - is the answer.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::{Duration, Instant};

use pavlov_config::{RconOptions, Secret};
use pavlov_host::confirmations::RconConfirmations;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::{LOG_CONFIRM_WINDOW, LogFor, RconRegistry};
use pavlov_host::server_labels::log_for;
use pavlov_rcon::testing::FakeRconServer;
use pavlov_rcon::{RconError, reply};

const LOG1: &str = "/srv/pavlov1/Pavlov/Saved/Logs/Pavlov.log";
const LOG2: &str = "/srv/pavlov2/Pavlov/Saved/Logs/Pavlov.log";

fn logs(paths: &[&str]) -> LogFor {
    let paths: Vec<String> = paths.iter().map(|p| (*p).to_owned()).collect();
    Arc::new(move |name: &str| log_for(&paths, name))
}

fn registry(
    server: &FakeRconServer,
    confirmations: &Arc<RconConfirmations>,
    timeout: Duration,
) -> RconRegistry {
    let mut o = server.options();
    o.command_timeout = timeout;
    o.read_cache_duration = Duration::ZERO;
    o.max_attempts = 1;
    let r = RconRegistry::with_system_clock(
        &[o],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    );
    r.use_log_confirmation(Arc::clone(confirmations), logs(&[LOG1, LOG2]));
    r
}

/// The log tail noting the command once the server has received it.
fn echo_when_received(
    server: &Arc<FakeRconServer>,
    confirmations: &Arc<RconConfirmations>,
    command: &'static str,
    log: &'static str,
) -> tokio::task::JoinHandle<()> {
    let server = Arc::clone(server);
    let confirmations = Arc::clone(confirmations);
    tokio::spawn(async move {
        for _ in 0..200 {
            if server.commands().iter().any(|c| c == command) {
                break;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        confirmations.note(command, Some(log));
    })
}

async fn fake() -> Arc<FakeRconServer> {
    Arc::new(FakeRconServer::start().await.unwrap())
}

#[tokio::test(flavor = "multi_thread")]
async fn a_logged_command_is_answered_before_a_slow_reply() {
    let server = fake().await;
    server.configure(|b| b.reply_delay = Duration::from_secs(2));
    let confirmations = Arc::new(RconConfirmations::default());
    let r = registry(&server, &confirmations, Duration::from_secs(5));

    let watch = Instant::now();
    let echo = echo_when_received(&server, &confirmations, "Kick Griefer", LOG1);
    let reply = r.send_verified("server1", "Kick Griefer").await.unwrap();
    echo.await.unwrap();

    assert!(
        watch.elapsed() < Duration::from_millis(1500),
        "took {:?}",
        watch.elapsed()
    );
    assert!(reply.contains("Pavlov.log"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_command_whose_reply_never_comes_is_confirmed_by_the_log() {
    let server = fake().await;
    server.configure(|b| {
        b.swallow.insert("Notify".into());
    });
    let confirmations = Arc::new(RconConfirmations::default());
    let r = registry(&server, &confirmations, Duration::from_millis(300));

    let echo = echo_when_received(&server, &confirmations, "Notify All hello", LOG1);
    let raw = r
        .send_verified("server1", "Notify All hello")
        .await
        .unwrap();
    echo.await.unwrap();

    assert_eq!(
        reply::successful(&reply::try_parse(Some(&raw)).unwrap()),
        Some(true)
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn with_no_log_line_the_rcon_failure_stands() {
    let server = fake().await;
    server.configure(|b| {
        b.swallow.insert("Kick".into());
    });
    let r = registry(
        &server,
        &Arc::new(RconConfirmations::default()),
        Duration::from_millis(300),
    );

    let error = r.send("server1", "Kick Nobody").await.unwrap_err();
    assert!(matches!(error, RconError::Unconfirmed { .. }), "{error:?}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_refusal_that_arrives_first_is_still_a_refusal() {
    let server = fake().await;
    server.configure(|b| b.refuse_everything = true);
    let r = registry(
        &server,
        &Arc::new(RconConfirmations::default()),
        Duration::from_secs(2),
    );

    let error = r.send_verified("server1", "Ban Griefer").await.unwrap_err();
    assert!(matches!(error, RconError::Rejected { .. }), "{error:?}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_command_seen_only_in_another_servers_log_does_not_count() {
    let server = fake().await;
    server.configure(|b| {
        b.swallow.insert("Kick".into());
    });
    let confirmations = Arc::new(RconConfirmations::default());
    let r = registry(&server, &confirmations, Duration::from_millis(300));

    let echo = echo_when_received(&server, &confirmations, "Kick Griefer", LOG2);
    let error = r.send("server1", "Kick Griefer").await.unwrap_err();
    echo.await.unwrap();

    assert!(matches!(error, RconError::Unconfirmed { .. }), "{error:?}");
}

#[tokio::test(flavor = "multi_thread")]
async fn an_unreachable_server_fails_without_waiting_for_the_log() {
    let port = {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        listener.local_addr().unwrap().port()
    };
    let mut o = RconOptions::new("server1", "127.0.0.1", i32::from(port), Secret::new("x"));
    o.max_attempts = 1;
    let r = RconRegistry::with_system_clock(
        &[o],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    );
    r.use_log_confirmation(Arc::new(RconConfirmations::default()), logs(&[LOG1]));

    let watch = Instant::now();
    assert!(r.send("server1", "Kick Griefer").await.is_err());
    assert!(
        watch.elapsed() < LOG_CONFIRM_WINDOW / 2,
        "took {:?}",
        watch.elapsed()
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_read_is_never_answered_from_the_log() {
    let server = fake().await;
    let confirmations = Arc::new(RconConfirmations::default());
    confirmations.note("RefreshList", Some(LOG1));
    let r = registry(&server, &confirmations, Duration::from_secs(2));

    let reply = r.send("server1", "RefreshList").await.unwrap();
    assert!(reply.contains("PlayerList"));
}

#[test]
fn an_rcon_server_maps_to_the_log_with_its_number() {
    let paths = vec![LOG1.to_owned(), LOG2.to_owned()];
    assert_eq!(log_for(&paths, "server1").as_deref(), Some(LOG1));
    assert_eq!(log_for(&paths, "server2").as_deref(), Some(LOG2));
    assert_eq!(log_for(&paths, "server3"), None);
    assert_eq!(log_for(&paths, "Server 1"), None);
}
