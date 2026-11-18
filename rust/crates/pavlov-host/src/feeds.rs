//! The Discord webhook feeds: joins, connections, kills, RCON commands and staff actions (C#
//! `Discord/FeedWebhooks.cs`). Webhooks are plain HTTP, so this needs no gateway connection.
//!
//! - POSTING IS QUEUED, NEVER AWAITED BY THE CALLER: a burst of kills behind Discord's rate
//!   limit used to hold up reading Pavlov.log, and joins never reached ban evasion. A full
//!   queue drops the oldest feed line; the log is never held up.
//! - REGISTERING TOUCHES NO NETWORK: only the URL's shape is checked, so a webhook Discord
//!   could not be reached for at boot is not dead for the life of the process.
//! - A DEAD WEBHOOK IS NOT RETRIED ON EVERY POST: it is reopened after a minute.
//! - NOTHING PINGS: every post forbids mentions; player names are attacker-controlled.
//! - Every feed has a status, so `/feeds` can say why one is quiet.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use indexmap::IndexMap;
use pavlov_core::embed::{Embed, palette};
use pavlov_core::text::Sanitize;
use pavlov_core::time::{Clock, EasternTime};
use serde_json::json;
use tokio::task::JoinHandle;

use crate::http::{HttpClient, HttpRequest};
use crate::metrics::{MetricLabels, MetricsRegistry};
use crate::queue::DropOldestQueue;

pub const JOIN: &str = "join";
pub const CONNECT: &str = "connect";
pub const KILL: &str = "kill";
pub const RCON: &str = "rcon";
pub const STAFF: &str = "staff";

pub const QUEUE_CAPACITY: usize = 500;
const REOPEN_AFTER: TimeDelta = TimeDelta::minutes(1);
const SEND_TIMEOUT: Duration = Duration::from_secs(10);
/// Discord's hard message limit is 2000; truncating beats the whole post being rejected.
const MAX_TEXT: usize = 1900;
/// The longest a 429 is waited out before the line is given up.
const MAX_RATE_LIMIT_WAIT: Duration = Duration::from_secs(30);

#[derive(Debug, Clone)]
enum Body {
    Text(String),
    Embed(Embed),
}

#[derive(Debug, Clone)]
struct Pending {
    label: String,
    body: Body,
}

#[derive(Debug, Default)]
struct State {
    urls: HashMap<String, String>,
    status: IndexMap<String, String>,
    confirmed: HashSet<String>,
    retry_after: HashMap<String, DateTime<Utc>>,
}

/// The webhook feeds. Construct with [`FeedWebhooks::new`], register each feed, then
/// [`start`](FeedWebhooks::start) the sender.
pub struct FeedWebhooks {
    http: Arc<dyn HttpClient>,
    metrics: Arc<MetricsRegistry>,
    clock: Arc<dyn Clock>,
    state: Mutex<State>,
    queue: DropOldestQueue<Pending>,
    sender: Mutex<Option<JoinHandle<()>>>,
}

impl std::fmt::Debug for FeedWebhooks {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FeedWebhooks")
            .field("status", &self.status())
            .finish_non_exhaustive()
    }
}

/// A Discord webhook address: absolute http(s), with `/webhooks/` in the path.
fn is_webhook_url(url: &str) -> bool {
    let Some((scheme, rest)) = url.split_once("://") else {
        return false;
    };
    let host = rest.split(['/', '?', '#']).next().unwrap_or_default();
    let path = &rest[host.len()..];
    matches!(scheme.to_ascii_lowercase().as_str(), "http" | "https")
        && !host.is_empty()
        && !host.contains(char::is_whitespace)
        && path.to_ascii_lowercase().contains("/webhooks/")
}

fn stamp(at: DateTime<Utc>) -> String {
    EasternTime::stamp(Some(at))
}

fn where_(server: Option<&str>) -> String {
    match server.filter(|s| !s.trim().is_empty()) {
        Some(server) => Sanitize::message(Some(server)),
        None => "the server".into(),
    }
}

/// `[stamp] Alice joined Server 2`. The PUBLIC feed: never an address.
#[must_use]
pub fn join_line(name: &str, server: Option<&str>, at: DateTime<Utc>) -> String {
    format!(
        "[{}] {} joined {}",
        stamp(at),
        Sanitize::message(Some(name)),
        where_(server)
    )
}

#[must_use]
pub fn leave_line(name: &str, server: Option<&str>, at: DateTime<Utc>) -> String {
    format!(
        "[{}] {} left {}",
        stamp(at),
        Sanitize::message(Some(name)),
        where_(server)
    )
}

