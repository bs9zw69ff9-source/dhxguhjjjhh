namespace PavlovBot.Core.Network;

/// <summary>One source's traffic over one window, out of the kernel's counters.</summary>
/// <param name="Packets">Packets that got through the guard (TCP, UDP and anything else).</param>
/// <param name="Bytes">Their IP-level bytes, headers included.</param>
/// <param name="TopPorts">The busiest destinations, busiest first, at most <see cref="TrafficAccounting.TopPortsKept"/>.</param>
/// <param name="DistinctPorts">How many destination ports it reached in the window.</param>
/// <param name="Syn">New TCP connections it opened.</param>
/// <param name="Dropped">Packets of its the guard dropped (block list, limits).</param>
public sealed record SourceWindow(
    string Source,
    long Packets,
    long Bytes,
    long Udp,
    long Tcp,
    long Other,
    IReadOnlyList<PortCount> TopPorts,
    int DistinctPorts,
    long Syn,
    long Dropped)
{
    public bool Player { get; init; }

    public bool Trusted { get; init; }
}

/// <summary>Packets to one protocol and port.</summary>
public readonly record struct PortCount(string Protocol, int Port, long Packets);

/// <summary>
/// Turning two readings of the kernel's ever-growing counters into what happened in between.
/// </summary>
/// <remarks>
/// A COUNTER ONLY GROWS, UNTIL ITS ENTRY EXPIRES. An entry the kernel dropped after three idle
/// minutes and re-created reads lower than last time; that is a fresh count, not a negative one.
/// An entry that expired between two readings was idle for longer than the window, so nothing it
/// counted is lost.
/// </remarks>
public static class TrafficAccounting
{
    public const int TopPortsKept = 5;

    /// <summary>What each source sent between two readings, busiest first.</summary>
    public static IReadOnlyList<SourceWindow> Between(Accounting? previous, Accounting current)
    {
        ArgumentNullException.ThrowIfNull(current);
        previous ??= Accounting.Empty;

        var bySource = new Dictionary<string, Builder>(StringComparer.Ordinal);
        Builder For(string source) => bySource.TryGetValue(source, out var b) ? b : bySource[source] = new Builder();

        foreach (var (key, now) in current.Flows)
        {
            var d = Delta(previous.Flows.TryGetValue(key, out var then) ? then : default, now);
            if (d.Packets == 0) continue;
            var b = For(key.Source);
            b.Packets += d.Packets;
            b.Bytes += d.Bytes;
            if (key.Protocol == "udp") b.Udp += d.Packets;
            else if (key.Protocol == "tcp") b.Tcp += d.Packets;
            b.Ports.Add(new PortCount(key.Protocol, key.Port, d.Packets));
        }

        foreach (var (key, now) in current.Other)
        {
            var d = Delta(previous.Other.TryGetValue(key, out var then) ? then : default, now);
            if (d.Packets == 0) continue;
            var b = For(key.Source);
            b.Packets += d.Packets;
            b.Bytes += d.Bytes;
            b.Other += d.Packets;
        }

        foreach (var (key, now) in current.Syn)
        {
            var d = Delta(previous.Syn.TryGetValue(key, out var then) ? then : default, now);
            if (d.Packets > 0) For(key.Source).Syn += d.Packets;
        }

        foreach (var (source, now) in current.Dropped)
        {
            var d = Delta(previous.Dropped.TryGetValue(source, out var then) ? then : default, now);
            if (d.Packets > 0) For(source).Dropped += d.Packets;
        }

        return [.. bySource
            .Select(s => s.Value.Build(s.Key))
            .OrderByDescending(s => s.Packets + s.Dropped)
            .ThenBy(s => s.Source, StringComparer.Ordinal)];
    }

    private static Counter Delta(Counter then, Counter now) =>
        now.Packets >= then.Packets && now.Bytes >= then.Bytes ? new Counter(now.Packets - then.Packets, now.Bytes - then.Bytes) : now;

