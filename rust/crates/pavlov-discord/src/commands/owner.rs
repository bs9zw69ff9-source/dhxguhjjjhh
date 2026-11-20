//! `/botstats`, `/manual`, `/firewall` and `/inspect` (C# `OwnerCommands.cs`). Owner only and
//! ephemeral, every one.
//!
//! - `/botstats` shows THE GAME SERVERS, which are what the box spends itself on, and a down
//!   server SAYS SO rather than showing the zeroes a stopped unit reports.
//! - `/manual` is AUDITED BEFORE IT IS SENT (a command that crashes the server must still be on
//!   record) and flattened to one line, or the record would lie about what was sent.
//! - A `/firewall` block is VERIFIED against `ufw status`: ufw can exit 0 having changed nothing.
//! - `/inspect` shows confirmed and guessed addresses APART: acting on a guess bans whoever else
//!   was connecting at that moment.

use std::net::IpAddr;
use std::sync::Arc;
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::moderation::BanRules;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::number::group_thousands;
use pavlov_host::firewall::Firewall;
use pavlov_host::kill_stats::KillStats;
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::moderation::bans::BanService;
use pavlov_host::registry::RconRegistry;
use pavlov_host::self_stats;
use pavlov_host::service_control::{ServiceControl, UnitState, UnitStats};

use crate::access::{Access, RequiredAccess};
use crate::caller::Caller;
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// The most of a command or reply shown.
const TEXT_LIMIT: usize = 1500;
/// Recent actions on `/inspect`.
const RECENT_ACTIONS: usize = 5;

fn refuse(access: &Access, caller: &Caller) -> Option<Embed> {
    (!access.allows(RequiredAccess::Owner, caller)).then(|| {
        theme::denied(
            "Not allowed",
            Some(&access.explain_refusal(caller, RequiredAccess::Owner)),
        )
    })
}

fn truncate(text: &str) -> String {
    if text.chars().count() > TEXT_LIMIT {
        format!("{}\n…", text.chars().take(TEXT_LIMIT).collect::<String>())
    } else {
        text.to_owned()
    }
}

#[allow(clippy::cast_precision_loss)]
fn bytes(bytes: u64) -> String {
    const KB: u64 = 1024;
    const MB: u64 = KB * 1024;
    const GB: u64 = MB * 1024;
    match bytes {
        b if b >= GB => format!("{:.1} GB", b as f64 / GB as f64),
        b if b >= MB => format!("{:.0} MB", b as f64 / MB as f64),
        b if b >= KB => format!("{:.0} KB", b as f64 / KB as f64),
        b => format!("{b} B"),
    }
}

/// `2d 3h`, `4h 12m`, `9m`.
#[must_use]
pub fn duration(span: Duration) -> String {
    let total = span.as_secs();
    let (days, hours, minutes) = (total / 86_400, (total % 86_400) / 3600, (total % 3600) / 60);
    if days >= 1 {
        format!("{days}d {hours}h")
    } else if total >= 3600 {
        format!("{}h {minutes}m", total / 3600)
    } else {
        format!("{}m", total / 60)
    }
}

/// One field per game server: a down one says so instead of showing zeroes.
#[must_use]
pub fn server_lines(stats: &[UnitStats], cores: usize) -> Vec<(String, String)> {
    let mut lines: Vec<(String, String)> = stats
        .iter()
        .enumerate()
        .map(|(i, s)| {
            let title = format!("Server {}", i + 1);
            if !matches!(s.state, UnitState::Active | UnitState::Starting) {
                let why = match s.state {
                    UnitState::Inactive => "stopped",
                    UnitState::Failed => "failed",
                    _ => "systemd could not be asked",
                };
                return (title, format!("`{}`\n{} {why}", s.unit, theme::BAD));
            }
            let mut body = vec![format!("`{}`", s.unit)];
            body.push(match (s.cpu_percent, s.cpu_total) {
                (Some(cpu), _) => format!("CPU **{}%**", trim_decimal(cpu)),
                (None, Some(total)) => {
                    format!("CPU {} min total", trim_decimal(total.as_secs_f64() / 60.0))
                }
                (None, None) => "CPU unknown".to_owned(),
            });
            if let Some(memory) = s.memory_bytes {
                body.push(match s.memory_limit_bytes {
                    Some(limit) => format!("RAM **{}** / {}", bytes(memory), bytes(limit)),
                    None => format!("RAM **{}**", bytes(memory)),
                });
            }
            if let Some(peak) = s.memory_peak_bytes {
                body.push(format!("peak {}", bytes(peak)));
            }
            if let Some(tasks) = s.tasks {
                body.push(format!("{tasks} tasks"));
            }
            if let Some(up) = s.uptime {
                body.push(format!("up {}", duration(up)));
            }
            (title, body.join("\n"))
        })
        .collect();
    // Said once: without it "180%" reads as impossible rather than "most of two cores".
    if !lines.is_empty() && stats.iter().any(|s| s.cpu_percent.is_some()) {
        lines.push((
            "CPU scale".into(),
            format!("100% = one core\n{cores} core(s) on this box"),
        ));
    }
    lines
}

