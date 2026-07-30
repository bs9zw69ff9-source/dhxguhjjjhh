using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PavlovBot.Host.Observability;

/// <summary>
/// A label set, kept small and allocation-light because metrics sit in hot paths.
/// </summary>
/// <remarks>
/// Labels are sorted when the key is built, so <c>{a,b}</c> and <c>{b,a}</c> are the SAME
/// series. Without that you silently get two time series for one thing and every dashboard
/// built on it is quietly wrong.
/// </remarks>
public readonly struct MetricLabels : IEquatable<MetricLabels>
{
    private readonly (string Key, string Value)[]? _pairs;

    private MetricLabels((string, string)[] pairs) => _pairs = pairs;

    public static MetricLabels None => default;

    public static MetricLabels Of(string key, string value) => new([(key, value)]);

    public static MetricLabels Of(string k1, string v1, string k2, string v2) => new([(k1, v1), (k2, v2)]);

    /// <summary>A copy with one more label. Used to add an outcome to a caller's labels.</summary>
    public MetricLabels With(string key, string value)
    {
        if (_pairs is null) return Of(key, value);
        var next = new (string, string)[_pairs.Length + 1];
        Array.Copy(_pairs, next, _pairs.Length);
        next[^1] = (key, value);
        return new MetricLabels(next);
    }

    private (string Key, string Value)[] Sorted()
    {
        if (_pairs is null || _pairs.Length < 2) return _pairs ?? [];
        var copy = (( string Key, string Value)[])_pairs.Clone();
        Array.Sort(copy, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return copy;
    }

    /// <summary>Stable identity for this label set.</summary>
    public string Key()
    {
        var pairs = Sorted();
        if (pairs.Length == 0) return "";
        return string.Join(",", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>Prometheus exposition form, including the braces. Empty when unlabelled.</summary>
    public string Render()
    {
        var pairs = Sorted();
        if (pairs.Length == 0) return "";
        return "{" + string.Join(",", pairs.Select(p => $"{p.Key}=\"{Escape(p.Value)}\"")) + "}";
    }

    internal string RenderWith(string extraKey, string extraValue) =>
        With(extraKey, extraValue).Render();

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    public bool Equals(MetricLabels other) => string.Equals(Key(), other.Key(), StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is MetricLabels other && Equals(other);
    public override int GetHashCode() => Key().GetHashCode(StringComparison.Ordinal);
    public static bool operator ==(MetricLabels left, MetricLabels right) => left.Equals(right);
    public static bool operator !=(MetricLabels left, MetricLabels right) => !left.Equals(right);
}

public enum MetricType { Counter, Gauge, Histogram }

/// <summary>
/// Counters, gauges and histograms with Prometheus text exposition.
/// </summary>
/// <remarks>
/// Hand-rolled rather than a client library, for the same reason the Node bot is: this is
/// about 200 lines of arithmetic, and the dependency it replaces would be the largest thing
/// in the process after Discord.Net.
///
/// Two rules that came out of running it:
///
///   SERIES COUNT IS CAPPED. Labels are fed from user-influenced values - command names,
///   server labels - so an unbounded registry is a memory leak with extra steps. Past the
///   ceiling, new label combinations are dropped and counted.
///
///   NOTHING HERE THROWS. A metrics bug must never take down a moderation action. Every
///   public method swallows its own failures; the worst case is a missing data point.
/// </remarks>
public sealed class MetricsRegistry
{
    public const int MaxSeries = 5000;

    /// <summary>Millisecond buckets, spanning "instant" to "something is wrong".</summary>
    public static readonly double[] DefaultBuckets =
        [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    private sealed class Series
    {
        public required MetricLabels Labels { get; init; }
        public double Value;
        public double[]? Buckets;
        public long[]? Counts;
        public double Sum;
        public long Count;
    }

    private sealed class Metric
    {
        public required string Name { get; init; }
        public required MetricType Type { get; init; }
        public required string Help { get; set; }
        public ConcurrentDictionary<string, Series> Series { get; } = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, Metric> _registry = new(StringComparer.Ordinal);
    private readonly string _prefix;
    private readonly Lock _sync = new();
    private int _seriesCount;
    private int _dropped;

    public MetricsRegistry(string prefix = "bot") => _prefix = prefix;

    private Metric GetMetric(string name, MetricType type, string? help)
    {
        var metric = _registry.GetOrAdd(name, n => new Metric { Name = n, Type = type, Help = help ?? n });
        // First caller with real help text wins; later ones do not clobber it with the name.
        if (help is not null && string.Equals(metric.Help, name, StringComparison.Ordinal)) metric.Help = help;
        return metric;
    }

    /// <summary>The series for a label set, or null once the ceiling is hit.</summary>
    private Series? GetSeries(Metric metric, MetricLabels labels, double[]? buckets)
    {
        var key = labels.Key();
        if (metric.Series.TryGetValue(key, out var existing)) return existing;

        lock (_sync)
        {
            if (metric.Series.TryGetValue(key, out existing)) return existing;
            if (_seriesCount >= MaxSeries) { _dropped++; return null; }

            var series = new Series { Labels = labels };
            if (buckets is not null)
            {
                series.Buckets = (double[])buckets.Clone();
                series.Counts = new long[buckets.Length];
            }
            metric.Series[key] = series;
            _seriesCount++;
            return series;
        }
    }

    /// <summary>Monotonically increasing count - commands run, errors, retries.</summary>
    public void Increment(string name, MetricLabels labels = default, double by = 1, string? help = null)
    {
        try
        {
            var series = GetSeries(GetMetric(name, MetricType.Counter, help), labels, null);
            if (series is null) return;
            lock (series) series.Value += by;
        }
        catch (Exception) { /* metrics must never break the caller */ }
    }

    /// <summary>Point-in-time value that can move either way - memory, queue depth, service up.</summary>
    public void Gauge(string name, double value, MetricLabels labels = default, string? help = null)
    {
        try
        {
            var series = GetSeries(GetMetric(name, MetricType.Gauge, help), labels, null);
            if (series is null) return;
            lock (series) series.Value = value;
        }
        catch (Exception) { }
    }

    /// <summary>A distribution. <paramref name="value"/> is milliseconds for timers.</summary>
    public void Observe(string name, double value, MetricLabels labels = default, string? help = null, double[]? buckets = null)
    {
        try
        {
            var chosen = buckets ?? DefaultBuckets;
            var series = GetSeries(GetMetric(name, MetricType.Histogram, help), labels, chosen);
            if (series is null || series.Buckets is null || series.Counts is null) return;

            lock (series)
            {
                series.Sum += value;
                series.Count++;
                // Cumulative, per Prometheus: bucket le="0.5" counts everything <= 0.5.
                for (var i = 0; i < series.Buckets.Length; i++)
                    if (value <= series.Buckets[i]) series.Counts[i]++;
            }
        }
        catch (Exception) { }
    }

    /// <summary>Time an operation, recording duration and a success/failure outcome label.</summary>
    public async Task<T> TimeAsync<T>(string name, MetricLabels labels, Func<Task<T>> operation, string? help = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await operation().ConfigureAwait(false);
            Observe(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, labels.With("outcome", "success"), help);
            return result;
        }
        catch (Exception)
        {
            Observe(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, labels.With("outcome", "failure"), help);
            throw;
        }
    }

    /// <summary>Prometheus text exposition format, v0.0.4.</summary>
    public string Render()
    {
        var output = new StringBuilder();
        foreach (var metric in _registry.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var full = $"{_prefix}_{metric.Name}";
            output.Append("# HELP ").Append(full).Append(' ').Append(metric.Help).Append('\n');
            output.Append("# TYPE ").Append(full).Append(' ').Append(metric.Type.ToString().ToLowerInvariant()).Append('\n');

            foreach (var series in metric.Series.Values)
            {
                lock (series)
                {
                    if (metric.Type == MetricType.Histogram && series.Buckets is not null && series.Counts is not null)
                    {
                        for (var i = 0; i < series.Buckets.Length; i++)
                        {
                            output.Append(full).Append("_bucket")
                                  .Append(series.Labels.RenderWith("le", Number(series.Buckets[i])))
                                  .Append(' ').Append(series.Counts[i]).Append('\n');
                        }
                        output.Append(full).Append("_bucket").Append(series.Labels.RenderWith("le", "+Inf"))
                              .Append(' ').Append(series.Count).Append('\n');
                        output.Append(full).Append("_sum").Append(series.Labels.Render())
                              .Append(' ').Append(Number(series.Sum)).Append('\n');
                        output.Append(full).Append("_count").Append(series.Labels.Render())
                              .Append(' ').Append(series.Count).Append('\n');
                    }
                    else
                    {
                        output.Append(full).Append(series.Labels.Render())
                              .Append(' ').Append(Number(series.Value)).Append('\n');
                    }
                }
            }
        }
        return output.ToString();
    }

    // Invariant culture, always. A scrape from a host with a comma decimal separator would
    // otherwise emit numbers Prometheus rejects.
    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    public MetricsStats Stats() => new(_registry.Count, _seriesCount, _dropped);

    /// <summary>Drop every series. Test and maintenance helper.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _registry.Clear();
            _seriesCount = 0;
            _dropped = 0;
        }
    }
}

/// <param name="Dropped">Series refused because the ceiling was reached - a label explosion alarm.</param>
public readonly record struct MetricsStats(int Metrics, int Series, int Dropped);