    /// <summary>The change in each rule counter between two readings; a counter that went down was reset.</summary>
    public static IReadOnlyDictionary<string, Counter> CounterDeltas(
        IReadOnlyDictionary<string, Counter>? previous, IReadOnlyDictionary<string, Counter> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var result = new Dictionary<string, Counter>(StringComparer.Ordinal);
        foreach (var (name, now) in current)
            result[name] = Delta(previous is not null && previous.TryGetValue(name, out var then) ? then : default, now);
        return result;
    }

    private sealed class Builder
    {
        public long Packets;
        public long Bytes;
        public long Udp;
        public long Tcp;
        public long Other;
        public long Syn;
        public long Dropped;
        public readonly List<PortCount> Ports = [];

        public SourceWindow Build(string source) => new(
            source, Packets, Bytes, Udp, Tcp, Other,
            [.. Ports.OrderByDescending(p => p.Packets).ThenBy(p => p.Port).Take(TopPortsKept)],
            Ports.Select(p => p.Port).Distinct().Count(),
            Syn, Dropped);
    }
}

/// <summary>The thresholds a window of per-source traffic is judged against.</summary>
/// <param name="UdpPps">A non-player sustaining more than this to any UDP port.</param>
/// <param name="SynPerMinute">New TCP connections a minute from one address.</param>
/// <param name="PortSpread">Distinct destination ports a minute from one address: a scan, or a reflection flood.</param>
/// <param name="DroppedPerMinute">Packets the guard dropped from one address in a minute, worth a record of its own.</param>
public sealed record DetectionThresholds(int UdpPps = 300, int SynPerMinute = 120, int PortSpread = 50, int DroppedPerMinute = 10_000);

/// <summary>One thing a source did that is worth an event.</summary>
/// <param name="Category"><c>udp-rate</c>, <c>syn-burst</c>, <c>port-spread</c> or <c>dropped</c>.</param>
public sealed record Detection(string Category, string Source, double Observed, double Threshold, string Reason)
{
    /// <summary>Whether repeating this alone may get the source banned. A drop is the firewall already working.</summary>
    public bool Escalates => Category != "dropped";
}

/// <summary>
/// What in a window of per-source traffic counts as suspicious.
/// </summary>
/// <remarks>
/// EVIDENCE, NOT A VERDICT. A detection is recorded and alerted; whether it leads to a ban is the
/// <see cref="ResponsePlanner"/>'s call, which wants it repeated and never bans players or the
/// trusted. Trusted addresses are never detected at all; players are only judged on connection
/// bursts, since a Pavlov client's UDP rate is exactly what the UDP threshold must not catch.
/// </remarks>
public static class TrafficDetector
{
    public static IReadOnlyList<Detection> Judge(IEnumerable<SourceWindow> sources, TimeSpan window, DetectionThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(thresholds);
        var seconds = Math.Max(1, window.TotalSeconds);
        var minutes = seconds / 60;

        var found = new List<Detection>();
        foreach (var s in sources)
        {
            if (s.Trusted) continue;

            var udpPps = s.Udp / seconds;
            if (!s.Player && udpPps > thresholds.UdpPps)
                found.Add(new Detection("udp-rate", s.Source, udpPps, thresholds.UdpPps,
                    $"{udpPps:0} UDP packets/s on average, over {thresholds.UdpPps}/s"));

            var synPerMinute = s.Syn / minutes;
            if (synPerMinute > thresholds.SynPerMinute)
                found.Add(new Detection("syn-burst", s.Source, synPerMinute, thresholds.SynPerMinute,
                    $"{synPerMinute:0} new TCP connections/min, over {thresholds.SynPerMinute}/min"));

            if (!s.Player && s.DistinctPorts >= thresholds.PortSpread)
                found.Add(new Detection("port-spread", s.Source, s.DistinctPorts, thresholds.PortSpread,
                    $"{s.DistinctPorts} destination ports in {seconds:0}s - a scan, or replies to a reflection attack"));

            var droppedPerMinute = s.Dropped / minutes;
            if (droppedPerMinute >= thresholds.DroppedPerMinute)
                found.Add(new Detection("dropped", s.Source, droppedPerMinute, thresholds.DroppedPerMinute,
                    $"{droppedPerMinute:0} packets/min dropped by the firewall"));
        }
        return found;
    }
}
