//! The bot's entry point (C# `Program.Main`).
//!
//! `pavlov-bot` runs the bot. `pavlov-bot --selftest` builds the whole graph WITHOUT
//! contacting Discord, registers the background services without starting them, prints what
//! it built and exits 0: the deploy gate, which catches a wiring mistake before a restart.
//!
//! Exit codes: 0 stopped cleanly or selftest passed; 78 (`EX_CONFIG`) a configuration that
//! cannot be used; 1 anything else.

use std::process::ExitCode;
use std::sync::Arc;
use std::time::Instant;

use pavlov_bot::graph::{self, Surfaces};
use pavlov_bot::startup::{EX_CONFIG, settle};
use pavlov_bot::{run, services};
use pavlov_config::Config;
use pavlov_gateway::DiscordClient;
use pavlov_storage::installs::DiskProbe;
use tracing_subscriber::EnvFilter;

fn exit(code: i32) -> ExitCode {
    ExitCode::from(u8::try_from(code).unwrap_or(1))
}

/// `LOG_LEVEL` (trace, debug, info, warn, error), default info. Serenity's own chatter is
/// held at warn so a busy guild does not drown the bot's lines.
fn init_logging(level: Option<&str>) {
    let level = match level.map(|l| l.trim().to_ascii_lowercase()).as_deref() {
        Some(l @ ("trace" | "debug" | "info" | "warn" | "error")) => l.to_owned(),
        Some("warning") => "warn".to_owned(),
        _ => "info".to_owned(),
    };
    let filter = EnvFilter::new(format!("{level},serenity=warn,tracing::span=warn"));
    tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_target(false)
        .init();
}

fn main() -> ExitCode {
    let started = Instant::now();
    let selftest = std::env::args().skip(1).any(|a| a == "--selftest");
    let cwd = match std::env::current_dir() {
        Ok(cwd) => cwd,
        Err(error) => {
            eprintln!("cannot read the working directory: {error}");
            return exit(1);
        }
    };
    let config = match Config::load(&cwd.join(".env"), std::env::vars_os()) {
        Ok(config) => Arc::new(config),
        Err(error) => {
            eprintln!("{error}");
            return exit(EX_CONFIG);
        }
    };
    let probe = DiskProbe::default();
    let settled = match settle(&config, &cwd, &probe) {
        Ok(settled) => settled,
        Err(fatal) => {
            for line in fatal.lines {
                eprintln!("{line}");
            }
            return exit(EX_CONFIG);
        }
    };
    for note in &settled.notes {
        eprintln!("{note}");
    }
    init_logging(config.get("LOG_LEVEL"));

    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("cannot start the async runtime: {error}");
            return exit(1);
        }
    };
    runtime.block_on(async move {
        if selftest {
            let graph = match graph::build(&settled, config, &cwd, Surfaces::offline()) {
                Ok(graph) => graph,
                Err(error) => {
                    eprintln!("selftest: FAILED - {error}");
                    return exit(1);
                }
            };
            if let Err(error) = services::register(&graph) {
                eprintln!("selftest: FAILED - service registration: {error}");
                return exit(1);
            }
            for line in run::selftest_report(&graph, started) {
                println!("{line}");
            }
            return exit(0);
        }

        let main = match DiscordClient::build(settled.options.discord_token.expose()).await {
            Ok(client) => client,
            Err(error) => {
                tracing::error!(%error, "cannot build the Discord client");
                return exit(1);
            }
        };
        let faction = match settled
            .options
            .faction_token
            .as_ref()
            .filter(|_| settled.options.faction_bot_enabled())
        {
            Some(token) => match DiscordClient::build(token.expose()).await {
                Ok(client) => Some(client),
                Err(error) => {
                    tracing::error!(%error, "cannot build the whitelist bot's client; running without it");
                    None
                }
            },
            None => None,
        };
        let surfaces = Surfaces {
            poster: main.poster(),
            alerts: main.alert_poster(),
            renamer: main.renamer(),
            directory: main.directory(),
            members: main.members(),
            roles: main.roles(),
            guild_installed: main.guild_installed(),
        };
        let graph = match graph::build(&settled, config, &cwd, surfaces) {
            Ok(graph) => graph,
            Err(error) => {
                tracing::error!(%error, "cannot build the bot");
                return exit(1);
            }
        };
        run::log_startup(&graph, &cwd, &probe).await;
        match run::run(graph, main, faction).await {
            Ok(()) => exit(0),
            Err(error) => {
                tracing::error!("{error}");
                exit(1)
            }
        }
    })
}
