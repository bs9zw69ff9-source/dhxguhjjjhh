//! Port of `RconFailureTests.cs`: what each server said, on the reply.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::Duration;

use pavlov_config::Secret;
use pavlov_core::embed::Embed;
use pavlov_discord::rcon_failure::explain;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::RconOptions;

fn registry(servers: &[&str]) -> RconRegistry {
    let options: Vec<RconOptions> = servers
        .iter()
        .map(|s| RconOptions::new(*s, "127.0.0.1", 1, Secret::new("x")))
        .collect();
    RconRegistry::with_system_clock(
        &options,
        Duration::from_secs(60),
        std::sync::Arc::new(MetricsRegistry::default()),
    )
}

fn render(embed: &Embed) -> String {
    format!(
        "{}\n{}\n{}",
        embed.title.clone().unwrap_or_default(),
        embed.description.clone().unwrap_or_default(),
        embed
            .fields
            .iter()
            .map(|f| format!("{}: {}", f.name, f.value))
            .collect::<Vec<_>>()
            .join("\n")
    )
}

fn attempt(server: &str, problem: Option<&str>) -> (String, Option<String>) {
    (server.to_owned(), problem.map(str::to_owned))
}

#[test]
fn the_reason_each_server_gave_is_on_the_reply() {
    let text = render(&explain(
        "Not granted",
        "Nothing was recorded.",
        &registry(&["server1", "server2"]),
        &[
            attempt("server1", Some("Connection refused (127.0.0.1:9102)")),
            attempt("server2", Some("Authentication failed")),
        ],
    ));
    for part in [
        "server1",
        "Connection refused (127.0.0.1:9102)",
        "server2",
        "Authentication failed",
    ] {
        assert!(text.contains(part), "{text}");
    }
}

#[test]
fn a_refusal_with_no_error_says_that_rather_than_nothing() {
    let text = render(&explain(
        "Not granted",
        "Nothing was recorded.",
        &registry(&["server1"]),
        &[attempt("server1", None)],
    ));
    assert!(text.contains("without an error"), "{text}");
}

#[test]
fn no_servers_configured_is_its_own_answer() {
    let text = render(&explain(
        "Not granted",
        "Nothing was recorded.",
        &registry(&[]),
        &[],
    ));
    assert!(text.contains("No RCON servers are configured"));
    assert!(text.contains("RCON_HOST_1"));
}

#[test]
fn the_common_causes_are_named_and_each_one_is_actionable() {
    let text = render(&explain(
        "Not granted",
        "Nothing was recorded.",
        &registry(&["server1"]),
        &[attempt("server1", Some("Connection refused"))],
    ));
    assert!(text.contains("RCON_PASSWORD"));
    assert!(text.contains("/serverinfo"));
}
