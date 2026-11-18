//! The loopback metrics and health endpoint (C# `Observability/MonitoringServer.cs`).
//!
//! - OFF unless `METRICS_PORT` is set, and bound to loopback unless told otherwise: these
//!   pages carry service names, error messages and internal state.
//! - Read-only: anything but GET and HEAD is 405; an unknown path is a bare 404.
//! - A token, when set, guards everything EXCEPT `/healthz`: an orchestrator has to be able to
//!   restart a bot whose configuration (a wrong token, say) is broken. Compared in constant time.
//! - `/health` is 503 only when UNHEALTHY. Degraded must not pull a bot out of a load balancer.
//! - A port that will not bind is logged and the bot carries on without it.
//!
//! The listener is a small HTTP/1.1 reader, not a framework: one request per connection, a
//! bounded head (8 KiB, 5 s), at most 16 in flight, `Connection: close`. That is everything
//! a scrape or an uptime check needs, with nothing else to attack.

use std::fmt::Write as _;
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use pavlov_config::MonitoringOptions;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::{Semaphore, watch};
use tokio::task::JoinHandle;

use crate::health::{HealthRegistry, HealthReport, HealthStatus};
use crate::metrics::MetricsRegistry;

const MAX_CONCURRENT_REQUESTS: usize = 16;
const MAX_HEAD_BYTES: usize = 8 * 1024;
const HEAD_TIMEOUT: Duration = Duration::from_secs(5);
const TEXT: &str = "text/plain; charset=utf-8";
const JSON: &str = "application/json";

/// What a route answers.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MonitoringResponse {
    pub status: u16,
    pub body: String,
    pub content_type: &'static str,
}

impl MonitoringResponse {
    fn new(status: u16, body: impl Into<String>, content_type: &'static str) -> Self {
        Self {
            status,
            body: body.into(),
            content_type,
        }
    }
}

/// Whether the bot is ready (the gateway is connected).
pub type Readiness = Arc<dyn Fn() -> bool + Send + Sync>;

/// The endpoint.
pub struct MonitoringServer {
    options: MonitoringOptions,
    metrics: Arc<MetricsRegistry>,
    health: Arc<HealthRegistry>,
    readiness: Readiness,
    expected_authorization: Option<Vec<u8>>,
    running: Mutex<Option<(watch::Sender<bool>, JoinHandle<()>)>>,
}

impl std::fmt::Debug for MonitoringServer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MonitoringServer")
            .field("host", &self.options.host)
            .field("port", &self.options.port)
            .finish_non_exhaustive()
    }
}

/// Equal without an early exit, so a guessed token cannot be timed one byte at a time.
fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0_u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

fn lower(status: HealthStatus) -> &'static str {
    match status {
        HealthStatus::Healthy => "healthy",
        HealthStatus::Degraded => "degraded",
        HealthStatus::Unhealthy => "unhealthy",
    }
}

fn json_string(value: &str) -> String {
    serde_json::to_string(value).unwrap_or_else(|_| "\"\"".into())
}

/// `0.###`: at most three decimals, trailing zeros dropped.
fn millis(value: f64) -> String {
    let text = format!("{value:.3}");
    let text = text.trim_end_matches('0').trim_end_matches('.');
    if text.is_empty() || text == "-" {
        "0".into()
    } else {
        text.to_owned()
    }
}

/// The `/health` body, in the C# layout (components sorted by name).
#[must_use]
pub fn health_json(report: &HealthReport) -> String {
    let mut json = String::new();
    let _ = write!(
        json,
        "{{\n  \"status\": \"{}\",\n  \"checkedAt\": \"{}\",\n  \"components\": {{\n",
        lower(report.status),
        report
            .checked_at
            .to_rfc3339_opts(chrono::SecondsFormat::Micros, true)
    );
    let mut first = true;
    for (name, c) in &report.components {
        if !first {
            json.push_str(",\n");
        }
        first = false;
        let _ = write!(
            json,
            "    {}: {{\"status\": \"{}\", \"detail\": {}, \"critical\": {}, \"durationMs\": {}}}",
            json_string(name),
            lower(c.status),
            c.detail
                .as_deref()
                .map_or_else(|| "null".into(), json_string),
            c.critical,
            millis(c.duration_ms)
        );
    }
    json.push_str("\n  }\n}");
    json
}

impl MonitoringServer {
    #[must_use]
    pub fn new(
        options: MonitoringOptions,
        metrics: Arc<MetricsRegistry>,
        health: Arc<HealthRegistry>,
        readiness: Option<Readiness>,
    ) -> Self {
        let expected_authorization = options
            .token
            .as_ref()
            .map(|t| format!("Bearer {}", t.expose()).into_bytes());
        Self {
            options,
            metrics,
            health,
            readiness: readiness.unwrap_or_else(|| Arc::new(|| true)),
            expected_authorization,
            running: Mutex::new(None),
        }
    }

    fn authorized(&self, authorization: Option<&str>) -> bool {
        match (&self.expected_authorization, authorization) {
            (Some(expected), Some(supplied)) => constant_time_eq(supplied.as_bytes(), expected),
            _ => false,
        }
    }