/// The STAFF connect feed: the address, where it is, and the VPN verdict.
#[must_use]
pub fn connect_line(
    name: &str,
    ip: Option<&str>,
    location: Option<&str>,
    verdict: Option<&str>,
    at: DateTime<Utc>,
) -> String {
    let mut parts = vec![format!(
        "[{}] JOIN  {}",
        stamp(at),
        Sanitize::message(Some(name))
    )];
    if let Some(ip) = ip.filter(|s| !s.is_empty()) {
        parts.push(format!("ip={ip}"));
    }
    if let Some(location) = location.filter(|s| !s.is_empty()) {
        parts.push(location.to_owned());
    }
    if let Some(verdict) = verdict.filter(|s| !s.is_empty()) {
        parts.push(format!("vpn={verdict}"));
    }
    parts.join("  |  ")
}

/// `[stamp] BAN  Mod → Player  (reason)`.
#[must_use]
pub fn staff_line(
    action: &str,
    moderator: &str,
    target: &str,
    reason: Option<&str>,
    at: DateTime<Utc>,
) -> String {
    let mut line = format!(
        "[{}] {}  {}",
        stamp(at),
        Sanitize::message(Some(action)).to_uppercase(),
        Sanitize::message(Some(moderator))
    );
    if !target.is_empty() {
        line.push_str(&format!(" → {}", Sanitize::message(Some(target))));
    }
    if let Some(reason) = reason.filter(|r| !r.is_empty()) {
        line.push_str(&format!("  ({})", Sanitize::message(Some(reason))));
    }
    line
}

/// A kill. No weapon on a self-kill is the world (a fall, a drown); no killer is "the world".
#[must_use]
pub fn kill_line(
    killer: Option<&str>,
    killed: &str,
    weapon: Option<&str>,
    at: DateTime<Utc>,
    headshot: bool,
) -> String {
    let who = Sanitize::message(Some(killed));
    let with = weapon
        .filter(|w| !w.is_empty())
        .map(|w| Sanitize::message(Some(w)));
    let detail = match (&with, headshot) {
        (Some(named), true) => format!("  ({named}, headshot)"),
        (Some(named), false) => format!("  ({named})"),
        (None, true) => "  (headshot)".into(),
        (None, false) => String::new(),
    };
    if killer.is_some_and(|k| !k.is_empty() && k == killed) {
        return match with {
            None => format!("[{}] KILL  {who} died", stamp(at)),
            Some(_) => format!("[{}] KILL  {who} killed themselves{detail}", stamp(at)),
        };
    }
    let by = match killer.filter(|k| !k.is_empty()) {
        Some(killer) => Sanitize::message(Some(killer)),
        None => "the world".into(),
    };
    format!("[{}] KILL  {by} → {who}{detail}", stamp(at))
}

/// An RCON or RCON+ command the game logged. A FAILED one is red whatever the channel colour,
/// because it is the one an operator most needs to catch.
#[must_use]
pub fn rcon_embed(
    plus: bool,
    verb: &str,
    instigator: Option<&str>,
    argument: Option<&str>,
    server: Option<&str>,
    at: DateTime<Utc>,
    successful: Option<bool>,
) -> Embed {
    let command = match argument.filter(|a| !a.is_empty()) {
        Some(a) => format!(
            "{} {}",
            Sanitize::message(Some(verb)),
            Sanitize::message(Some(a))
        ),
        None => Sanitize::message(Some(verb)),
    };
    let went_through = match successful {
        Some(true) => "✅ true",
        Some(false) => "❌ false",
        None => "☑️ executed",
    };
    let color = match (successful, plus) {
        (Some(false), _) => palette::BAN_RED,
        (_, true) => palette::AMBER,
        (_, false) => palette::BLUE,
    };
    Embed::new()
        .color(color)
        .author(if plus {
            "RCON+ command"
        } else {
            "RCON command"
        })
        .field("Command", format!("`{command}`"), true)
        .field(
            "By",
            instigator
                .filter(|w| !w.is_empty())
                .map_or_else(|| "console".into(), |w| Sanitize::message(Some(w))),
            true,
        )
        .field("Server", where_(server), true)
        .field("Went through", went_through, true)
        .timestamp(at)
}

impl FeedWebhooks {
    #[must_use]
    pub fn new(
        http: Arc<dyn HttpClient>,
        metrics: Arc<MetricsRegistry>,
        clock: Arc<dyn Clock>,
    ) -> Self {
        Self {
            http,
            metrics,
            clock,
            state: Mutex::new(State::default()),
            queue: DropOldestQueue::new(QUEUE_CAPACITY),
            sender: Mutex::new(None),
        }
    }

