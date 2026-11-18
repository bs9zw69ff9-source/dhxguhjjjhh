//! Running the built graph (C# the tail of `Program.Main` and the hosted services' start and
//! stop), and `--selftest`.
//!
//! The start order is the C#'s: the startup summary, the monitoring endpoint, the background
//! services, master access, then the gateway LAST, so nothing Discord can trigger runs before
//! the services it needs. The stop is the reverse, with in-flight interactions drained first.

use std::path::Path;
use std::sync::Arc;
use std::time::{Duration, Instant};

use pavlov_core::servers::HostProbe;
use pavlov_core::skin;
use pavlov_discord::component_router::ComponentRouter;
use pavlov_gateway::{DiscordClient, Role, Wiring};
use pavlov_host::build;
use pavlov_host::factions::factions_file::describe_source;
use pavlov_host::metrics::{MetricLabels, MetricsRegistry};
use pavlov_host::monitoring_server::MonitoringServer;
use pavlov_host::self_stats::SelfStats;

use crate::graph::Graph;
use crate::services;
use crate::startup;

/// How long in-flight interactions get to finish at shutdown.
pub const DRAIN: Duration = Duration::from_secs(10);

/// The process gauges (C# the `runtime-metrics` tick; there is no managed heap or GC here).
pub fn record_process_metrics(metrics: &MetricsRegistry, stats: &SelfStats) {
    let none = MetricLabels::none();
    #[allow(clippy::cast_precision_loss)]
    {
        if let Some(bytes) = stats.resident_bytes {
            metrics.gauge(
                "process_resident_bytes",
                bytes as f64,
                &none,
                Some("Resident set size in bytes"),
            );
        }
        if let Some(threads) = stats.threads {
            metrics.gauge("process_threads", threads as f64, &none, Some("OS threads"));
        }
    }
    if let Some(uptime) = stats.uptime {
        metrics.gauge(
            "process_uptime_seconds",
            uptime.as_secs_f64(),
            &none,
            Some("Seconds since process start"),
        );
    }
    if let Some(cpu) = stats.cpu_time {
        metrics.gauge(
            "process_cpu_seconds",
            cpu.as_secs_f64(),
            &none,
            Some("CPU time used"),
        );
    }
}

/// The startup summary, then the warnings (C# the `logger.LogInformation` block).
pub async fn log_startup(graph: &Graph, cwd: &Path, probe: &dyn HostProbe) {
    tracing::info!(build = %build::stamp(), "build");
    tracing::info!(
        servers = graph.options.servers.len(),
        data = %graph.options.data_directory.display(),
        "starting"
    );
    for line in graph.features.describe(probe) {
        tracing::info!("  {line}");
    }
    let names: Vec<&str> = graph.factions.names().collect();
    tracing::info!(
        "  factions: {} from {} - {}",
        names.len(),
        describe_source(
            graph.features.factions_path.as_deref().map(Path::new),
            graph.features.faction_set_name.as_deref(),
            cwd
        ),
        names.join(", ")
    );
    let carried = pavlov_host::factions::renames::carry_over(&graph.store, &graph.factions).await;
    if carried > 0 {
        tracing::info!(carried, "carried renamed factions' records over");
    }
    tracing::info!(
        "  theme: {} (THEME), footer \"{}\"",
        skin::police().name,
        skin::theme::brand_name()
    );
    tracing::info!(
        "  whitelist bot: {}",
        if graph.options.faction_bot_enabled() {
            "on (owns /whitelist /promotion /demotion /subclass)"
        } else {
            "off (FACTION_BOT_TOKEN / FACTION_CLIENT_ID not set - those commands stay on the main bot)"
        }
    );
    let rosters = graph.rosters.ensure_roster_files();
    tracing::info!("  faction rosters: {}", rosters.summary);
    if graph.seeded > 0 {
        tracing::info!(seeded = graph.seeded, "seeded empty datasets");
    }
    tracing::info!("  staff roles outside a server: {}", graph.home.describe());
    for line in graph
        .mod_save_sync
        .diagnose(graph.features.ledger_directory.as_deref())
    {
        tracing::info!("  ModSave sync: {line}");
    }
    let status: Vec<String> = graph
        .feeds
        .status()
        .into_iter()
        .map(|(k, v)| format!("{k}={v}"))
        .collect();
    tracing::info!("Feeds: {}", status.join(" | "));
    if let Some(warning) = graph.vpn.configuration_warning() {
        tracing::warn!("{warning}");
    }
    if !graph.disabled_unknown.is_empty() {
        tracing::warn!(
            "COMMANDS_DISABLED names {}, which is not a command here",
            graph.disabled_unknown.join(", ")
        );
    }
    for warning in startup::warnings(&graph.features, &startup::environment(cwd)) {
        tracing::warn!("{warning}");
    }
}

