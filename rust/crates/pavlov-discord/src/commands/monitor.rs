//! `/monitor status|health|test` (C# `ServerStatusCommand.cs`): live server monitoring.
//!
//! - It renders with the SAME formatting the live board uses, so the two never disagree.
//! - CPU and RAM come from systemd and cost a sampling round trip, so they are read here, for
//!   the status view only, and never on the monitor's tick; when systemd cannot answer the
//!   card simply has no CPU/RAM.
//! - `test` is Admin: it pings the alert role and posts to the staff channel.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_core::text::number::group_thousands;
use pavlov_host::monitoring::board::{
    answering, cpu, ms, ram, span, state_colour, state_dot, state_name, tick, unknownable,
};
use pavlov_host::monitoring::{MonitorAlertSink, ServerMonitor};
use pavlov_host::service_control::{ServiceControl, UnitControl, UnitStats, number_for};

use crate::access::{Access, RequiredAccess};
use crate::caller::Caller;
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// The window the health views cover.
pub const HEALTH_WINDOW: Duration = Duration::from_secs(24 * 3600);

/// What the command is wired to.
pub struct MonitorCommand {
    pub monitor: Arc<ServerMonitor>,
    pub service: Arc<ServiceControl>,
    pub alerts: Arc<dyn MonitorAlertSink>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for MonitorCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MonitorCommand").finish_non_exhaustive()
    }
}

fn refuse(access: &Access, caller: &Caller, required: RequiredAccess) -> Option<Embed> {
    (!access.allows(required, caller)).then(|| {
        theme::denied(
            "Not allowed",
            Some(&access.explain_refusal(caller, required)),
        )
    })
}

fn players(players: Option<u32>, max: Option<u32>) -> String {
    match (players, max) {
        (Some(p), Some(m)) => format!("{p}/{m}"),
        (Some(p), None) => p.to_string(),
        (None, _) => "—".to_owned(),
    }
}

fn percent(value: f64) -> String {
    format!("{value:.2}")
}

impl MonitorCommand {
    async fn usage(&self) -> HashMap<String, UnitStats> {
        let by_unit: HashMap<String, UnitStats> = self
            .service
            .stats()
            .await
            .into_iter()
            .map(|s| (s.unit.clone(), s))
            .collect();
        self.monitor
            .servers()
            .into_iter()
            .filter_map(|server| {
                let unit = number_for(&server).and_then(|n| self.service.unit_for(n))?;
                by_unit.get(unit).cloned().map(|stats| (server, stats))
            })
            .collect()
    }

    fn status_card(&self, server: &str, usage: &HashMap<String, UnitStats>) -> Embed {
        let h = self.monitor.snapshot(server);
        let latency = h.latency.current.map_or_else(
            || "—".to_owned(),
            |l| format!("{} (avg {})", ms(l), ms(h.latency.average.unwrap_or(l))),
        );
        let mut embed = Embed::new()
            .color(state_colour(h.state))
            .title(format!(
                "{} {}",
                state_dot(h.state),
                Sanitize::code(Some(server))
            ))
            .field("Status", state_name(h.state), true)
            .field("Players", players(h.players, h.max_players), true)
            .field(
                "Map",
                h.map
                    .as_deref()
                    .filter(|m| !m.is_empty())
                    .map_or_else(|| "—".into(), |m| Sanitize::code(Some(m))),
                true,
            )
            .field(
                "RCON",
                if answering(h.state) {
                    "Connected"
                } else {
                    "Disconnected"
                },
                true,
            )
            .field("Latency", latency, true)
            .field(
                "Last check",
                h.last_probe_at
                    .map_or_else(|| "never".into(), |at| format!("<t:{}:T>", at.timestamp())),
                true,
            );
        if let Some(u) = usage.get(server) {
            embed = embed
                .field("CPU", cpu(u), true)
                .field("RAM", ram(u), true)
                .field(
                    "Server uptime",
                    u.uptime.map_or_else(|| "—".into(), span),
                    true,
                );
        }
        embed.field(
            "Health",
            format!(
                "RCON {}  ·  Logs {}  ·  Players {}  ·  Process {}",
                tick(answering(h.state)),
                tick(!h.log_inactive_reported),
                tick(h.players.is_some()),
                unknownable(h.process_running)
            ),
            false,
        )
    }

