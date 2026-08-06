using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PavlovBot.Host.Observability;

namespace PavlovBot.Host.Services;

public enum ServiceState { Stopped, Starting, Running, Failed }

/// <param name="Interval">How often <see cref="Tick"/> runs. Null for a start/stop-only service.</param>
/// <param name="RunOnStart">
/// Fire one tick immediately on start. Off by default, and that default is load-bearing:
/// services start BEFORE the Discord gateway connects, so an immediate tick on anything
/// that posts to Discord fires with no connection - and re-fires on every supervisor
/// restart. The Node bot shipped a leaderboard regression exactly this way.
/// </param>
/// <param name="Critical">
/// When true, a failure to START aborts host startup. Everything else comes up with one
/// feature dark rather than not at all.
/// </param>
/// <param name="DependsOn">Services that must be running first.</param>
public sealed record ServiceDefinition
{
    public required string Name { get; init; }
    public required Func<CancellationToken, Task> Tick { get; init; }
    public TimeSpan? Interval { get; init; }
    public bool RunOnStart { get; init; }
    public bool Critical { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public Func<CancellationToken, Task>? OnStart { get; init; }
    public Func<Task>? OnStop { get; init; }
    public Func<CancellationToken, Task<HealthResult>>? HealthCheck { get; init; }

    /// <summary>
    /// How long one tick may run before it is abandoned. Null means the default below.
    /// </summary>
    /// <remarks>
    /// A TICK WITH NO TIMEOUT IS A SERVICE THAT CAN STOP FOREVER WITHOUT SAYING SO, and
    /// that is not theoretical - it is what "it's been 7 minutes and the tick hasn't
    /// happened" looks like from the outside. The loop awaits Tick before waiting on the
    /// timer again, so a tick that never returns means no further ticks, ever. The state
    /// stays Running, LastTickAt stays frozen at whenever it started, ConsecutiveFailures
    /// stays 0 - so <see cref="ServiceRegistry.ReviveFailedAsync"/>, which looks at exactly
    /// those two things, never revives it. Even the overrun counter is in a finally that a
    /// hung tick never reaches.
    ///
    /// Every tick in this bot reaches the network - RCON, the Discord API, a VPN provider,
    /// the Pavlov master list - so every one of them can hang on something that will never
    /// answer. A timeout converts a permanent silent stall into a logged, counted failure
    /// that the supervisor can act on.
    /// </remarks>
    public TimeSpan? Timeout { get; init; }
}

/// <param name="At">When it happened, so an old failure is not read as a current one.</param>
/// <param name="Message">What actually went wrong.</param>
public sealed record ServiceFailure(DateTimeOffset At, string Message);

/// <param name="RecentErrors">The last few failures, newest first. Empty when there are none.</param>
public sealed record ServiceStatus(
    string Name,
    ServiceState State,
    TimeSpan Uptime,
    TimeSpan? Interval,
    long Ticks,
    long Failures,
    int ConsecutiveFailures,
    long Overruns,
    string? LastError,
    DateTimeOffset? LastTickAt,
    double? LastDurationMs,
    IReadOnlyList<ServiceFailure>? RecentErrors = null)
{
    public IReadOnlyList<ServiceFailure> Errors => RecentErrors ?? [];
}

/// <summary>
/// Lifecycle and supervision for the bot's recurring work.
/// </summary>
/// <remarks>
/// Every one of these was a bare <c>setInterval</c> in the Node bot. Registering them buys
/// four things without changing what any of them does:
///
///   - a throwing tick is caught, counted and reported instead of becoming an unhandled
///     rejection that can take the process down
///   - a slow tick is never re-entered, so a stalled RCON sweep cannot stack up copies of
///     itself until the runtime drowns
///   - each one can be stopped, restarted and inspected via /health and /metrics
///   - shutdown stops them deterministically rather than leaving them mid-flight
///
/// ONE DELIBERATE DIFFERENCE FROM THE NODE VERSION. Node used a timer plus a `busy` flag
/// and counted the ticks it skipped. Here each service is a loop over a
/// <see cref="PeriodicTimer"/>, so re-entrancy is impossible by construction rather than
/// by a flag somebody has to remember to check. Missed ticks collapse into one instead of
/// being counted as skips, so the skip counter is replaced by an OVERRUN counter: ticks
/// that took longer than their own interval. That is the signal you actually alert on, and
/// unlike the skip count it cannot be wrong.
/// </remarks>
public sealed class ServiceRegistry : IAsyncDisposable
{
    private sealed class Record
    {
        public required ServiceDefinition Definition { get; init; }
        public ServiceState State = ServiceState.Stopped;
        public DateTimeOffset? StartedAt;
        public long Ticks;
        public long Failures;
        public int ConsecutiveFailures;
        public long Overruns;
        public string? LastError;

