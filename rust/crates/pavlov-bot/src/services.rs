//! The background services (C# `Services/BackgroundServiceHost.cs`), registered in its order
//! with its intervals, dependencies and gates. Each tick is one bounded, never-reentered run;
//! the registry counts failures and the supervisor revives what keeps failing.
//!
//! NO BOARD RUNS ON START: services start before the gateway connects, so an immediate tick
//! would post into a dead connection (the Node bot shipped a leaderboard regression that way).

use std::future::Future;
use std::sync::Arc;
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::security::OwnerGuard;
use pavlov_core::text::number::group_thousands;
use pavlov_discord::boards::build_kd_board;
use pavlov_discord::verification::{panel, panel_button};
use pavlov_host::factions::rank_restorer::RankRestorer;
use pavlov_host::services::{RegistryError, ServiceDefinition, TickError, Work};

use crate::graph::Graph;

/// Ticks in a row before the supervisor restarts a service (C# `ReviveFailedAsync` default).
pub const REVIVE_THRESHOLD: u32 = 5;

fn work<F, Fut>(tick: F) -> Work
where
    F: Fn() -> Fut + Send + Sync + 'static,
    Fut: Future<Output = Result<(), TickError>> + Send + 'static,
{
    Arc::new(move || -> BoxFuture<'static, Result<(), TickError>> { Box::pin(tick()) })
}

fn every(name: &str, interval: Duration, tick: Work) -> ServiceDefinition {
    ServiceDefinition {
        interval: Some(interval),
        ..ServiceDefinition::new(name, tick)
    }
}

fn after(mut definition: ServiceDefinition, dependency: &str) -> ServiceDefinition {
    definition.depends_on.push(dependency.to_owned());
    definition
}

/// Post one board through the autoposter.
fn board<B, Fut>(graph: &Graph, key: &'static str, channel: Option<u64>, build: B) -> Work
where
    B: Fn() -> Fut + Send + Sync + 'static,
    Fut: Future<Output = Option<Embed>> + Send + 'static,
{
    let autopost = Arc::clone(&graph.autopost);
    let build = Arc::new(build);
    work(move || {
        let autopost = Arc::clone(&autopost);
        let build = Arc::clone(&build);
        async move {
            autopost
                .post(
                    key,
                    channel,
                    async move { Ok::<_, String>(build().await) },
                    Vec::new(),
                )
                .await;
            Ok(())
        }
    })
}

/// Register every service.
///
/// # Errors
/// A duplicate name or an unknown dependency: a wiring bug, refused at startup.
#[allow(clippy::too_many_lines)]
pub fn register(graph: &Graph) -> Result<(), RegistryError> {
    let registry = &graph.services;
    let features = &graph.features;
    let options = &graph.options;

    let rcon = Arc::clone(&graph.rcon);
    let probe_rcon = Arc::clone(&rcon);
    let mut rcon_health = every(
        "rcon-health",
        options.rcon_health_interval,
        work(move || {
            let rcon = Arc::clone(&probe_rcon);
            async move {
                rcon.probe_all().await;
                Ok(())
            }
        }),
    );
    let health_rcon = Arc::clone(&rcon);
    rcon_health.health_check = Some(Arc::new(move || {
        let rcon = Arc::clone(&health_rcon);
        Box::pin(async move { Ok(rcon.health().await) })
    }));
    registry.register(rcon_health)?;

    let roster_rcon = Arc::clone(&rcon);
    registry.register(after(
        every(
            "player-cache",
            options.player_cache_interval,
            work(move || {
                let rcon = Arc::clone(&roster_rcon);
                async move {
                    rcon.refresh_rosters().await;
                    Ok(())
                }
            }),
        ),
        "rcon-health",
    ))?;

    if features.monitoring_enabled {
        let monitor = Arc::clone(&graph.monitor);
        registry.register(after(
            every(
                "server-monitor",
                features.monitor_settings.check_interval,
                work(move || {
                    let monitor = Arc::clone(&monitor);
                    async move {
                        monitor.tick().await;
                        Ok(())
                    }
                }),
            ),
            "rcon-health",
        ))?;
        if features.monitor_board_enabled && features.monitor_alert_channel.is_some() {
            let board_source = Arc::clone(&graph.monitor_board);
            registry.register(after(
                every(
                    "monitor-board",
                    features.monitor_settings.check_interval,
                    board(
                        graph,
                        "monitor-board",
                        features.monitor_alert_channel,
                        move || {
                            let source = Arc::clone(&board_source);
                            async move { Some(source.build().await) }
                        },
                    ),
                ),
                "server-monitor",
            ))?;
        }
    }

    let metrics = Arc::clone(&graph.metrics);
    registry.register(ServiceDefinition {
        run_on_start: true, // touches no Discord surface
        ..every(
            "runtime-metrics",
            Duration::from_secs(15),
            work(move || {
                let metrics = Arc::clone(&metrics);
                async move {
                    crate::run::record_process_metrics(&metrics, &pavlov_host::self_stats::read());
                    Ok(())
                }
            }),
        )
    })?;

    if graph.features.install_roots.len() > graph.log_paths.len() {
        tracing::warn!(
            tailed = graph.log_paths.len(),
            installs = graph.features.install_roots.len(),
            "tailing fewer logs than there are Pavlov installs: the feeds (joins, kills, \
             connections) will be blank for the servers with no log. Set PAVLOV_LOGS to every \
             install's Pavlov.log, or check the missing server is running and writing its log"
        );
    }
    if !graph.log_paths.is_empty() {
        let tailer = Arc::clone(&graph.tailer);
        let tracking = Arc::clone(&graph.tracking);
        let paths = graph.log_paths.clone();
        registry.register(ServiceDefinition {
            // Lines are consumed before they are processed, so a tick abandoned at its budget
            // loses its batch: the budget is a backstop against a hang, not a deadline.
            timeout: Some(Duration::from_secs(180)),
            ..every(
                "log-tail",
                features.log_poll_interval,
                work(move || {
                    let tailer = Arc::clone(&tailer);
                    let tracking = Arc::clone(&tracking);
                    let paths = paths.clone();
                    async move {
                        for path in &paths {
                            // One line cannot cost the rest of the batch: `ingest` answers
                            // every failure itself and never unwinds past a line.
                            for line in tailer.poll(path, false) {
                                tracking.ingest(&line).await;
                            }
                        }
                        Ok(())
                    }
                }),
            )
        })?;
    }
    if graph.stats_log.enabled() {
        let stats = Arc::clone(&graph.stats_log);
        registry.register(every(
            "stats-log",
            features.log_poll_interval,
            work(move || {
                let stats = Arc::clone(&stats);
                async move {
                    stats.tick().await;
                    Ok(())
                }
            }),
        ))?;
    }

    let bans = Arc::clone(&graph.bans);
    let expiring = Arc::clone(&bans);
    registry.register(every(
        "ban-expiry",
        features.ban_expiry_interval,
        work(move || {
            let bans = Arc::clone(&expiring);
            async move {
                bans.process_expired().await;
                Ok(())
            }
        }),
    ))?;

    // Registered even with FIREWALL_BANS off: a pass then lifts every rule it added.
    let firewall = Arc::clone(&graph.ban_firewall);
    registry.register(ServiceDefinition {
        run_on_start: true,
        ..every(
            "ban-firewall",
            Duration::from_secs(30),
            work(move || {
                let firewall = Arc::clone(&firewall);
                async move {
                    let pass = firewall.sync().await;
                    if pass.denied + pass.lifted > 0 {
                        tracing::info!(
                            denied = pass.denied,
                            lifted = pass.lifted,
                            failed = pass.failed,
                            "ban firewall"
                        );
                    }
                    Ok(())
                }
            }),
        )
    })?;

    let events = Arc::clone(&graph.events);
    let retention = features.event_retention_days;
    let clock = Arc::clone(&graph.clock);
    registry.register(every(
        "timeline-retention",
        Duration::from_secs(3600),
        work(move || {
            let events = Arc::clone(&events);
            let now = clock.now();
            async move {
                if retention == 0 {
                    return Ok(());
                }
                let cutoff = now - chrono::Duration::days(i64::from(retention));
                events
                    .prune(cutoff)
                    .map(|_| ())
                    .map_err(|e| TickError::new(e.to_string()))
            }
        }),
    ))?;

    let sweeping = Arc::clone(&bans);
    registry.register(after(
        every(
            "ban-sweep",
            features.ban_sweep_interval,
            work(move || {
                let bans = Arc::clone(&sweeping);
                async move {
                    bans.enforce_sweep().await;
                    Ok(())
                }
            }),
        ),
        "player-cache",
    ))?;

    if graph.payroll.enabled() {
        let payroll = Arc::clone(&graph.payroll);
        let audit = Arc::clone(&graph.audit);
        registry.register(after(
            every(
                "payroll",
                Duration::from_secs(60),
                work(move || {
                    let payroll = Arc::clone(&payroll);
                    let audit = Arc::clone(&audit);
                    async move {
                        let run = payroll.run().await;
                        if run.paid.is_empty() {
                            return Ok(());
                        }
                        audit
                            .record(
                                "payroll",
                                "system",
                                &format!("{} {}", run.paid.len(), run.faction),
                                Some(&format!(
                                    "banked {} in wages earned on duty",
                                    group_thousands(run.total())
                                )),
                            )
                            .await
                            .map_err(|e| TickError::new(e.to_string()))
                    }
                }),
            ),
            "player-cache",
        ))?;
    }

    let kills = Arc::clone(&graph.kill_stats);
    let flush_on_stop = Arc::clone(&kills);
    registry.register(ServiceDefinition {
        // A clean shutdown must not throw away the last minute of a firefight.
        on_stop: Some(work(move || {
            let kills = Arc::clone(&flush_on_stop);
            async move {
                kills
                    .flush()
                    .await
                    .map_err(|e| TickError::new(e.to_string()))
            }
        })),
        ..every(
            "kill-stats",
            Duration::from_secs(60),
            work(move || {
                let kills = Arc::clone(&kills);
                async move {
                    kills
                        .flush()
                        .await
                        .map_err(|e| TickError::new(e.to_string()))
                }
            }),
        )
    })?;

    let playtime = Arc::clone(&graph.boards);
    registry.register(after(
        every(
            "playtime",
            Duration::from_secs(60),
            work(move || {
                let boards = Arc::clone(&playtime);
                async move {
                    boards
                        .tick_playtime(chrono::TimeDelta::minutes(1))
                        .await
                        .map_err(|e| TickError::new(e.to_string()))
                }
            }),
        ),
        "player-cache",
    ))?;

    let interval = features.leaderboard_interval;
    if features.leaderboard_channel.is_some() {
        let boards = Arc::clone(&graph.boards);
        registry.register(every(
            "leaderboard",
            interval,
            board(
                graph,
                "leaderboard",
                features.leaderboard_channel,
                move || {
                    let boards = Arc::clone(&boards);
                    async move { Some(boards.build_cash_board()) }
                },
            ),
        ))?;
    }
    if features.arrest_board_channel.is_some() {
        let boards = Arc::clone(&graph.boards);
        registry.register(every(
            "arrest-board",
            interval,
            board(graph, "arrests", features.arrest_board_channel, move || {
                let boards = Arc::clone(&boards);
                async move { Some(boards.build_arrest_board()) }
            }),
        ))?;
    }
    if features.kd_board_channel.is_some() {
        let boards = Arc::clone(&graph.boards);
        let kills = Arc::clone(&graph.kill_stats);
        let rosters = Arc::clone(&graph.rosters);
        let clock = Arc::clone(&graph.clock);
        registry.register(every(
            "kd-board",
            interval,
            board(graph, "kd", features.kd_board_channel, move || {
                let embed = build_kd_board(
                    &kills.all(),
                    &boards.playtime(),
                    &rosters.affiliations(),
                    clock.now(),
                );
                async move { Some(embed) }
            }),
        ))?;
    }
    if features.warrant_board_channel.is_some() {
        let boards = Arc::clone(&graph.boards);
        registry.register(every(
            "warrant-board",
            interval,
            board(
                graph,
                "warrants",
                features.warrant_board_channel,
                move || {
                    let boards = Arc::clone(&boards);
                    async move { Some(boards.build_warrant_board()) }
                },
            ),
        ))?;
    }
    if features.player_board_channel.is_some() {
        let boards = Arc::clone(&graph.boards);
        registry.register(after(
            every(
                "player-board",
                interval,
                board(graph, "players", features.player_board_channel, move || {
                    let boards = Arc::clone(&boards);
                    async move { boards.build_player_board() }
                }),
            ),
            "player-cache",
        ))?;
    }

    if !features.player_count_channels.is_empty() || features.shack_total_channel.is_some() {
        let counts = Arc::clone(&graph.counts);
        let targets = pavlov_discord::player_count::Targets {
            server_channels: features.player_count_channels.clone(),
            total_channel: features.shack_total_channel,
        };
        // Exactly ON Discord's limit (two renames per channel per ten minutes), for freshness;
        // unchanged names are never written, so an idle server spends nothing.
        registry.register(after(
            every(
                "player-count-channels",
                Duration::from_secs(300),
                work(move || {
                    let counts = Arc::clone(&counts);
                    let targets = targets.clone();
                    async move {
                        counts.tick(&targets).await;
                        Ok(())
                    }
                }),
            ),
            "player-cache",
        ))?;
    }

    if features.crash_recovery {
        let recovery = Arc::clone(&graph.crash_recovery);
        let audit = Arc::clone(&graph.audit);
        registry.register(every(
            "crash-recovery",
            Duration::from_secs(60),
            work(move || {
                let recovery = Arc::clone(&recovery);
                let audit = Arc::clone(&audit);
                async move {
                    let sweep = recovery.sweep().await;
                    for unit in &sweep.restarted {
                        audit
                            .record(
                                "auto-restart",
                                "system",
                                unit,
                                Some("the unit was in the failed state and was restarted automatically"),
                            )
                            .await
                            .map_err(|e| TickError::new(e.to_string()))?;
                    }
                    for (unit, reason) in &sweep.suppressed {
                        audit
                            .record("auto-restart-stopped", "system", unit, Some(reason))
                            .await
                            .map_err(|e| TickError::new(e.to_string()))?;
                    }
                    Ok(())
                }
            }),
        ))?;
    }

    let backup = Arc::clone(&graph.whitelist_backup);
    registry.register(ServiceDefinition {
        run_on_start: true,
        ..every(
            "whitelist-backup",
            Duration::from_secs(3600),
            work(move || {
                let backup = Arc::clone(&backup);
                async move {
                    backup.auto_save().await;
                    Ok(())
                }
            }),
        )
    })?;

    let restorer = Arc::new(RankRestorer::new(
        Arc::clone(&graph.store),
        Arc::clone(&graph.rosters),
    ));
    let restore_clock = Arc::clone(&graph.clock);
    registry.register(every(
        "rank-restore",
        Duration::from_secs(30),
        work(move || {
            let restorer = Arc::clone(&restorer);
            let now = restore_clock.now();
            async move {
                restorer.restore_expired(now).await;
                Ok(())
            }
        }),
    ))?;

    if graph.mod_save_sync.can_sync() {
        let sync = Arc::clone(&graph.mod_save_sync);
        // No run on start: the first sweep needs player-cache to know who is online, and with
        // none yet it would fail open and mirror a ledger whose player is still moving it.
        registry.register(every(
            "modsave-sync",
            Duration::from_secs(60),
            work(move || {
                let sync = Arc::clone(&sync);
                async move {
                    tokio::task::spawn_blocking(move || sync.sweep())
                        .await
                        .map(|_| ())
                        .map_err(|e| TickError::new(e.to_string()))
                }
            }),
        ))?;
    }

    if features.verify_channel.is_some() && graph.verification.enabled() {
        registry.register(every("verify-panel", Duration::from_secs(600), {
            let autopost = Arc::clone(&graph.autopost);
            let channel = features.verify_channel;
            work(move || {
                let autopost = Arc::clone(&autopost);
                async move {
                    autopost
                        .post(
                            "verifypanel",
                            channel,
                            async { Ok::<_, String>(Some(panel())) },
                            panel_button(),
                        )
                        .await;
                    Ok(())
                }
            })
        }))?;
    }

    let backend = Arc::clone(&graph.backend);
    let store = Arc::clone(&graph.store);
    let data = options.data_directory.clone();
    registry.register(every(
        "json-export",
        Duration::from_secs(900),
        work(move || {
            let backend = Arc::clone(&backend);
            let unreadable = store.unreadable();
            let data = data.clone();
            async move {
                tokio::task::spawn_blocking(move || backend.export_to_json(&data, &unreadable))
                    .await
                    .map_err(|e| TickError::new(e.to_string()))?
                    .map(|_| ())
                    .map_err(|e| TickError::new(e.to_string()))
            }
        }),
    ))?;

    // A supervisor that could silently die is not one: it is a service like the rest.
    let supervised = Arc::clone(&graph.services);
    registry.register(every(
        "service-supervisor",
        options.supervisor_interval,
        work(move || {
            let registry = Arc::clone(&supervised);
            async move {
                // Checked again every tick: a tampered build that got past boot fails here,
                // LOUDLY, every minute.
                OwnerGuard::verify().map_err(|e| TickError::new(e.to_string()))?;
                let revived = registry.revive_failed(REVIVE_THRESHOLD).await;
                if !revived.is_empty() {
                    tracing::warn!(services = %revived.join(", "), "revived services");
                }
                Ok(())
            }
        }),
    ))?;

    let services_health = Arc::clone(&graph.services);
    graph.health.register(
        "services",
        false,
        Arc::new(move || {
            let registry = Arc::clone(&services_health);
            Box::pin(async move { registry.health().await })
        }),
    );
    let rcon_health = Arc::clone(&graph.rcon);
    graph.health.register(
        "rcon",
        false,
        Arc::new(move || {
            let rcon = Arc::clone(&rcon_health);
            Box::pin(async move { rcon.health().await })
        }),
    );
    Ok(())
}