    /// Answer one request.
    pub async fn route(
        &self,
        method: &str,
        raw_url: &str,
        authorization: Option<&str>,
    ) -> MonitoringResponse {
        if !matches!(method, "GET" | "HEAD") {
            return MonitoringResponse::new(405, "method not allowed", TEXT);
        }
        let path = raw_url.split('?').next().unwrap_or("");
        if self.expected_authorization.is_some()
            && path != "/healthz"
            && !self.authorized(authorization)
        {
            return MonitoringResponse::new(401, "unauthorized", TEXT);
        }
        match path {
            "/metrics" => MonitoringResponse::new(
                200,
                self.metrics.render(),
                "text/plain; version=0.0.4; charset=utf-8",
            ),
            "/healthz" => MonitoringResponse::new(200, "ok", TEXT),
            "/ready" => {
                let ready = (self.readiness)();
                MonitoringResponse::new(
                    if ready { 200 } else { 503 },
                    format!("{{\"ready\":{ready}}}"),
                    JSON,
                )
            }
            "/health" => {
                let report = self.health.report().await;
                MonitoringResponse::new(
                    if report.status == HealthStatus::Unhealthy {
                        503
                    } else {
                        200
                    },
                    health_json(&report),
                    JSON,
                )
            }
            _ => MonitoringResponse::new(404, "", TEXT),
        }
    }

    /// Bind and serve, when `METRICS_PORT` is set. A port that will not bind is logged and the
    /// bot carries on: the endpoint observes a running bot, it must not stop one running.
    pub async fn start(self: &Arc<Self>) {
        let Some(port) = self.options.port.filter(|_| self.options.enabled()) else {
            tracing::debug!("METRICS_PORT not set - monitoring endpoint disabled");
            return;
        };
        let address = format!("{}:{port}", self.options.host);
        let listener = match TcpListener::bind(&address).await {
            Ok(listener) => listener,
            Err(error) => {
                tracing::warn!(%address, %error, "monitoring endpoint failed to bind - continuing without it");
                return;
            }
        };
        tracing::info!(
            "metrics on http://{address}/metrics, health on /health{}",
            if self.expected_authorization.is_some() {
                " (token required)"
            } else {
                ""
            }
        );
        let (stop, stopped) = watch::channel(false);
        let server = Arc::clone(self);
        let handle = tokio::spawn(async move { server.accept_loop(listener, stopped).await });
        *self.running.lock().unwrap_or_else(PoisonError::into_inner) = Some((stop, handle));
    }

    /// Stop accepting, and wait briefly for the loop to end.
    pub async fn stop(&self) {
        let running = self
            .running
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .take();
        if let Some((stop, handle)) = running {
            stop.send_replace(true);
            let _ = tokio::time::timeout(Duration::from_secs(2), handle).await;
        }
    }

    async fn accept_loop(
        self: Arc<Self>,
        listener: TcpListener,
        mut stopped: watch::Receiver<bool>,
    ) {
        let in_flight = Arc::new(Semaphore::new(MAX_CONCURRENT_REQUESTS));
        loop {
            let accepted = tokio::select! {
                accepted = listener.accept() => accepted,
                _ = stopped.wait_for(|s| *s) => return,
            };
            let mut stream = match accepted {
                Ok((stream, _)) => stream,
                Err(error) => {
                    tracing::warn!(%error, "monitoring accept failed");
                    continue;
                }
            };
            let Ok(permit) = Arc::clone(&in_flight).try_acquire_owned() else {
                tracing::warn!(
                    max = MAX_CONCURRENT_REQUESTS,
                    "monitoring at capacity, refused a request"
                );
                let _ = stream
                    .write_all(b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                    .await;
                continue;
            };
            let server = Arc::clone(&self);
            tokio::spawn(async move {
                if let Err(error) = server.serve(stream).await {
                    tracing::debug!(%error, "monitoring request failed");
                }
                drop(permit);
            });
        }
    }

    async fn serve(&self, mut stream: TcpStream) -> std::io::Result<()> {
        let head = match tokio::time::timeout(HEAD_TIMEOUT, read_head(&mut stream)).await {
            Ok(Ok(Some(head))) => head,
            Ok(Ok(None)) | Err(_) => {
                return write_response(&mut stream, false, &MonitoringResponse::new(400, "", TEXT))
                    .await;
            }
            Ok(Err(error)) => return Err(error),
        };
        let mut lines = head.split("\r\n");
        let mut request = lines.next().unwrap_or("").split(' ');
        let method = request.next().unwrap_or("").to_owned();
        let url = request.next().unwrap_or("/").to_owned();
        let authorization = lines.find_map(|line| {
            let (name, value) = line.split_once(':')?;
            name.trim()
                .eq_ignore_ascii_case("authorization")
                .then(|| value.trim().to_owned())
        });
        let response = self.route(&method, &url, authorization.as_deref()).await;
        write_response(&mut stream, method == "HEAD", &response).await
    }
}

/// The request head, up to the blank line. `None` when it is too big or not text.
async fn read_head(stream: &mut TcpStream) -> std::io::Result<Option<String>> {
    let mut head = Vec::with_capacity(512);
    let mut chunk = [0_u8; 1024];
    loop {
        let read = stream.read(&mut chunk).await?;
        if read == 0 {
            return Ok(None);
        }
        head.extend_from_slice(&chunk[..read]);
        if let Some(end) = head.windows(4).position(|w| w == b"\r\n\r\n") {
            head.truncate(end);
            return Ok(String::from_utf8(head).ok());
        }
        if head.len() > MAX_HEAD_BYTES {
            return Ok(None);
        }
    }
}

fn reason(status: u16) -> &'static str {
    match status {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Service Unavailable",
    }
}

async fn write_response(
    stream: &mut TcpStream,
    head_only: bool,
    response: &MonitoringResponse,
) -> std::io::Result<()> {
    let mut out = format!(
        "HTTP/1.1 {} {}\r\nContent-Type: {}\r\nContent-Length: {}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n",
        response.status,
        reason(response.status),
        response.content_type,
        response.body.len()
    )
    .into_bytes();
    if !head_only {
        out.extend_from_slice(response.body.as_bytes());
    }
    stream.write_all(&out).await?;
    stream.shutdown().await
}