        /// <summary>When this service was restarted by the supervisor, newest last.</summary>
        /// <remarks>
        /// Bounded by pruning to the window on every check, so it cannot grow: a service
        /// restarted forever would otherwise accumulate one timestamp per restart for the
        /// life of the process.
        /// </remarks>
        public readonly List<DateTimeOffset> RestartsAt = [];

        /// <summary>Set once when the supervisor stops trying, so it is said once and shown in health.</summary>
        public bool RestartsExhausted;

        /// <summary>
        /// The last few failures, newest last. Bounded, and bounded deliberately small.
        /// </summary>
        /// <remarks>
        /// A COUNT IS NOT A DIAGNOSIS. "12 failed" says something is wrong and nothing about
        /// what, and the message was thrown away the moment the next failure overwrote
        /// LastError - so a service that failed twelve times for two different reasons
        /// looked identical to one that failed twelve times for one.
        ///
        /// Five, not fifty: this is read in a Discord embed with a hard character limit, and
        /// the useful question is "what is going wrong now", not "what has ever gone wrong".
        /// </remarks>
        public readonly Queue<ServiceFailure> Recent = new();

        public DateTimeOffset? LastTickAt;
        public double? LastDurationMs;
        public CancellationTokenSource? Stopping;
        public Task? Loop;
    }

    private readonly ConcurrentDictionary<string, Record> _services = new(StringComparer.Ordinal);
    private readonly MetricsRegistry _metrics;
    private readonly ILogger _logger;