/// `--selftest`: what was built, without starting anything. The deploy gate reads the
/// "graph built" line.
#[must_use]
pub fn selftest_report(graph: &Graph, started: Instant) -> Vec<String> {
    let mut lines = vec![
        format!(
            "selftest: graph built in {}ms",
            started.elapsed().as_millis()
        ),
        format!(
            "selftest: {} RCON server(s), {} command(s): {}",
            graph.options.servers.len(),
            graph.command_names.len(),
            graph
                .command_names
                .iter()
                .map(|c| format!("/{c}"))
                .collect::<Vec<_>>()
                .join(", ")
        ),
        format!(
            "selftest: {} background service(s): {}",
            graph.services.count(),
            graph.services.names().join(", ")
        ),
        format!(
            "selftest: {} component prefix(es): {}",
            graph.router.prefixes().len(),
            graph.router.prefixes().join(", ")
        ),
    ];
    if graph.disabled_count > 0 {
        lines.push(format!(
            "selftest: {} command(s) disabled by COMMANDS_DISABLED",
            graph.disabled_count
        ));
    }
    for unknown in &graph.disabled_unknown {
        lines.push(format!(
            "selftest: WARNING - COMMANDS_DISABLED names \"{unknown}\", which is not a command here"
        ));
    }
    let stats = pavlov_host::self_stats::read();
    #[allow(clippy::cast_precision_loss)]
    if let Some(bytes) = stats.resident_bytes {
        lines.push(format!(
            "selftest: resident {:.1} MB",
            bytes as f64 / 1024.0 / 1024.0
        ));
    }
    lines.push(
        "selftest: gateway ready=false (not connected - this is construction only)".to_owned(),
    );
    lines
}

fn wiring(graph: &Graph, role: Role, router: Arc<ComponentRouter>) -> Wiring {
    let autocomplete = Arc::clone(&graph.autocomplete);
    Wiring {
        role,
        runner: Arc::clone(&graph.runner),
        router,
        suggest: Arc::new(move |focused, typed, faction| {
            autocomplete.for_focused(focused, typed, faction)
        }),
        blacklist: Some(Arc::clone(&graph.blacklist)),
        metrics: Arc::clone(&graph.metrics),
        dispatcher: Arc::clone(&graph.dispatcher),
    }
}

async fn stopped() {
    let terminate = async {
        match tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()) {
            Ok(mut signal) => {
                signal.recv().await;
            }
            Err(error) => {
                tracing::warn!(%error, "cannot listen for SIGTERM; only Ctrl-C stops the bot");
                std::future::pending::<()>().await;
            }
        }
    };
    tokio::select! {
        _ = tokio::signal::ctrl_c() => tracing::info!("interrupted"),
        () = terminate => tracing::info!("stopping (SIGTERM)"),
    }
}

/// Run until stopped.
///
/// # Errors
/// When the services cannot be registered (a wiring bug), or the main gateway fails for good.
pub async fn run(
    graph: Graph,
    main: DiscordClient,
    faction: Option<DiscordClient>,
) -> Result<(), String> {
    graph.feeds.start();
    let ready = main.ready_flag();
    let monitoring = Arc::new(MonitoringServer::new(
        graph.options.monitoring.clone(),
        Arc::clone(&graph.metrics),
        Arc::clone(&graph.health),
        Some(Arc::new(move || ready())),
    ));
    monitoring.start().await;

    services::register(&graph).map_err(|e| format!("service registration: {e}"))?;
    graph
        .services
        .start_all()
        .await
        .map_err(|e| format!("service start: {e}"))?;
    graph.master_access.start(Some(&graph.tracking)).await;

    main.wire(wiring(
        &graph,
        Role::Main {
            settings: graph.registration,
            commands: graph.main_specs.clone(),
        },
        Arc::clone(&graph.router),
    ));
    let main_stop = main.shutdown();
    let mut faction_stop = None;
    let faction_run = faction.map(|client| {
        client.wire(wiring(
            &graph,
            Role::Faction {
                commands: graph.faction_specs.clone(),
            },
            Arc::clone(&graph.router),
        ));
        faction_stop = Some(client.shutdown());
        tokio::spawn(async move {
            if let Err(error) = client.run().await {
                // The whitelist bot failing must not take the main bot down with it.
                tracing::error!(%error, "the whitelist bot's gateway stopped");
            }
        })
    });

    let outcome = tokio::select! {
        result = main.run() => result.map_err(|e| format!("gateway: {e}")),
        () = stopped() => Ok(()),
    };

    main_stop.stop().await;
    if let Some(stop) = faction_stop {
        stop.stop().await;
    }
    if let Some(task) = faction_run {
        task.abort();
    }
    if !graph.dispatcher.drain(DRAIN).await {
        tracing::warn!(
            in_flight = graph.dispatcher.in_flight(),
            "interactions still running at shutdown"
        );
    }
    graph.master_access.stop(Duration::from_secs(5)).await;
    graph.services.stop_all().await;
    monitoring.stop().await;
    graph.rcon.shutdown().await;
    tracing::info!("stopped");
    outcome
}