    fn state(&self) -> std::sync::MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn set_status(&self, label: &str, status: impl Into<String>) {
        self.state().status.insert(label.to_owned(), status.into());
    }

    /// Start the sender task.
    pub fn start(self: &Arc<Self>) {
        let feeds = Arc::clone(self);
        let task = tokio::spawn(async move { feeds.send_loop().await });
        *self.sender.lock().unwrap_or_else(PoisonError::into_inner) = Some(task);
    }

    /// Register a feed. Blank is "off", which is a choice; a malformed URL is an error said at
    /// once, since it is fixable now. Whether the webhook exists is learned on the first post.
    pub fn register(&self, label: &str, url: Option<&str>) {
        let url = url.map(str::trim).filter(|u| !u.is_empty());
        let Some(url) = url else {
            self.set_status(label, "not configured");
            tracing::debug!(label, "webhook not configured - that feed is off");
            return;
        };
        if !is_webhook_url(url) {
            self.set_status(label, "invalid URL - not a Discord webhook address");
            tracing::error!(
                label,
                "the webhook URL does not look like a Discord webhook (expected .../api/webhooks/<id>/<token>) - that feed will do nothing"
            );
            return;
        }
        let mut state = self.state();
        state.urls.insert(label.to_owned(), url.to_owned());
        state
            .status
            .insert(label.to_owned(), "configured, nothing posted yet".into());
    }

    /// Every feed's status, in registration order.
    #[must_use]
    pub fn status(&self) -> IndexMap<String, String> {
        self.state().status.clone()
    }

    #[must_use]
    pub fn is_configured(&self, label: &str) -> bool {
        self.state().urls.contains_key(label)
    }

    /// Lines lost to a full queue.
    #[must_use]
    pub fn dropped(&self) -> u64 {
        self.queue.dropped()
    }

    fn enqueue(&self, label: &str, body: Body) {
        if !self.is_configured(label) {
            return; // feed off: nothing to queue
        }
        if let Some(lost) = self.queue.push(Pending {
            label: label.to_owned(),
            body,
        }) {
            if self.queue.dropped() == 1 {
                tracing::warn!(
                    capacity = QUEUE_CAPACITY,
                    "the feed queue is full - dropping the oldest lines; Discord is rate-limiting the webhooks faster than events arrive"
                );
            }
            self.metrics.increment(
                "feed_dropped_total",
                &MetricLabels::of("feed", &lost.label),
                1.0,
                Some("Feed lines dropped because the queue was full"),
            );
        }
    }

    /// Queue a line. Never waits on Discord, never fails.
    pub fn post(&self, label: &str, content: &str) {
        if !content.is_empty() {
            self.enqueue(label, Body::Text(content.to_owned()));
        }
    }

    /// Queue an embed.
    pub fn post_embed(&self, label: &str, embed: Embed) {
        self.enqueue(label, Body::Embed(embed));
    }

    pub fn post_join(&self, name: &str, server: Option<&str>, at: DateTime<Utc>) {
        self.post(JOIN, &join_line(name, server, at));
    }

    pub fn post_leave(&self, name: &str, server: Option<&str>, at: DateTime<Utc>) {
        self.post(JOIN, &leave_line(name, server, at));
    }

    pub fn post_connect(
        &self,
        name: &str,
        ip: Option<&str>,
        location: Option<&str>,
        verdict: Option<&str>,
        at: DateTime<Utc>,
    ) {
        self.post(CONNECT, &connect_line(name, ip, location, verdict, at));
    }

    pub fn post_staff(
        &self,
        action: &str,
        moderator: &str,
        target: &str,
        reason: Option<&str>,
        at: DateTime<Utc>,
    ) {
        self.post(STAFF, &staff_line(action, moderator, target, reason, at));
    }

    pub fn post_kill(
        &self,
        killer: Option<&str>,
        killed: &str,
        weapon: Option<&str>,
        at: DateTime<Utc>,
        headshot: bool,
    ) {
        self.post(KILL, &kill_line(killer, killed, weapon, at, headshot));
    }

    #[allow(clippy::too_many_arguments)]
    pub fn post_rcon(
        &self,
        plus: bool,
        verb: &str,
        instigator: Option<&str>,
        argument: Option<&str>,
        server: Option<&str>,
        at: DateTime<Utc>,
        successful: Option<bool>,
    ) {
        self.post_embed(
            RCON,
            rcon_embed(plus, verb, instigator, argument, server, at, successful),
        );
    }

