using System.Globalization;

namespace PavlovBot.Core.Network;

/// <summary>What a search of the anti-DDoS logs keeps. Every field is optional; unset matches anything.</summary>
/// <param name="Network">An address or network the record's source must be in.</param>
/// <param name="Port">A destination port the record covers.</param>
/// <param name="Protocol"><c>udp</c>, <c>tcp</c> or <c>icmp</c> (ICMPv6 counts as ICMP).</param>
/// <param name="Action"><c>dropped</c> or <c>passed</c> for packets; an event's action.</param>
/// <param name="Category">A drop reason, suspicion or event category; matched as a prefix, so <c>suspect</c> finds both kinds.</param>
public sealed record QueryFilter(
    DateTimeOffset From,
    DateTimeOffset To,
    IpNetworks.Network? Network = null,
    int? Port = null,
    string? Protocol = null,
    string? Action = null,
    string? Category = null)
{
    public bool Matches(LogRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Time < From || r.Time > To) return false;
        if (Network is not null && (r.Source is null || !Network.Contains(r.Source))) return false;
        if (Port is { } port && !r.Ports.Contains(port)) return false;
        if (Protocol is { } protocol && !SameProtocol(protocol, r.Protocol)) return false;
        if (Action is { } action && !string.Equals(action, r.Action, StringComparison.OrdinalIgnoreCase)) return false;
        if (Category is { } category && (r.Category is null || !r.Category.StartsWith(category, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    private static bool SameProtocol(string wanted, string? actual) =>
        actual is not null && (string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase) ||
                               (wanted.Equals("icmp", StringComparison.OrdinalIgnoreCase) && actual == "ipv6-icmp"));
}

/// <summary>One source's totals across a search.</summary>
public sealed record SourceTotal(string Source, long Packets, long Bytes, long Dropped, double PeakPps, int Windows, bool Player);

/// <summary>The searches over the logs: filtering, the busiest sources, and the traffic rate over time.</summary>
/// <remarks>
/// STREAMING. Records go through one at a time from files the caller has already narrowed to
/// the time range, so a search holds only what it returns, never a whole log, and a result is
/// cut at its limit instead of growing with the log.
/// </remarks>
public static class LogQuery
{
    /// <summary>The records that match, oldest first, at most <paramref name="limit"/>; and whether there were more.</summary>
    public static (IReadOnlyList<LogRecord> Records, bool Truncated) Find(IEnumerable<LogRecord> records, QueryFilter filter, int limit)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(filter);
        var found = new List<LogRecord>();
        foreach (var r in records)
        {
            if (!filter.Matches(r)) continue;
            if (found.Count >= limit) return (found, true);
            found.Add(r);
        }
        return (found, false);
    }

    /// <summary>The busiest sources across source records, by packets including those dropped.</summary>
    public static IReadOnlyList<SourceTotal> TopSources(IEnumerable<LogRecord> records, QueryFilter filter, int top)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(filter);
        var totals = new Dictionary<string, (long Packets, long Bytes, long Dropped, double Peak, int Windows, bool Player)>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.Kind != RecordKind.Source || r.Source is null || !filter.Matches(r)) continue;
            var dropped = Field(r.Line, "dropped");
            var pps = FieldDouble(r.Line, "pps");
            var player = r.Line.Contains("\"player\":true", StringComparison.Ordinal);
            totals[r.Source] = totals.TryGetValue(r.Source, out var t)
                ? (t.Packets + r.Packets, t.Bytes + r.Bytes, t.Dropped + dropped, Math.Max(t.Peak, pps), t.Windows + 1, t.Player || player)
                : (r.Packets, r.Bytes, dropped, pps, 1, player);
        }
        return [.. totals
            .Select(t => new SourceTotal(t.Key, t.Value.Packets, t.Value.Bytes, t.Value.Dropped, t.Value.Peak, t.Value.Windows, t.Value.Player))
            .OrderByDescending(t => t.Packets + t.Dropped)
            .ThenBy(t => t.Source, StringComparer.Ordinal)
            .Take(top)];
    }

    /// <summary>One point of the traffic rate: the interface's packets and megabits a second, and what UDP got through.</summary>
    public sealed record RatePoint(DateTimeOffset Time, double Pps, double Mbps, long PeakPps, long UdpPackets, long Dropped);

    /// <summary>The traffic rate across traffic records, oldest first.</summary>
    public static IReadOnlyList<RatePoint> Rates(IEnumerable<LogRecord> records, QueryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(filter);
        var points = new List<RatePoint>();
        foreach (var r in records)
        {
            if (r.Kind != RecordKind.Traffic || r.Time < filter.From || r.Time > filter.To) continue;
            var dropped = FloodShieldRules.DropReasons.Sum(reason => CounterPackets(r.Line, reason));
            points.Add(new RatePoint(r.Time, FieldDouble(r.Line, "pps"), FieldDouble(r.Line, "mbps"), Field(r.Line, "peak_pps"),
                CounterPackets(r.Line, "udp"), dropped));
        }
        return points;
    }

    private static long Field(string line, string name) =>
        (long)FieldDouble(line, name);

    private static double FieldDouble(string line, string name)
    {
        // Our own records, written by AntiDdosRecords: "name":number with no spaces. Cheaper than a
        // second parse of every line in a large search.
        var key = "\"" + name + "\":";
        var at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return 0;
        var start = at + key.Length;
        var end = start;
        while (end < line.Length && (char.IsDigit(line[end]) || line[end] is '.' or '-' or 'E' or 'e' or '+')) end++;
        return double.TryParse(line.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static long CounterPackets(string line, string counter)
    {
        var key = "\"" + counter + "\":{\"packets\":";
        var at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return 0;
        var start = at + key.Length;
        var end = start;
        while (end < line.Length && char.IsDigit(line[end])) end++;
        return long.TryParse(line.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