    public ServiceRegistry(MetricsRegistry metrics, ILogger logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public ServiceRegistry Register(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        if (!_services.TryAdd(definition.Name, new Record { Definition = definition }))
            throw new InvalidOperationException($"service \"{definition.Name}\" is already registered");
        return this;
    }

    public int Count => _services.Count;
    public int Running => _services.Values.Count(r => r.State == ServiceState.Running);
    public IReadOnlyCollection<string> Names => _services.Keys.ToList();

    /// <summary>
    /// Start order honouring <see cref="ServiceDefinition.DependsOn"/>.
    /// </summary>
    /// <remarks>
    /// Throws on a cycle, NAMING the participants. "Stack overflow" at 3am is not a
    /// diagnosis. An unknown dependency is reported rather than silently ignored, because
    /// a typo'd dependency name that quietly does nothing is worse than a startup failure.
    /// </remarks>
    public static IReadOnlyList<ServiceDefinition> OrderByDependencies(IReadOnlyCollection<ServiceDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var byName = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = visiting, 2 = done
        var ordered = new List<ServiceDefinition>();
        var path = new List<string>();

        void Visit(string name, string? requiredBy)
        {
            if (state.TryGetValue(name, out var s))
            {
                if (s == 2) return;
                throw new InvalidOperationException(
                    $"circular service dependency: {string.Join(" -> ", path.Append(name))}");
            }
            if (!byName.TryGetValue(name, out var definition))
                throw new InvalidOperationException(
                    $"service \"{requiredBy ?? "?"}\" depends on unknown service \"{name}\"");

            state[name] = 1;
            path.Add(name);
            foreach (var dependency in definition.DependsOn) Visit(dependency, name);
            path.RemoveAt(path.Count - 1);
            state[name] = 2;
            ordered.Add(definition);
        }

        foreach (var definition in definitions) Visit(definition.Name, null);
        return ordered;
    }

    public async Task StartAllAsync(CancellationToken ct = default)
    {
        var ordered = OrderByDependencies(_services.Values.Select(r => r.Definition).ToList());
        foreach (var definition in ordered) await StartOneAsync(definition.Name, ct).ConfigureAwait(false);
        _logger.LogInformation("{Running}/{Total} background services running", Running, ordered.Count);
    }

    public async Task StartOneAsync(string name, CancellationToken ct = default)
    {
        if (!_services.TryGetValue(name, out var record)) throw new InvalidOperationException($"unknown service \"{name}\"");
        if (record.State == ServiceState.Running) return;

        record.State = ServiceState.Starting;
        try
        {
            if (record.Definition.OnStart is not null) await record.Definition.OnStart(ct).ConfigureAwait(false);

            record.Stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = record.Stopping.Token;

            if (record.Definition.Interval is { } interval)
                record.Loop = Task.Run(() => LoopAsync(record, interval, token), CancellationToken.None);

            record.State = ServiceState.Running;
            record.StartedAt = DateTimeOffset.UtcNow;
            record.ConsecutiveFailures = 0;
            _metrics.Gauge("service_up", 1, MetricLabels.Of("service", name), "1 when a service is running");
            _logger.LogDebug("Service {Name} started{Interval}", name,
                record.Definition.Interval is { } i ? $" (every {i.TotalMilliseconds:0}ms)" : "");
        }
        catch (Exception ex)
        {
            record.State = ServiceState.Failed;
            Remember(record, ex);
            _metrics.Gauge("service_up", 0, MetricLabels.Of("service", name));

            /* A non-critical service that fails to start must not abort the rest of
               startup - the bot should come up with one feature dark, not not at all. */
            if (record.Definition.Critical) throw;
            _logger.LogWarning(ex, "Service {Name} failed to start (non-critical, continuing)", name);
        }
    }

    /// <summary>Keep at most <see cref="MaxRecentErrors"/> failures, newest last.</summary>
    private static void Remember(Record record, Exception ex)
    {
        /* The TYPE as well as the message. "Object reference not set to an instance of an
           object" identifies nothing on its own, and it is exactly the message a null deref
           in a tick produces. */
        var message = ex.Message.Length > 0 ? $"{ex.GetType().Name}: {ex.Message}" : ex.GetType().Name;

        /* AND WHERE IT CAME FROM. The type and message together still identify nothing when
           the type is NullReferenceException - five identical "Object reference not set to an
           instance of an object" lines under `log-tail` is a real report this produced, and it
           narrowed the cause to "somewhere in the log pipeline", which is most of the bot.

           One frame is enough and is all that fits: the file and line of the throw site turns
           that report into a place to look. */
        if (Origin(ex) is { } origin) message += $"  ({origin})";

        record.LastError = message;

        lock (record.Recent)
        {
            record.Recent.Enqueue(new ServiceFailure(DateTimeOffset.UtcNow, message));
            while (record.Recent.Count > MaxRecentErrors) record.Recent.Dequeue();
        }
    }

    /// <summary>
    /// The deepest frame that belongs to this bot: "IpTrackingService.cs:302".
    /// </summary>
    /// <remarks>
    /// The innermost frame overall is usually inside the framework - a dictionary, a JSON
    /// converter, LINQ - which is true and no use. The first frame carrying one of OUR file
    /// names is the line to open. Falls back to the method name when the build carries no
    /// symbols, and to null rather than guessing.
    /// </remarks>
    internal static string? Origin(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var trace = new System.Diagnostics.StackTrace(ex.InnerException ?? ex, fNeedFileInfo: true);

        foreach (var frame in trace.GetFrames())
        {
            if (frame.GetFileName() is { Length: > 0 } file)
                return $"{Path.GetFileName(file)}:{frame.GetFileLineNumber()}";
        }

        var method = trace.GetFrames().FirstOrDefault()?.GetMethod();
        return method is null ? null : $"{method.DeclaringType?.Name}.{method.Name}";
    }

    private const int MaxRecentErrors = 5;

    /// <summary>
    /// How long a tick gets before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Derived from the interval rather than fixed, because the intervals here span three
    /// orders of magnitude - the log tail runs every 1.5 seconds and the player-count
    /// channels every five minutes - and one constant cannot suit both. Five intervals is
    /// generously past any healthy overrun while still catching a stall in minutes rather
    /// than never.
    ///
    /// Floored at 30 seconds so the fast services are not killed by a slow but legitimate
    /// sweep, and capped at 10 minutes so the slow ones still have a bound at all.
    /// </remarks>
    internal static TimeSpan TickBudget(ServiceDefinition definition, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Timeout is { } explicitly) return explicitly;

        var derived = interval * 5;
        if (derived < TimeSpan.FromSeconds(30)) return TimeSpan.FromSeconds(30);
        if (derived > TimeSpan.FromMinutes(10)) return TimeSpan.FromMinutes(10);
        return derived;
    }