    async fn send_loop(&self) {
        while self.queue.ready().await {
            for item in self.queue.take(16) {
                self.send(item).await;
            }
        }
    }

    /// The URL to post to, unless the feed is off or backing off after a failure.
    fn open(&self, label: &str) -> Option<String> {
        let now = self.clock.now();
        let mut state = self.state();
        if state
            .retry_after
            .get(label)
            .is_some_and(|until| now < *until)
        {
            return None;
        }
        state.retry_after.remove(label);
        state.urls.get(label).cloned()
    }

    async fn send(&self, item: Pending) {
        let Some(url) = self.open(&item.label) else {
            return;
        };
        let payload = match &item.body {
            Body::Text(text) => {
                let text = if text.chars().count() > MAX_TEXT {
                    format!("{} …", text.chars().take(MAX_TEXT).collect::<String>())
                } else {
                    text.clone()
                };
                json!({ "content": text, "allowed_mentions": { "parse": [] } })
            }
            Body::Embed(embed) => {
                json!({ "embeds": [embed.to_json()], "allowed_mentions": { "parse": [] } })
            }
        }
        .to_string();
        let mut waited = false;
        loop {
            let response = self
                .http
                .send(HttpRequest::post_json(
                    url.clone(),
                    payload.clone(),
                    SEND_TIMEOUT,
                ))
                .await;
            match response {
                Ok(r) if r.is_success() => {
                    self.delivered(&item.label);
                    return;
                }
                // A 429 is waited out once, as Discord asks, then the line is given up.
                Ok(r) if r.status == 429 && !waited => {
                    waited = true;
                    let seconds = serde_json::from_slice::<serde_json::Value>(&r.body)
                        .ok()
                        .and_then(|v| v.get("retry_after").and_then(serde_json::Value::as_f64))
                        .or_else(|| r.header("retry-after").and_then(|h| h.trim().parse().ok()))
                        .unwrap_or(1.0);
                    let wait = Duration::from_secs_f64(
                        seconds.clamp(0.0, MAX_RATE_LIMIT_WAIT.as_secs_f64()),
                    );
                    tokio::time::sleep(wait).await;
                }
                Ok(r) if r.status == 404 || r.status == 401 => {
                    self.not_delivering(&item.label, "the webhook no longer exists - it was probably deleted, or the channel was. Set a new URL in .env");
                    return;
                }
                Ok(r) => {
                    self.failing(&item.label, &format!("HTTP {}", r.status));
                    return;
                }
                Err(error) => {
                    tracing::debug!(label = %item.label, %error, "webhook post failed");
                    self.not_delivering(
                        &item.label,
                        "could not reach Discord - will try again shortly",
                    );
                    return;
                }
            }
        }
    }

    fn delivered(&self, label: &str) {
        self.metrics.increment(
            "feed_posts_total",
            &MetricLabels::of("feed", label),
            1.0,
            Some("Feed lines posted"),
        );
        let first = {
            let mut state = self.state();
            state.status.insert(label.to_owned(), "delivering".into());
            state.confirmed.insert(label.to_owned())
        };
        if first {
            tracing::info!(label, "feed confirmed - first line delivered");
        }
    }

    fn back_off(&self, label: &str, status: String) {
        let until = self.clock.now() + REOPEN_AFTER;
        let mut state = self.state();
        state.status.insert(label.to_owned(), status);
        state.retry_after.insert(label.to_owned(), until);
        state.confirmed.remove(label);
    }

    fn not_delivering(&self, label: &str, advice: &str) {
        self.metrics.increment(
            "feed_errors_total",
            &MetricLabels::of("feed", label),
            1.0,
            Some("Feed posts that failed"),
        );
        tracing::error!(label, advice, "webhook not delivering");
        self.back_off(label, format!("not delivering: {advice}"));
    }

    fn failing(&self, label: &str, why: &str) {
        self.metrics.increment(
            "feed_errors_total",
            &MetricLabels::of("feed", label),
            1.0,
            Some("Feed posts that failed"),
        );
        tracing::warn!(label, why, "feed post failed");
        self.back_off(label, format!("failing: {why}"));
    }

    /// Give what is queued five seconds to go out, then stop.
    pub async fn shutdown(&self) {
        self.queue.close();
        let task = self
            .sender
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .take();
        if let Some(mut task) = task {
            if tokio::time::timeout(Duration::from_secs(5), &mut task)
                .await
                .is_err()
            {
                task.abort();
            }
        }
    }
}
