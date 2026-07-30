using System.Collections.Concurrent;
using System.Diagnostics;

namespace PavlovBot.Host.Observability;

/// <summary>
/// Three states, and the middle one is the point.
/// </summary>
/// <remarks>
/// <see cref="Degraded"/> is the difference between "page someone" and "watch it". One of
/// three RCON hosts down, or a reachable-but-slow server, is degraded - not a failure, not
/// fine. Collapsing it into either neighbour is how you get either alert fatigue or a
/// silent partial outage.
/// </remarks>
public enum HealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2,
}

/// <param name="Detail">Why, in words a human reads at 3am. Null when there is nothing to say.</param>
public sealed record HealthResult(HealthStatus Status, string? Detail = null)
{
    public static HealthResult Healthy(string? detail = null) => new(HealthStatus.Healthy, detail);
    public static HealthResult Degraded(string detail) => new(HealthStatus.Degraded, detail);
    public static HealthResult Unhealthy(string detail) => new(HealthStatus.Unhealthy, detail);
}

public sealed record ComponentReport(
    string Name,
    HealthStatus Status,
    string? Detail,
    double DurationMs,
    DateTimeOffset CheckedAt,
    bool Critical);

public sealed record HealthReport(
    HealthStatus Status,
    DateTimeOffset CheckedAt,
    IReadOnlyDictionary<string, ComponentReport> Components);

/// <summary>
/// Component health checks, rolled up into one verdict.
/// </summary>
/// <remarks>
/// Three rules, each of which exists because the alternative was observed:
///
///   A CHECK THAT THROWS IS UNHEALTHY, not an exception. The framework must not be able to
///   take down the thing it is monitoring.
///
///   A CHECK THAT HANGS IS UNHEALTHY after a timeout. An unbounded await inside a health
///   endpoint turns a scrape into a hang - monitoring causing the outage it was meant to
///   detect. Note the hung check keeps running in the background; it is abandoned, not
///   cancelled, because a check that ignores its token would otherwise still block us.
///
///   RESULTS ARE CACHED BRIEFLY so a scrape loop cannot itself become load.
/// </remarks>
public sealed class HealthRegistry
{
    private sealed class Entry
    {
        public required Func<CancellationToken, Task<HealthResult>> Check { get; init; }
        public required bool Critical { get; init; }
        public ComponentReport? Cached;
    }

    private readonly ConcurrentDictionary<string, Entry> _checks = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cacheDuration;
    private readonly TimeProvider _time;

    public HealthRegistry(TimeSpan? timeout = null, TimeSpan? cacheDuration = null, TimeProvider? timeProvider = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        _cacheDuration = cacheDuration ?? TimeSpan.FromSeconds(1);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <param name="critical">
    /// False means this component can be unhealthy without failing the overall rollup. Use
    /// it for optional features - an unconfigured webhook, a detector nobody set a key for.
    /// A bot with one optional feature dark is not an outage.
    /// </param>
    /// <returns>A handle that unregisters the check when disposed.</returns>
    public IDisposable Register(string name, Func<CancellationToken, Task<HealthResult>> check, bool critical = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);
        _checks[name] = new Entry { Check = check, Critical = critical };
        return new Registration(this, name);
    }

    public bool Unregister(string name) => _checks.TryRemove(name, out _);

    public IReadOnlyCollection<string> Names => _checks.Keys.ToList();

    private async Task<ComponentReport> RunAsync(string name, Entry entry, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (entry.Cached is { } cached && now - cached.CheckedAt < _cacheDuration) return cached;

        var started = Stopwatch.GetTimestamp();
        HealthResult result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var check = entry.Check(cts.Token);
            var completed = await Task.WhenAny(check, Task.Delay(_timeout, cts.Token)).ConfigureAwait(false);

            if (completed != check)
            {
                result = HealthResult.Unhealthy($"health check timed out after {_timeout.TotalMilliseconds:0}ms");
                // Observe the abandoned task so a later failure is not an unobserved exception.
                _ = check.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            }
            else
            {
                result = await check.ConfigureAwait(false) ?? HealthResult.Healthy();
            }
        }
        catch (Exception ex)
        {
            result = HealthResult.Unhealthy(ex.Message);
        }

        var report = new ComponentReport(
            name, result.Status, result.Detail,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, now, entry.Critical);

        entry.Cached = report;
        return report;
    }

    /// <summary>Every component, plus a worst-wins overall status across the CRITICAL ones.</summary>
    public async Task<HealthReport> ReportAsync(CancellationToken ct = default)
    {
        var entries = _checks.ToArray();
        var reports = await Task.WhenAll(entries.Select(e => RunAsync(e.Key, e.Value, ct))).ConfigureAwait(false);

        var overall = HealthStatus.Healthy;
        foreach (var report in reports)
            if (report.Critical && report.Status > overall) overall = report.Status;

        return new HealthReport(
            overall,
            _time.GetUtcNow(),
            reports.ToDictionary(r => r.Name, r => r, StringComparer.Ordinal));
    }

    public async Task<ComponentReport?> CheckAsync(string name, CancellationToken ct = default) =>
        _checks.TryGetValue(name, out var entry) ? await RunAsync(name, entry, ct).ConfigureAwait(false) : null;

    private sealed class Registration : IDisposable
    {
        private readonly HealthRegistry _owner;
        private readonly string _name;
        public Registration(HealthRegistry owner, string name) { _owner = owner; _name = name; }
        public void Dispose() => _owner.Unregister(_name);
    }
}