    private async Task LoopAsync(Record record, TimeSpan interval, CancellationToken ct)
    {
        if (record.Definition.RunOnStart) await RunTickAsync(record, interval, ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await RunTickAsync(record, interval, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunTickAsync(Record record, TimeSpan interval, CancellationToken ct)
    {
        var name = record.Definition.Name;
        var started = Stopwatch.GetTimestamp();
        var budget = TickBudget(record.Definition, interval);

        /* ONE CORRELATION ID PER TICK. Background work interleaves - the log tail, the ban
           sweep and the channel renamer all write while each other are mid-run - so without
           this, reading a log meant guessing which lines belonged to which pass. */
        using var scope = _logger.BeginTick(name);

        try
        {
            /* BOUNDED. See ServiceDefinition.Timeout: without this a tick that never returns
               stops the service permanently while it still reports Running, and nothing in
               the supervisor is looking at a clock. */
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(budget);

            try
            {
                await record.Definition.Tick(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                /* Reported as a failure rather than swallowed, so it counts towards
                   ConsecutiveFailures and the service eventually gets restarted. */
                throw new TimeoutException(
                    $"tick exceeded its {budget.TotalSeconds:0.#}s budget and was abandoned");
            }

            /* A SUCCESSFUL TICK CLEARS THE BRAKE. Without this, three restarts inside any
               half hour would disable supervision of that service for the life of the
               process, so a service that recovered and later broke again would never be
               revived. */
            record.ConsecutiveFailures = 0;
            record.RestartsAt.Clear();
            record.RestartsExhausted = false;
            _metrics.Increment("service_ticks_total", MetricLabels.Of("service", name, "outcome", "success"),
                help: "Service ticks by outcome");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutting down, not a failure
        }
        catch (Exception ex)
        {
            record.Failures++;
            record.ConsecutiveFailures++;
            Remember(record, ex);
            _metrics.Increment("service_ticks_total", MetricLabels.Of("service", name, "outcome", "failure"));
            _logger.LogWarning(ex, "Service {Name} tick failed", name);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            record.Ticks++;
            record.LastTickAt = DateTimeOffset.UtcNow;
            record.LastDurationMs = elapsed.TotalMilliseconds;
            _metrics.Observe("service_tick_duration_ms", elapsed.TotalMilliseconds,
                MetricLabels.Of("service", name), "Service tick duration in milliseconds");

            // A tick slower than its own interval is the thing worth alerting on: the
            // service is no longer running at the frequency it was configured for.
            if (elapsed > interval)
            {
                record.Overruns++;
                _metrics.Increment("service_tick_overruns_total", MetricLabels.Of("service", name),
                    help: "Ticks that took longer than the service's interval");
            }
        }
    }

    public async Task StopOneAsync(string name)
    {
        if (!_services.TryGetValue(name, out var record) || record.State == ServiceState.Stopped) return;

        if (record.Stopping is not null) await record.Stopping.CancelAsync().ConfigureAwait(false);
        if (record.Loop is not null)
        {
            try { await record.Loop.ConfigureAwait(false); }
            catch (Exception) { /* already recorded by the tick handler */ }
        }
        record.Stopping?.Dispose();
        record.Stopping = null;
        record.Loop = null;

        try { if (record.Definition.OnStop is not null) await record.Definition.OnStop().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Service {Name} stop handler failed", name); }

        record.State = ServiceState.Stopped;
        record.StartedAt = null;
        _metrics.Gauge("service_up", 0, MetricLabels.Of("service", name));
        _logger.LogDebug("Service {Name} stopped", name);
    }

    /// <summary>Stop everything in REVERSE dependency order, so nothing is torn down while
    /// something that depends on it is still ticking.</summary>
    public async Task StopAllAsync()
    {
        var ordered = OrderByDependencies(_services.Values.Select(r => r.Definition).ToList()).Reverse();
        foreach (var definition in ordered) await StopOneAsync(definition.Name).ConfigureAwait(false);
    }

    public async Task RestartAsync(string name, CancellationToken ct = default)
    {
        await StopOneAsync(name).ConfigureAwait(false);
        _metrics.Increment("service_restarts_total", MetricLabels.Of("service", name), help: "Service restarts");
        await StartOneAsync(name, ct).ConfigureAwait(false);
    }

    /// <summary>How many supervisor restarts one service may have inside <see cref="RestartWindow"/>.</summary>
    /// <remarks>
    /// THE SUPERVISOR HAD NO BRAKE. A service whose dependency is simply down - RCON
    /// unreachable, a game server off, a disk full - fails every tick, and a restart does not
    /// fix any of those. StartOneAsync clears ConsecutiveFailures, so the counter climbs back
    /// to the threshold and the service is restarted again, and again, for as long as the
    /// outage lasts. Nothing escalated, nothing gave up, and each restart awaits the running
    /// loop, which can sit for a whole tick budget (up to ten minutes) before it lets go.
    ///
    /// CrashRecovery reached this conclusion first, for Pavlov servers, and says so in its own
    /// remarks: past the cap "it stops and says so once, because a server that will not start
    /// needs a person rather than another restart". The same is true of our own services, and
    /// the supervisor for them was the one place without the rule.
    /// </remarks>
    public const int MaxRestartsPerWindow = 3;

    /// <summary>The window the restart cap is measured over.</summary>
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(30);

    /// <summary>Restart anything that has failed outright, or failed repeatedly in a row.</summary>
    /// <remarks>
    /// Bounded: see <see cref="MaxRestartsPerWindow"/>. A service past the cap is left in
    /// Failed so it shows red in /health rather than being quietly abandoned - the point is to
    /// stop pretending a restart will help, not to stop reporting the problem.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ReviveFailedAsync(int threshold = 5, CancellationToken ct = default)
    {
        var revived = new List<string>();
        var now = DateTimeOffset.UtcNow;

        foreach (var record in _services.Values)
        {
            var stuck = record.State == ServiceState.Failed ||
                        (record.State == ServiceState.Running && record.ConsecutiveFailures >= threshold);
            if (!stuck) continue;

            // Pruned here rather than on a timer, so the list cannot outgrow the window.
            record.RestartsAt.RemoveAll(at => now - at > RestartWindow);

            if (record.RestartsAt.Count >= MaxRestartsPerWindow)
            {
                /* SAID ONCE. Repeating it every sweep would bury the original failure under
                   the report that we have stopped acting on it. */
                if (!record.RestartsExhausted)
                {
                    record.RestartsExhausted = true;
                    _logger.LogError(
                        "Service {Name} restarted {Count} times in {Minutes:0} minutes and still fails - " +
                        "giving up until it recovers or the bot restarts. Last error: {Error}",
                        record.Definition.Name, record.RestartsAt.Count, RestartWindow.TotalMinutes,
                        record.LastError ?? "(none recorded)");
                }
                continue;
            }

            _logger.LogWarning("Restarting {Name} after {Failures} consecutive failures",
                record.Definition.Name, record.ConsecutiveFailures);

            record.RestartsAt.Add(now);
            await RestartAsync(record.Definition.Name, ct).ConfigureAwait(false);
            revived.Add(record.Definition.Name);
        }
        return revived;
    }

    public IReadOnlyList<ServiceStatus> Status() =>
        _services.Values.Select(r => new ServiceStatus(
            r.Definition.Name, r.State,
            r.StartedAt is { } at ? DateTimeOffset.UtcNow - at : TimeSpan.Zero,
            r.Definition.Interval, r.Ticks, r.Failures, r.ConsecutiveFailures, r.Overruns,
            r.LastError, r.LastTickAt, r.LastDurationMs,
            RecentErrors: Snapshot(r))).ToList();

    /// <summary>Newest first, which is the order somebody reads them in.</summary>
    private static IReadOnlyList<ServiceFailure> Snapshot(Record record)
    {
        lock (record.Recent) return [.. record.Recent.Reverse()];
    }

    /// <summary>Roll every service into one verdict, honouring per-service checks.</summary>
    public async Task<HealthResult> HealthAsync(CancellationToken ct = default)
    {
        var worst = HealthStatus.Healthy;
        var notes = new List<string>();

        foreach (var record in _services.Values)
        {
            var status = record.State switch
            {
                ServiceState.Running => HealthStatus.Healthy,
                ServiceState.Stopped => HealthStatus.Degraded,
                _ => HealthStatus.Unhealthy,
            };
            var detail = record.LastError;

            if (record.State == ServiceState.Running && record.ConsecutiveFailures > 0)
            {
                status = HealthStatus.Degraded;
                detail = $"{record.ConsecutiveFailures} consecutive tick failures";
            }

            if (record.State == ServiceState.Running && record.Definition.HealthCheck is { } check)
            {
                try
                {
                    var result = await check(ct).ConfigureAwait(false);
                    if (result.Status != HealthStatus.Healthy) { status = result.Status; detail = result.Detail ?? detail; }
                }
                catch (Exception ex) { status = HealthStatus.Unhealthy; detail = ex.Message; }
            }

            if (status != HealthStatus.Healthy) notes.Add($"{record.Definition.Name}: {detail ?? status.ToString()}");
            if (status > worst) worst = status;
        }

        return new HealthResult(worst, notes.Count > 0 ? string.Join("; ", notes) : $"{Running}/{Count} running");
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}
