using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Services;

/// <summary>
/// Registers the bot's recurring work and drives the <see cref="ServiceRegistry"/> from the
/// host lifetime.
/// </summary>
/// <remarks>
/// This is the .NET equivalent of core/background-services.js: the one place that says WHAT
/// runs and HOW OFTEN, separate from the machinery that runs it. Only the services whose
/// underlying features are ported are registered - listing a service whose implementation
/// does not exist yet would report a green /health for work nobody is doing.
/// </remarks>
public sealed class BackgroundServiceHost : IHostedService
{
    private readonly ServiceRegistry _registry;
    private readonly RconRegistry _rcon;
    private readonly BotOptions _options;
    private readonly MetricsRegistry _metrics;
    private readonly HealthRegistry _health;
    private readonly ILogger<BackgroundServiceHost> _logger;
    private readonly FeatureOptions _features;
    private readonly LogTailer _tailer;
    private readonly IpTrackingService _tracking;
    private readonly BanService _bans;
    private readonly MasterNames _masters;
    private readonly MoneyLog _moneyLog;
    private readonly SqliteKeyValueBackend _backend;
    private readonly Boards _boards;
    private readonly AutoPost _autoPost;
    private readonly ModsaveBanlist _modsave;
    private readonly RosterService _rosters;
    private readonly PavlovBot.Core.Data.SerializedStore _store;
    /// <summary>
    /// Held only so the bridge is CONSTRUCTED. It does its work entirely through the events
    /// it subscribes to in its constructor, and a service that is registered but never
    /// resolved is never built - which is how the feeds were silent for weeks.
    /// </summary>
    private readonly PavlovBot.Host.Logs.FeedBridge _bridge;

    private readonly PavlovBot.Host.Logs.ServerLabels _servers;
    private readonly PavlovBot.Host.Discord.PlayerCountChannels _counts;
    private readonly PavlovBot.Host.Verification.VerificationService _verification;
    private readonly PavlovBot.Host.Discord.Commands.MenuPanel _menuPanel;
    private readonly PavlovBot.Host.Economy.Payroll _payroll;
    private readonly PavlovBot.Host.Economy.MoneyAnomalyDetector _moneyAlerts;
    private readonly PavlovBot.Host.Servers.CrashRecovery _crashRecovery;
    private readonly PavlovBot.Host.Moderation.AuditLog _audit;
    private readonly PavlovBot.Host.Events.IEventStore _events;

    /// <summary>
    /// Held only so the bridge is CONSTRUCTED, exactly like <see cref="_bridge"/> above. It
    /// works entirely through the tracker events it subscribes to in its constructor, and a
    /// singleton nothing resolves is a singleton nothing builds.
    /// </summary>
    private readonly PavlovBot.Host.Events.PlayerEventBridge _playerEvents;