    fn status_overview(&self, servers: &[String], usage: &HashMap<String, UnitStats>) -> Embed {
        let lines: Vec<String> = servers
            .iter()
            .map(|s| {
                let h = self.monitor.snapshot(s);
                let latency = h.latency.current.map_or_else(|| "—".to_owned(), ms);
                let host = usage
                    .get(s)
                    .map(|u| format!(" · {} cpu · {}", cpu(u), ram(u)))
                    .unwrap_or_default();
                format!(
                    "{} **{}** — {} · {} · {latency}{host}",
                    state_dot(h.state),
                    Sanitize::code(Some(s)),
                    state_name(h.state),
                    players(h.players, h.max_players)
                )
            })
            .collect();
        let mut embed = theme::notice("Server status", Some(&lines.join("\n")));
        embed.footer = Some(format!(
            "{} server(s) · 100% cpu = one core · updated live",
            servers.len()
        ));
        embed
    }

    fn health_card(&self, server: &str) -> Embed {
        let s = self.monitor.stats(server, HEALTH_WINDOW);
        let title = format!("{} — last 24h", Sanitize::code(Some(server)));
        if s.samples == 0 {
            return theme::notice(&title, Some("No monitoring data yet."));
        }
        theme::notice(&title, None)
            .field("Uptime", format!("{}%", percent(s.uptime_percent)), true)
            .field("RCON failures", s.rcon_failures.to_string(), true)
            .field(
                "Unexpected restarts",
                s.unexpected_restarts.to_string(),
                true,
            )
            .field(
                "Average latency",
                s.average_latency_ms.map_or_else(
                    || "—".to_owned(),
                    // Rounded to whole milliseconds, as the C# "N0" shows it.
                    #[allow(clippy::cast_possible_truncation)]
                    |a| format!("{}ms", group_thousands(a.round() as i64)),
                ),
                true,
            )
            .field("Longest outage", span(s.longest_outage), true)
            .field("Samples", s.samples.to_string(), true)
    }

    fn health_overview(&self, servers: &[String]) -> Embed {
        let lines: Vec<String> = servers
            .iter()
            .map(|server| {
                let s = self.monitor.stats(server, HEALTH_WINDOW);
                if s.samples == 0 {
                    format!("**{}** — no data yet", Sanitize::code(Some(server)))
                } else {
                    format!(
                        "**{}** — {}% up · {} RCON fails · {} restarts",
                        Sanitize::code(Some(server)),
                        percent(s.uptime_percent),
                        s.rcon_failures,
                        s.unexpected_restarts
                    )
                }
            })
            .collect();
        theme::notice("Health — last 24h", Some(&lines.join("\n")))
    }

    async fn run(&self, context: &CommandContext<'_>) -> Embed {
        let invocation = context.invocation;
        let caller = &invocation.caller;
        let sub = invocation.subcommand();
        if sub == Some("test") {
            if let Some(refusal) = refuse(&self.access, caller, RequiredAccess::Admin) {
                return refusal;
            }
            let result = self.alerts.test().await;
            return theme::notice("Monitoring alert test", Some(&result));
        }
        let wanted = Sanitize::id(invocation.string("server"));
        let servers = self.monitor.servers();
        if servers.is_empty() {
            return theme::notice("No servers configured", None);
        }
        let one = (!wanted.is_empty())
            .then(|| servers.iter().find(|s| eq_ignore_case(s, &wanted)).cloned());
        let one = match one {
            Some(None) => {
                let known: Vec<String> = servers.iter().map(|s| Sanitize::code(Some(s))).collect();
                return theme::failure(
                    "Unknown server",
                    Some(&format!(
                        "`{}` is not one of: {}.",
                        Sanitize::code(Some(&wanted)),
                        known.join(", ")
                    )),
                );
            }
            Some(Some(server)) => Some(server),
            None => None,
        };
        if sub == Some("health") {
            return one.map_or_else(|| self.health_overview(&servers), |s| self.health_card(&s));
        }
        let usage = self.usage().await;
        match one {
            Some(server) => self.status_card(&server, &usage),
            None => self.status_overview(&servers, &usage),
        }
    }
}

impl SlashCommand for MonitorCommand {
    fn name(&self) -> &str {
        "monitor"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        let server = || {
            OptionSpec::new(OptionKind::String, "server", "Which server (default: all)")
                .autocomplete()
        };
        CommandSpec::new("monitor", "Mod - Live server monitoring")
            .option(OptionSpec::sub("status", "Current status, one server or all").option(server()))
            .option(
                OptionSpec::sub("health", "Health statistics over the last 24 hours")
                    .option(server()),
            )
            .option(OptionSpec::sub(
                "test",
                "Admin - post a test alert to the monitoring channel",
            ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = match refuse(
                &self.access,
                &context.invocation.caller,
                RequiredAccess::Mod,
            ) {
                Some(refusal) => refusal,
                None => self.run(&context).await,
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