/// `12.5` / `12` (C# `0.#`).
fn trim_decimal(value: f64) -> String {
    let text = format!("{value:.1}");
    text.strip_suffix(".0").map_or(text.clone(), str::to_owned)
}

// ---- /botstats ----

pub struct BotStatsCommand {
    pub metrics: Arc<MetricsRegistry>,
    pub services: Arc<ServiceControl>,
    pub access: Arc<Access>,
    pub kills: Option<Arc<KillStats>>,
}

impl std::fmt::Debug for BotStatsCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BotStatsCommand").finish_non_exhaustive()
    }
}

impl BotStatsCommand {
    async fn run(&self) -> Embed {
        let me = self_stats::read();
        let unknown = || "unknown".to_owned();
        let mut embed = theme::notice("Process stats", None)
            .field(
                "Resident",
                me.resident_bytes
                    .map_or_else(unknown, |b| format!("{:.1} MB", mb(b))),
                true,
            )
            .field(
                "Threads",
                me.threads.map_or_else(unknown, |t| t.to_string()),
                true,
            )
            .field("Uptime", me.uptime.map_or_else(unknown, duration), true)
            .field(
                "CPU time",
                me.cpu_time
                    .map_or_else(unknown, |c| format!("{:.1} min", c.as_secs_f64() / 60.0)),
                true,
            );
        let stats = self.metrics.stats();
        // A dropped series is a label explosion: a memory leak with extra steps.
        let dropped = if stats.dropped > 0 {
            format!(", **{} dropped** (label explosion)", stats.dropped)
        } else {
            String::new()
        };
        embed = embed.field(
            "Metrics",
            format!(
                "{} metric(s), {} series{dropped}",
                stats.metrics, stats.series
            ),
            true,
        );
        // WHETHER THE KILL PARSER MATCHES ANYTHING: zero on a played server means the log's
        // kill lines are not the expected shape, which errors nowhere and looks like quiet.
        if let Some(kills) = &self.kills {
            let all = kills.all();
            let value = if all.is_empty() {
                "**nothing recorded** — no kills yet, or the kill lines are not being parsed"
                    .to_owned()
            } else {
                let recorded: i64 = all.iter().map(|k| k.kills).sum();
                let last = all.iter().filter_map(|k| k.last_at).max();
                let when = last
                    .map(|w| format!(", last {}", theme::relative(w)))
                    .unwrap_or_default();
                format!(
                    "{} player(s), {} kill(s){when}",
                    all.len(),
                    group_thousands(recorded)
                )
            };
            embed = embed.field("Kill stats", value, true);
        }
        let cores = std::thread::available_parallelism().map_or(1, std::num::NonZeroUsize::get);
        for (title, body) in server_lines(&self.services.stats().await, cores) {
            embed = embed.field(title, body, true);
        }
        embed
    }
}

#[allow(clippy::cast_precision_loss)]
fn mb(bytes: u64) -> f64 {
    bytes as f64 / 1_048_576.0
}

