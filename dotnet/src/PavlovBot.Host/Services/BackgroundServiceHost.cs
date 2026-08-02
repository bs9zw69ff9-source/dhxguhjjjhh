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
        ILogger<BackgroundServiceHost> logger)
    {
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
                            await _tracking.IngestAsync(line, ct).ConfigureAwait(false);
                },
            });
        }

        // ---- bans ----
        _registry.Register(new ServiceDefinition
        {
            Name = "ban-expiry",
            Interval = _features.BanExpiryInterval,
            Tick = async ct =>
            {
                foreach (var lifted in await _bans.ProcessExpiredAsync(ct).ConfigureAwait(false))
                {
                    /* A served ban's flags linger until the clean-up runs, so without this
                       exemption the sweep re-catches them the moment they reconnect - and a
                       served temp ban silently becomes permanent. */
                    await _masters.ExemptAsync(lifted.PlayerId, ct: ct).ConfigureAwait(false);
                }
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
                Tick = ct => _moneyLog.TickAsync(ct),
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
