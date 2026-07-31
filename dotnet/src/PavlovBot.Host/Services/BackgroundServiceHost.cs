using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Rcon;
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
            Tick = _rcon.RefreshRostersAsync,
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

    public Task StopAsync(CancellationToken cancellationToken) => _registry.StopAllAsync();
}