    public BackgroundServiceHost(
        ServiceRegistry registry,
        RconRegistry rcon,
        BotOptions options,
        FeatureOptions features,
        MetricsRegistry metrics,
        HealthRegistry health,
        LogTailer tailer,
        IpTrackingService tracking,
        BanService bans,
        MasterNames masters,
        MoneyLog moneyLog,
        SqliteKeyValueBackend backend,
        Boards boards,
        AutoPost autoPost,
        ModsaveBanlist modsave,
        RosterService rosters,
        PavlovBot.Core.Data.SerializedStore store,
        PavlovBot.Host.Logs.FeedBridge bridge,
        PavlovBot.Host.Logs.ServerLabels servers,
        PavlovBot.Host.Discord.PlayerCountChannels counts,
        PavlovBot.Host.Verification.VerificationService verification,
        PavlovBot.Host.Discord.Commands.MenuPanel menuPanel,
        PavlovBot.Host.Economy.Payroll payroll,
        PavlovBot.Host.Economy.MoneyAnomalyDetector moneyAlerts,
        PavlovBot.Host.Servers.CrashRecovery crashRecovery,
        PavlovBot.Host.Moderation.AuditLog audit,
        PavlovBot.Host.Events.IEventStore events,
        PavlovBot.Host.Events.PlayerEventBridge playerEvents,
        ILogger<BackgroundServiceHost> logger)
    {
        _payroll = payroll;
        _moneyAlerts = moneyAlerts;
        _crashRecovery = crashRecovery;
        _audit = audit;
        _events = events;
        _playerEvents = playerEvents;
        _registry = registry;
        _rcon = rcon;
        _options = options;
        _features = features;
        _metrics = metrics;
        _health = health;
        _tailer = tailer;
        _tracking = tracking;
        _bans = bans;
        _masters = masters;
        _moneyLog = moneyLog;
        _backend = backend;
        _boards = boards;
        _autoPost = autoPost;
        _modsave = modsave;
        _rosters = rosters;
        _store = store;
        _bridge = bridge;
        _servers = servers;
        _counts = counts;
        _verification = verification;
        _menuPanel = menuPanel;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _registry.Register(new ServiceDefinition
        {
            Name = "rcon-health",
            Interval = _options.RconHealthInterval,
            Tick = ct => _rcon.ProbeAllAsync(ct),
            HealthCheck = _rcon.HealthAsync,
        });

        _registry.Register(new ServiceDefinition
        {
            Name = "player-cache",
            Interval = _options.PlayerCacheInterval,
            Tick = ct => _rcon.RefreshRostersAsync(ct),
            // Depends on rcon-health so the ordering in /health reads the way the system
            // actually layers, and so a future RCON-owning service can be stopped last.
            DependsOn = ["rcon-health"],
        });

        _registry.Register(new ServiceDefinition
        {
            Name = "runtime-metrics",
            Interval = TimeSpan.FromSeconds(15),
            RunOnStart = true,   // safe here: it touches no Discord surface
            Tick = _ =>
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                _metrics.Gauge("process_resident_bytes", process.WorkingSet64, help: "Resident set size in bytes");
                _metrics.Gauge("process_managed_heap_bytes", GC.GetTotalMemory(false), help: "Managed heap in bytes");
                _metrics.Gauge("process_threads", process.Threads.Count, help: "OS threads");
                _metrics.Gauge("process_uptime_seconds", (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
                    help: "Seconds since process start");
                for (var generation = 0; generation <= GC.MaxGeneration; generation++)
                    _metrics.Gauge("gc_collections_total", GC.CollectionCount(generation),
                        MetricLabels.Of("generation", generation.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        "Garbage collections by generation");
                return Task.CompletedTask;
            },
        });

        /* ---- log tailing ----
           1.5 seconds, because this is what catches a flagged join. A slower poll means a
           ban evader plays for that long before anything notices. */
        var logPaths = LogTailer.Discover(_features.LogPaths, _logger);

        /* Named BEFORE the tail starts, so the very first join line already says "Server 1"
           rather than "the server" - the discovery order IS the numbering. */
        _servers.Assign(logPaths);

        if (logPaths.Count > 0)
        {
            _registry.Register(new ServiceDefinition
            {
                Name = "log-tail",
                Interval = _features.LogPollInterval,
                Tick = async ct =>
                {
                    foreach (var path in logPaths)
                        foreach (var line in _tailer.Poll(path))
                        {
                            /* PER LINE, and this is not defensive padding. Poll() has
                               ALREADY advanced its file offset past this whole batch, so an
                               exception escaping one line silently discards every line
                               after it - joins, leaves, kills, address pairings - with no
                               way to ever get them back. The tick handler above catches and
                               logs, so the bot survived; the data did not.

                               One malformed line, or one downstream handler that throws,
                               must cost that line and nothing else. */
                            try
                            {
                                await _tracking.IngestAsync(line, ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                throw;   // shutting down
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex,
                                    "Failed to process a line of {Path} - skipping it and continuing. " +
                                    "The rest of this batch is unaffected", path);
                            }
                        }
                },
            });
        }

        // ---- bans ----
        _registry.Register(new ServiceDefinition
        {
            Name = "ban-expiry",
            Interval = _features.BanExpiryInterval,
            /* The exemption used to be granted HERE, and only here, which is why /unban never
               got one. BanService.LiftAsync now clears the flags and grants the exemption for
               every lift, so this tick is just the timer. */
            Tick = ct => _bans.ProcessExpiredAsync(ct),
        });

        /* ---- timeline retention ----
           HOURLY, not on a tick. Pruning is a DELETE over an indexed range and costs nothing
           at this cadence, and running it more often would only mean the same delete finding
           nothing more frequently. Registered unconditionally: with the timeline off the
           store is a no-op and this prunes zero rows. */
        _registry.Register(new ServiceDefinition
        {
            Name = "timeline-retention",
            Interval = TimeSpan.FromHours(1),
            Tick = async ct =>
            {
                if (_features.EventRetentionDays <= 0) return;

                await _events.PruneAsync(
                    DateTimeOffset.UtcNow - TimeSpan.FromDays(_features.EventRetentionDays), ct)
                    .ConfigureAwait(false);
            },
        });

        _registry.Register(new ServiceDefinition
        {
            Name = "ban-sweep",
            Interval = _features.BanSweepInterval,
            Tick = ct => _bans.EnforceSweepAsync(ct),
            DependsOn = ["player-cache"],   // it sweeps the roster that service refreshes
        });

        _registry.Register(new ServiceDefinition
        {
            Name = "ban-reconcile",
            Interval = _features.BanReconcileInterval,
            Tick = ct => _bans.ReconcileAsync(ct: ct),
        });

        // ---- economy ----
        if (_moneyLog.Enabled)
        {
            _registry.Register(new ServiceDefinition
            {
                Name = "money-log",
                Interval = _features.MoneyLogInterval,
                Tick = async ct =>
                {
                    var changes = await _moneyLog.TickAsync(ct).ConfigureAwait(false);

                    /* THE SAME DELTAS, READ TWICE. The money log posts them to a feed; the
                       detector sums them over a window. Hanging the detector off this tick
                       rather than giving it its own means it sees exactly what was reported,
                       with no second pass over the ledgers and no chance of the two
                       disagreeing about what changed. */
                    foreach (var alert in await _moneyAlerts.ObserveAsync(changes, ct).ConfigureAwait(false))
                    {
                        await _audit.RecordAsync("money-alert", "system", alert.Player,
                            $"earned {alert.Total:N0} across {alert.Events} credit(s) in " +
                            $"{alert.Window.TotalMinutes:0} minutes", ct).ConfigureAwait(false);
                    }
                },
            });
        }

        if (_payroll.Enabled)
        {
            /* TICKS FAR MORE OFTEN THAN IT PAYS, and that is the design. Payroll decides for
               itself whether a period has elapsed, from a PERSISTED timestamp - so a bot
               restart, a supervisor revival or a slow tick cannot skip a period or pay one
               twice. A service interval that WAS the pay period would do both. */
            _registry.Register(new ServiceDefinition
            {
                Name = "payroll",
                Interval = TimeSpan.FromMinutes(1),
                Tick = async ct =>
                {
                    var run = await _payroll.RunAsync(ct).ConfigureAwait(false);

                    /* Only the SETTLEMENT is logged to staff. Accrual happens every period
                       for everyone on duty and moves no money - auditing it would bury the
                       entries that did. */
                    if (run.Paid.Count == 0) return;

                    await _audit.RecordAsync("payroll", "system", $"{run.Paid.Count} {run.Faction}",
                        $"banked {run.Total:N0} in wages earned on duty", ct).ConfigureAwait(false);
                },
                DependsOn = ["player-cache"],   // it pays whoever that service says is online
            });
        }

        /* ---- discord surfaces ----
           NO RunOnStart on any board. Services start BEFORE the gateway connects, so an
           immediate tick fires with no connection - and re-fires on every supervisor
           restart. The Node bot shipped a leaderboard regression exactly this way. */
        _registry.Register(new ServiceDefinition
        {
            Name = "playtime",
            Interval = TimeSpan.FromMinutes(1),
            Tick = ct => _boards.TickPlaytimeAsync(TimeSpan.FromMinutes(1), ct),
            DependsOn = ["player-cache"],
        });

        if (_features.LeaderboardChannel is not null)
        {
            _registry.Register(new ServiceDefinition
            {
                Name = "leaderboard",
                Interval = _features.LeaderboardInterval,
                // CASH, not playtime. LEADERBOARD_CHANNEL is the money board - see
                // Boards.BuildCashBoard.
                Tick = ct => _autoPost.PostAsync("leaderboard", _features.LeaderboardChannel,
                    () => Task.FromResult(_boards.BuildCashBoard()), ct),
            });
        }

        if (_features.ArrestBoardChannel is not null)
        {
            _registry.Register(new ServiceDefinition
            {
                Name = "arrest-board",
                Interval = _features.LeaderboardInterval,
                Tick = ct => _autoPost.PostAsync("arrests", _features.ArrestBoardChannel,
                    () => Task.FromResult(_boards.BuildArrestBoard()), ct),
            });
        }

        if (_features.WarrantBoardChannel is not null)
        {
            _registry.Register(new ServiceDefinition
            {
                Name = "warrant-board",
                Interval = _features.LeaderboardInterval,
                Tick = ct => _autoPost.PostAsync("warrants", _features.WarrantBoardChannel,
                    () => Task.FromResult(_boards.BuildWarrantBoard()), ct),
            });
        }

        if (_features.PlayerCountChannels.Count > 0 || _features.ShackTotalChannel is not null)
        {
            /* FIVE MINUTES, and the interval is load-bearing. Discord allows two channel
               renames per ten minutes PER CHANNEL, so this sits exactly ON the limit rather
               than under it - chosen deliberately for freshness. The cost is no headroom: a
               retry or a little clock skew puts a tick over, and Discord.Net WAITS OUT a
               rename rate limit rather than failing, so the effect is a late name and not a
               lost one. Unchanged names are not written at all, which is what keeps an idle
               server from spending the allowance it would need when the count moves. */
            _registry.Register(new ServiceDefinition
            {
                Name = "player-count-channels",
                Interval = TimeSpan.FromMinutes(5),
                Tick = ct => _counts.TickAsync(
                    new PavlovBot.Host.Discord.PlayerCountChannels.Targets(
                        _features.PlayerCountChannels, _features.ShackTotalChannel), ct),
                DependsOn = ["player-cache"],
            });
        }

        if (_features.CrashRecovery)
        {
            /* ONE MINUTE. Fast enough that a crash at 4am is a two-minute outage rather than
               a morning one, slow enough that the systemctl calls are nothing. The brake is
               inside CrashRecovery, not here - a shorter interval cannot turn into a restart
               storm, because the attempt cap is measured in wall time. */
            _registry.Register(new ServiceDefinition
            {
                Name = "crash-recovery",
                Interval = TimeSpan.FromMinutes(1),
                Tick = async ct =>
                {
                    var sweep = await _crashRecovery.SweepAsync(ct).ConfigureAwait(false);

                    foreach (var unit in sweep.Restarted)
                    {
                        await _audit.RecordAsync("auto-restart", "system", unit,
                            "the unit was in the failed state and was restarted automatically", ct)
                            .ConfigureAwait(false);
                    }

                    foreach (var (unit, reason) in sweep.Suppressed)
                    {
                        await _audit.RecordAsync("auto-restart-stopped", "system", unit, reason, ct)
                            .ConfigureAwait(false);
                    }
                },
            });
        }

        /* ---- rank suspensions ----
           The suspension records where to put them BACK. Restoring to the default rank
           would quietly strip every suspended officer of everything they had earned. */
        _registry.Register(new ServiceDefinition
        {
            Name = "rank-restore",
            Interval = TimeSpan.FromSeconds(30),
            Tick = RestoreExpiredRanksAsync,
        });

        if (_modsave.Enabled)
        {
            _registry.Register(new ServiceDefinition
            {
                Name = "banlist-sync",
                Interval = TimeSpan.FromMinutes(5),
                // Import THEN export. Exporting first rewrites the file from the store and
                // destroys the in-game entries before they have been read.
                Tick = ct => _modsave.SyncAsync(ct),
            });
        }

        /* ---- verification panel ----
           An autopost board, so a restart EDITS the existing message instead of leaving an
           orphan with a dead button behind it. Slow, because the panel's content never
           changes - this only exists to put it back if somebody deletes it. */
        if (_features.VerifyChannel is not null && _verification.Enabled)
        {
            _registry.Register(new ServiceDefinition
            {
                Name = "verify-panel",
                Interval = TimeSpan.FromMinutes(10),
                Tick = ct => _autoPost.PostAsync("verifypanel", _features.VerifyChannel,
                    () => Task.FromResult<global::Discord.Embed?>(_verification.BuildPanel()),
                    ct, PavlovBot.Host.Verification.VerificationService.PanelButton()),
            });
        }

        /* ---- menu panel ----
           Same shape as the verification panel: an autopost board so a restart edits it
           rather than leaving an orphan with a dead button. */
        if (_menuPanel.Enabled)
        {
            _registry.Register(new ServiceDefinition
            {
                Name = "menu-panel",
                Interval = TimeSpan.FromMinutes(10),
                Tick = ct => _autoPost.PostAsync("menupanel", _features.MenuPanelChannel,
                    () => Task.FromResult<global::Discord.Embed?>(_menuPanel.BuildPanel()),
                    ct, PavlovBot.Host.Discord.Commands.MenuPanel.PanelButton()),
            });
        }

        // ---- persistence ----
        _registry.Register(new ServiceDefinition
        {
            Name = "json-export",
            Interval = TimeSpan.FromMinutes(15),
            Tick = _ =>
            {
                // Keeps the human-readable backup CURRENT. A file that looks like a backup
                // and is six months stale is worse than not having one.
                _backend.ExportToJson(_options.DataDirectory);
                return Task.CompletedTask;
            },
        });

        /* The supervisor restarts services that have wedged. It is itself a service, which
           means it is subject to the same overlap protection and failure counting - a
           supervisor that can silently die is not a supervisor. */
        _registry.Register(new ServiceDefinition
        {
            Name = "service-supervisor",
            Interval = _options.SupervisorInterval,
            Tick = async ct =>
            {
                /* LAYER 4 - RE-VERIFIED WHILE RUNNING, not only at startup. Patching the
                   startup gate is the obvious single edit, so the guard is checked again on
                   every supervisor tick. A tampered build that got past boot fails here, and
                   fails LOUDLY: this throws, so the supervisor records it, /health shows it,
                   and it keeps failing every minute rather than once. */
                PavlovBot.Core.Security.OwnerGuard.Verify();

                var revived = await _registry.ReviveFailedAsync(ct: ct).ConfigureAwait(false);
                if (revived.Count > 0) _logger.LogWarning("Revived services: {Names}", string.Join(", ", revived));
            },
        });

        _health.Register("services", _registry.HealthAsync);
        _health.Register("rcon", _rcon.HealthAsync);

        await _registry.StartAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Put back the rank of anyone whose suspension has run out.</summary>
    private async Task RestoreExpiredRanksAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var suspensions = _store.Read(Datasets.RankSuspensions,
            new Dictionary<string, RankSuspension>(StringComparer.OrdinalIgnoreCase));

        var due = suspensions.Values.Where(s => s.Until <= now).ToList();
        if (due.Count == 0) return;

        foreach (var suspension in due)
        {
            var faction = PavlovBot.Core.Factions.FactionRegistry.Get(suspension.Faction);
            if (faction is null) continue;

            /* Re-add at the entry rank, then promote to where they were. Writing straight
               into the target rank file would skip the cap check, so a rank that filled up
               while they were suspended would silently overflow. */
            await _rosters.JoinAsync(faction, suspension.Player, ct).ConfigureAwait(false);

            var target = faction.IndexOf(suspension.RestoreTo);
            for (var step = faction.IndexOf(faction.Default); step < target; step++)
            {
                var decision = await _rosters.ChangeRankAsync(faction, suspension.Player, +1, ct).ConfigureAwait(false);
                if (!decision.IsAllowed)
                {
                    _logger.LogWarning(
                        "Could not fully restore {Player} to {Rank}: {Outcome}. They are at a lower rank and need a manual promotion.",
                        suspension.Player, suspension.RestoreTo, decision.Outcome);
                    break;
                }
            }

            _logger.LogInformation("Rank suspension served: {Player} restored to {Rank}", suspension.Player, suspension.RestoreTo);
        }

        await _store.UpdateAsync(Datasets.RankSuspensions,
            new Dictionary<string, RankSuspension>(StringComparer.OrdinalIgnoreCase),
            current =>
            {
                foreach (var suspension in due) current.Remove(suspension.Player);
                return current;
            }, ct).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _registry.StopAllAsync();
}
