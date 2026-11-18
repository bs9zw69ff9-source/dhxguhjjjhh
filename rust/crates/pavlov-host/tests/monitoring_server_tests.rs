//! Port of `MonitoringServerTests.cs`: the metrics and health endpoint's routing, plus a
//! Rust-only test over a real socket.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use pavlov_config::{MonitoringOptions, Secret};
use pavlov_host::health::{HealthRegistry, HealthResult};
use pavlov_host::metrics::{MetricLabels, MetricsRegistry};
use pavlov_host::monitoring_server::{MonitoringServer, Readiness};
use tokio::io::{AsyncReadExt, AsyncWriteExt};

fn options(token: Option<&str>, port: i32) -> MonitoringOptions {
    MonitoringOptions {
        port: Some(port),
        host: "127.0.0.1".into(),
        token: token.map(Secret::new),
    }
}

fn health() -> Arc<HealthRegistry> {
    Arc::new(HealthRegistry::new(Duration::from_secs(3), Duration::ZERO))
}

fn build(
    token: Option<&str>,
    health: Option<Arc<HealthRegistry>>,
    metrics: Option<Arc<MetricsRegistry>>,
    readiness: Option<Readiness>,
) -> MonitoringServer {
    MonitoringServer::new(
        options(token, 9109),
        metrics.unwrap_or_default(),
        health.unwrap_or_else(self::health),
        readiness,
    )
}

fn plain() -> MonitoringServer {
    build(None, None, None, None)
}

fn with_check(result: HealthResult) -> Arc<HealthRegistry> {
    let registry = health();
    registry.register(
        "thing",
        true,
        Arc::new(move || {
            let r = result.clone();
            Box::pin(async move { r })
        }),
    );
    registry
}

#[tokio::test]
async fn metrics_are_served_in_prometheus_format() {
    let metrics = Arc::new(MetricsRegistry::default());
    metrics.increment(
        "commands_total",
        &MetricLabels::none(),
        1.0,
        Some("Commands run"),
    );

    let response = build(None, None, Some(metrics), None)
        .route("GET", "/metrics", None)
        .await;

    assert_eq!(response.status, 200);
    assert!(response.content_type.contains("version=0.0.4"));
    assert!(
        response.body.contains("bot_commands_total 1"),
        "{}",
        response.body
    );
}

#[tokio::test]
async fn only_unhealthy_returns503() {
    // Degraded must not take a bot out of a load balancer.
    let degraded = build(
        None,
        Some(with_check(HealthResult::degraded("slow"))),
        None,
        None,
    );
    assert_eq!(degraded.route("GET", "/health", None).await.status, 200);

    let failing = build(
        None,
        Some(with_check(HealthResult::unhealthy("down"))),
        None,
        None,
    );
    assert_eq!(failing.route("GET", "/health", None).await.status, 503);
}

#[tokio::test]
async fn the_health_body_is_valid_json_with_the_component_detail() {
    let registry = health();
    registry.register(
        "rcon",
        true,
        Arc::new(|| Box::pin(async { HealthResult::degraded("server2: connection refused") })),
    );

    let response = build(None, Some(registry), None, None)
        .route("GET", "/health", None)
        .await;
    let json: serde_json::Value = serde_json::from_str(&response.body).unwrap();

    assert_eq!(json["status"], "degraded");
    assert_eq!(
        json["components"]["rcon"]["detail"],
        "server2: connection refused"
    );
}

#[tokio::test]
async fn a_detail_containing_quotes_does_not_break_the_json() {
    let server = build(
        None,
        Some(with_check(HealthResult::unhealthy(
            "could not open \"C:\\a\\b\"\nretrying",
        ))),
        None,
        None,
    );

    let response = server.route("GET", "/health", None).await;
    let _: serde_json::Value = serde_json::from_str(&response.body).unwrap(); // fails if malformed
    assert_eq!(response.status, 503);
}

#[tokio::test]
async fn readiness_reflects_the_gateway() {
    let ready = build(None, None, None, Some(Arc::new(|| true)));
    assert_eq!(ready.route("GET", "/ready", None).await.status, 200);
    let not = build(None, None, None, Some(Arc::new(|| false)));
    assert_eq!(not.route("GET", "/ready", None).await.status, 503);
}

#[tokio::test]
async fn a_token_guards_everything_except_liveness() {
    let server = build(Some("secret"), None, None, None);

    assert_eq!(server.route("GET", "/metrics", None).await.status, 401);
    assert_eq!(
        server
            .route("GET", "/health", Some("Bearer wrong"))
            .await
            .status,
        401
    );
    assert_eq!(
        server
            .route("GET", "/metrics", Some("Bearer secret"))
            .await
            .status,
        200
    );
    // Liveness stays OPEN: a bot with a broken token must still be restartable.
    assert_eq!(server.route("GET", "/healthz", None).await.status, 200);
}

#[tokio::test]
async fn query_strings_do_not_defeat_routing() {
    assert_eq!(plain().route("GET", "/metrics?x=1", None).await.status, 200);
}

#[tokio::test]
async fn non_read_methods_are_refused() {
    assert_eq!(plain().route("POST", "/metrics", None).await.status, 405);
    assert_eq!(plain().route("DELETE", "/health", None).await.status, 405);
    assert_eq!(plain().route("HEAD", "/healthz", None).await.status, 200);
}

#[tokio::test]
async fn unknown_paths_give_a_bare404() {
    let response = plain().route("GET", "/admin", None).await;
    assert_eq!(response.status, 404);
    assert_eq!(response.body, "");
}

async fn get(port: u16, request: &str) -> String {
    let mut stream = tokio::net::TcpStream::connect(("127.0.0.1", port))
        .await
        .unwrap();
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut reply = String::new();
    stream.read_to_string(&mut reply).await.unwrap();
    reply
}

#[tokio::test]
async fn the_listener_serves_over_a_real_socket() {
    // Rust-only: the hand-written HTTP/1.1 reader against real requests.
    let port = std::net::TcpListener::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let server = Arc::new(MonitoringServer::new(
        options(Some("secret"), i32::from(port)),
        Arc::default(),
        health(),
        None,
    ));
    server.start().await;

    let live = get(port, "GET /healthz HTTP/1.1\r\nHost: x\r\n\r\n").await;
    assert!(live.starts_with("HTTP/1.1 200 OK\r\n"), "{live}");
    assert!(live.ends_with("\r\n\r\nok"));

    let refused = get(port, "GET /metrics HTTP/1.1\r\n\r\n").await;
    assert!(refused.starts_with("HTTP/1.1 401"), "{refused}");

    let allowed = get(
        port,
        "GET /metrics HTTP/1.1\r\nauthorization: Bearer secret\r\n\r\n",
    )
    .await;
    assert!(allowed.starts_with("HTTP/1.1 200"), "{allowed}");
    assert!(allowed.contains("Cache-Control: no-store"));

    let head = get(port, "HEAD /healthz HTTP/1.1\r\n\r\n").await;
    assert!(
        head.ends_with("\r\n\r\n"),
        "a HEAD reply has no body: {head}"
    );

    let oversized = get(
        port,
        &format!("GET /{} HTTP/1.1\r\n\r\n", "a".repeat(10_000)),
    )
    .await;
    assert!(oversized.starts_with("HTTP/1.1 400"), "{oversized}");

    server.stop().await;
}