impl SlashCommand for BotStatsCommand {
    fn name(&self) -> &str {
        "botstats"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "botstats",
            "Owner - CPU and memory for the bot and each game server",
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = match refuse(&self.access, &context.invocation.caller) {
                Some(refusal) => refusal,
                None => self.run().await,
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /manual ----

pub struct ManualCommand {
    pub rcon: Arc<RconRegistry>,
    pub audit: Arc<AuditLog>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for ManualCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ManualCommand").finish_non_exhaustive()
    }
}

impl ManualCommand {
    async fn run(
        &self,
        username: &str,
        server: &str,
        raw: Option<&str>,
    ) -> Result<Embed, CommandError> {
        let raw = Sanitize::message(raw);
        if raw.is_empty() {
            return Ok(theme::failure("Nothing to send", None));
        }
        self.audit
            .record("manual", username, server, Some(&raw))
            .await?;
        tracing::warn!(server, by = username, command = %raw, "MANUAL RCON");
        Ok(match self.rcon.send(server, &raw).await {
            Ok(reply) => {
                let reply = if reply.is_empty() {
                    "(no response)".to_owned()
                } else {
                    reply
                };
                // VERBATIM: interpreting it would hide the surprise this exists to surface.
                theme::notice(
                    &format!("Sent to {server}"),
                    Some(&format!(
                        "```\n{}\n```\n**Reply**\n```\n{}\n```",
                        truncate(&raw),
                        truncate(&reply)
                    )),
                )
            }
            Err(error) => theme::failure(
                "The server refused it",
                Some(&format!("```\n{}\n```", truncate(&error.to_string()))),
            ),
        })
    }
}

impl SlashCommand for ManualCommand {
    fn name(&self) -> &str {
        "manual"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        let server = self.rcon.servers().into_iter().fold(
            OptionSpec::new(OptionKind::String, "server", "Which server").required(),
            |option, name| option.choice(Choice::string(name.clone(), name)),
        );
        CommandSpec::new("manual", "Owner - Send a raw RCON command")
            .option(server)
            .option(
                OptionSpec::new(OptionKind::String, "command", "The exact command to send")
                    .required(),
            )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = match refuse(&self.access, &invocation.caller) {
                Some(refusal) => refusal,
                None => {
                    self.run(
                        &invocation.username,
                        invocation.string("server").unwrap_or(""),
                        invocation.string("command"),
                    )
                    .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /firewall ----

pub struct FirewallCommand {
    pub firewall: Arc<dyn Firewall>,
    pub audit: Arc<AuditLog>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for FirewallCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FirewallCommand").finish_non_exhaustive()
    }
}

const UFW_HINT: &str = "Usually ufw is not installed, or the bot is not running as root.";

impl FirewallCommand {
    async fn run(&self, username: &str, action: &str, raw_ip: &str) -> Result<Embed, CommandError> {
        if action == "status" {
            let status = self.firewall.status().await;
            return Ok(if status.ok {
                theme::notice(
                    "Firewall status",
                    Some(&format!("```\n{}\n```", truncate(&status.detail))),
                )
            } else {
                theme::failure(
                    "ufw did not run",
                    Some(&format!(
                        "```\n{}\n```\n{UFW_HINT}",
                        truncate(&status.detail)
                    )),
                )
            });
        }
        // PARSED, not pattern-matched: only a real address is ever passed on.
        let Ok(address) = raw_ip.parse::<IpAddr>() else {
            return Ok(theme::failure(
                "Not an IP address",
                Some(&format!(
                    "`{}` could not be parsed.",
                    Sanitize::code(Some(raw_ip))
                )),
            ));
        };
        let canonical = address.to_string();
        self.audit
            .record(&format!("firewall-{action}"), username, &canonical, None)
            .await?;
        tracing::warn!(action = %action.to_uppercase(), ip = %canonical, by = username, "FIREWALL");
        let result = if action == "block" {
            self.firewall.deny(&canonical).await
        } else {
            self.firewall.undeny(&canonical).await
        };
        if !result.ok {
            return Ok(theme::failure(
                "ufw did not run",
                Some(&format!(
                    "```\n{}\n```\n{UFW_HINT}",
                    truncate(&result.detail)
                )),
            ));
        }
        let shown = Sanitize::code(Some(raw_ip));
        if action == "unblock" {
            return Ok(theme::success(
                "Firewall block removed",
                Some(&format!("`{shown}` is no longer denied.")),
            ));
        }
        // VERIFIED AGAINST THE TABLE: ufw can exit 0 having changed nothing.
        let table = self.firewall.status().await;
        Ok(if table.ok && table.detail.contains(&canonical) {
            theme::warning(
                "Blocked at the firewall",
                Some(&format!("`{shown}` is now denied.")),
            )
            .field(
                format!("{} Affects everyone on that address", theme::WARN),
                "Undo with `/firewall unblock`.",
                false,
            )
        } else {
            theme::failure(
                "Not blocked",
                Some(&format!(
                    "`{shown}` is not in `ufw status`, so it is NOT blocked. The bot must run as root, or with a \
                     sudoers grant for `ufw`."
                )),
            )
            .field("ufw said", format!("```\n{}\n```", truncate(&result.detail)), false)
        })
    }
}

impl SlashCommand for FirewallCommand {
    fn name(&self) -> &str {
        "firewall"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "firewall",
            "Owner - Manual OS firewall (ufw) control. Bans are also denied automatically.",
        )
        .option(
            OptionSpec::new(OptionKind::String, "action", "What to do")
                .required()
                .choice(Choice::string("block", "block"))
                .choice(Choice::string("unblock", "unblock"))
                .choice(Choice::string("status", "status")),
        )
        .option(OptionSpec::new(
            OptionKind::String,
            "ip",
            "The address to block or unblock",
        ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = match refuse(&self.access, &invocation.caller) {
                Some(refusal) => refusal,
                None => {
                    self.run(
                        &invocation.username,
                        invocation.string("action").unwrap_or("status"),
                        invocation.string("ip").unwrap_or("").trim(),
                    )
                    .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /inspect ----

pub struct InspectCommand {
    pub tracking: Arc<IpTrackingService>,
    pub audit: Arc<AuditLog>,
    pub bans: Arc<BanService>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for InspectCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("InspectCommand").finish_non_exhaustive()
    }
}

impl InspectCommand {
    fn run(&self, raw: Option<&str>) -> Embed {
        let player = Sanitize::id(raw);
        let mut embed = theme::notice(
            &format!("Inspect — {}", Sanitize::code(Some(&player))),
            None,
        );
        match self.tracking.account_by_name(&player) {
            None => {
                embed.description = Some("No account on record.".into());
            }
            Some(account) => {
                let seen = |at: Option<chrono::DateTime<chrono::Utc>>| {
                    at.map_or_else(|| "unknown".to_owned(), theme::relative)
                };
                embed = embed
                    .field("Account id", format!("`{}`", account.id), true)
                    .field("First seen", seen(account.first_seen), true)
                    .field("Last seen", seen(account.last_seen), true);
                if account.names.len() > 1 {
                    let aka: Vec<String> = account
                        .names
                        .iter()
                        .skip(1)
                        .map(|n| format!("`{}`", Sanitize::code(Some(n))))
                        .collect();
                    embed = embed.field("Also known as", aka.join(", "), false);
                }
                if !account.confirmed_ips.is_empty() {
                    let ips: Vec<String> = account
                        .confirmed_ips
                        .iter()
                        .map(|ip| format!("`{ip}`"))
                        .collect();
                    embed = embed.field(
                        format!("Confirmed addresses ({})", account.confirmed_ips.len()),
                        ips.join("\n"),
                        false,
                    );
                }
                if !account.guessed_ips.is_empty() {
                    let ips: Vec<String> = account
                        .guessed_ips
                        .iter()
                        .map(|ip| format!("`{ip}`"))
                        .collect();
                    embed = embed.field(
                        format!(
                            "{} Guessed addresses ({})",
                            theme::WARN,
                            account.guessed_ips.len()
                        ),
                        format!(
                            "{}\n**Not certain** — never act on these alone.",
                            ips.join("\n")
                        ),
                        false,
                    );
                }
            }
        }
        let ban = self
            .bans
            .active_bans()
            .into_iter()
            .find(|b| BanRules::same_player(Some(&b.player_id), Some(&player)));
        let ban_text = match ban {
            None => "Not banned".to_owned(),
            Some(b) => {
                let length = if b.permanent {
                    "permanent".to_owned()
                } else {
                    format!(
                        "expires {}",
                        b.expires
                            .map_or_else(|| "unknown".to_owned(), theme::relative)
                    )
                };
                format!(
                    "{} — {length}",
                    Sanitize::code(Some(&Sanitize::redact_private(Some(
                        b.reason.as_deref().unwrap_or("no reason")
                    ))))
                )
            }
        };
        embed = embed.field("Ban", ban_text, false);
        let history: Vec<String> = self
            .audit
            .against(&player)
            .into_iter()
            .take(RECENT_ACTIONS)
            .map(|a| {
                format!(
                    "{} — `{}` by {}",
                    theme::relative(a.at),
                    a.action,
                    a.moderator
                )
            })
            .collect();
        if !history.is_empty() {
            embed = embed.field("Recent actions", history.join("\n"), false);
        }
        embed
    }
}

impl SlashCommand for InspectCommand {
    fn name(&self) -> &str {
        "inspect"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("inspect", "Owner - Everything the bot knows about a player").option(
            OptionSpec::new(OptionKind::String, "playerid", "Player ID or username")
                .required()
                .autocomplete(),
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = match refuse(&self.access, &context.invocation.caller) {
                Some(refusal) => refusal,
                None => self.run(context.invocation.string("playerid")),
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
