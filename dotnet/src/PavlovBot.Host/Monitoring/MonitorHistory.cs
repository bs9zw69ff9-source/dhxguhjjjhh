using PavlovBot.Core.Data;
using PavlovBot.Core.Monitoring;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Monitoring;

/// <summary>One recorded reading, kept so /server health can look back.</summary>
public sealed record MonitorSample(
    DateTimeOffset At,
    HealthState State,
    double? LatencyMs,
    int? Players,
    bool RconOk,
    bool UnexpectedRestart);

/// <summary>Aggregate health over a window, for the /server health card.</summary>
public sealed record HealthStats(
    double UptimePercent,
    int RconFailures,
    int UnexpectedRestarts,
    double? AverageLatencyMs,
    TimeSpan LongestOutage,
    int Samples)
{
    public static HealthStats Empty { get; } = new(0, 0, 0, null, TimeSpan.Zero, 0);
}

/// <summary>
/// The rolling record of what monitoring saw, persisted so it survives a bot restart.
/// </summary>
/// <remarks>
/// BOUNDED ON WRITE. The list is pruned to a retention window every time it is appended to, so a
/// server that has been monitored for a month holds a day of samples, not a month of them - the
/// write path is the only place it grows, so it is the right place to cap it. This is memory the
/// bot would otherwise leak one sample at a time.
///
/// Uptime is counted over samples, which are evenly spaced, so the count is a faithful proxy for
/// time without having to integrate over gaps. A window with no samples is reported as such rather
/// than as 100% - "no data" and "perfect" are not the same answer.
/// </remarks>
public sealed class MonitorHistory(SerializedStore store, TimeProvider time)
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(25);
    private const int MaxSamplesPerServer = 4000;

    private Dictionary<string, List<MonitorSample>> Read() =>
        store.Read(Datasets.MonitorHistory, new Dictionary<string, List<MonitorSample>>(StringComparer.OrdinalIgnoreCase));

    public Task RecordAsync(string server, MonitorSample sample, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var cutoff = time.GetUtcNow() - Retention;

        return store.UpdateAsync(Datasets.MonitorHistory,
            new Dictionary<string, List<MonitorSample>>(StringComparer.OrdinalIgnoreCase),
            all =>
            {
                var list = all.TryGetValue(server, out var existing) && existing is not null
                    ? existing : [];

                list.Add(sample);
                list.RemoveAll(s => s is null || s.At < cutoff);
                if (list.Count > MaxSamplesPerServer)
                    list.RemoveRange(0, list.Count - MaxSamplesPerServer);

                all[server] = list;
                return all;
            }, ct);
    }

    public HealthStats Stats(string server, TimeSpan window)
    {
        var since = time.GetUtcNow() - window;
        var samples = Read().GetValueOrDefault(server)?
            .Where(s => s is not null && s.At >= since)
            .OrderBy(s => s.At)
            .ToList() ?? [];

        if (samples.Count == 0) return HealthStats.Empty;

        var up = samples.Count(s => s.State is HealthState.Online or HealthState.Degraded);
        var failures = samples.Count(s => !s.RconOk);
        var restarts = samples.Count(s => s.UnexpectedRestart);
        var latencies = samples.Where(s => s.LatencyMs is not null).Select(s => s.LatencyMs!.Value).ToList();

        return new HealthStats(
            UptimePercent: 100.0 * up / samples.Count,
            RconFailures: failures,
            UnexpectedRestarts: restarts,
            AverageLatencyMs: latencies.Count > 0 ? latencies.Average() : null,
            LongestOutage: LongestOutage(samples),
            Samples: samples.Count);
    }

    /// <summary>The longest unbroken run of OFFLINE samples, measured end to end.</summary>
    private static TimeSpan LongestOutage(IReadOnlyList<MonitorSample> samples)
    {
        var longest = TimeSpan.Zero;
        DateTimeOffset? start = null;
        DateTimeOffset last = default;

        foreach (var s in samples)
        {
            if (s.State == HealthState.Offline)
            {
                start ??= s.At;
                last = s.At;
            }
            else if (start is { } began)
            {
                if (last - began > longest) longest = last - began;
                start = null;
            }
        }

        if (start is { } stillDown && last - stillDown > longest) longest = last - stillDown;
        return longest;
    }
}
