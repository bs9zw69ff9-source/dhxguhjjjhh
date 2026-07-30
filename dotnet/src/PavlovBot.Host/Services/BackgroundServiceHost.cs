using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;

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

    public BackgroundServiceHost(
        ServiceRegistry registry,
        RconRegistry rcon,
        BotOptions options,
        MetricsRegistry metrics,
        HealthRegistry health,
        ILogger<BackgroundServiceHost> logger)
    {
        _registry = registry;
        _rcon = rcon;
        _options = options;
        _metrics = metrics;
        _health = health;
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
