using System.Globalization;
using System.Text;

namespace PavlovBot.Core.Network;

/// <summary>What the firewall shield did during the attack.</summary>
/// <param name="Problem">Why it could not engage, or null when it did.</param>
/// <param name="Stats">Its counters at the end, when they could be read.</param>
public sealed record ShieldOutcome(string? Problem, ShieldStats? Stats, ShieldLimits Limits)
{
    public bool Engaged => Problem is null;
}

/// <summary>How the players fared: online before, at the worst point, after, and who was knocked off.</summary>
/// <param name="AddressesKnown">How many of the online players the bot had an address for.</param>
/// <param name="Dropped">Every player who disconnected during the attack, and when they came back if they did.</param>
/// <param name="StaleSeconds">Seconds the RCON rosters could not be read, when a drop would have gone unseen.</param>
public sealed record PlayerImpact(
    int Before, int Lowest, int After, int AddressesKnown,
    IReadOnlyList<PlayerDrop>? Dropped = null, int StaleSeconds = 0)
{
    public IReadOnlyList<PlayerDrop> Drops => Dropped ?? [];

    public int Rejoined => Drops.Count(d => d.Back is not null);
}

/// <summary>One player knocked off during an attack.</summary>
/// <param name="Back">When they reappeared on a roster, or null when they had not by the end.</param>
public sealed record PlayerDrop(string Name, DateTimeOffset At, DateTimeOffset? Back);

/// <summary>
/// Who disconnected during an attack, from one roster to the next.
/// </summary>
/// <remarks>
/// THE POINT OF THE WHOLE SUMMARY for a game server: the packet counts say how hard it was hit,
/// this says whether anybody felt it. A drop is a name on the roster before the attack (or
/// joined during it) that is missing from a later one; a name that comes back is marked as
/// rejoined rather than counted twice.
///
/// A STALE ROSTER IS NOT A DISCONNECT. RCON is often the first thing a flood takes down, and a
/// roster that could not be read must not report every player as gone - so a stale second is
/// counted as such and compared against nothing.
///
/// Somebody who simply chose to leave mid-attack is counted too; the roster cannot tell the
/// difference, and the report says "disconnected", not "kicked by the attack".
/// </remarks>
public sealed class PlayerDropTracker
{
    private HashSet<string> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset At, DateTimeOffset? Back)> _drops = new(StringComparer.OrdinalIgnoreCase);

    public int StaleSeconds { get; private set; }

    /// <summary>Who is on now, outside an attack: the starting point, with nothing recorded.</summary>
    public void Baseline(IEnumerable<string> online)
    {
        _last = new HashSet<string>(online, StringComparer.OrdinalIgnoreCase);
        _drops.Clear();
        StaleSeconds = 0;
    }

    /// <summary>One roster during an attack.</summary>
    /// <param name="fresh">False when RCON could not be read: the roster is then old and is not compared.</param>
    public void Observe(IEnumerable<string> online, DateTimeOffset at, bool fresh)
    {
        if (!fresh)
        {
            StaleSeconds++;
            return;
        }

        var now = new HashSet<string>(online, StringComparer.OrdinalIgnoreCase);

        foreach (var gone in _last.Where(n => !now.Contains(n)))
        {
            // A second drop after a rejoin starts over; the report keeps the latest.
            _drops[gone] = (at, null);
        }

        foreach (var back in now.Where(n => _drops.TryGetValue(n, out var d) && d.Back is null))
            _drops[back] = (_drops[back].At, at);

        _last = now;
    }

    public IReadOnlyList<PlayerDrop> Drops =>
        [.. _drops.OrderBy(d => d.Value.At).ThenBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
            .Select(d => new PlayerDrop(d.Key, d.Value.At, d.Value.Back))];
}

/// <summary>Whether the breakdown can be trusted, in tshark's own numbers.</summary>
/// <param name="Problem">Why there is no breakdown at all, or null when tshark ran.</param>
/// <param name="Captured">Packets tshark captured.</param>
/// <param name="DroppedByKernel">Packets dropped because tshark could not keep up.</param>
public sealed record CaptureHealth(string? Problem, long Captured = 0, long DroppedByKernel = 0)
{
    public bool Ran => Problem is null;

    /// <summary>Share of the packets tshark saw that it actually got, 0..1.</summary>
    public double Completeness => Captured + DroppedByKernel == 0 ? 1 : (double)Captured / (Captured + DroppedByKernel);
}

/// <summary>
/// One attack, start to finish: the numbers behind the summary embed and the attached report.
/// </summary>
/// <remarks>
/// TWO SOURCES, NAMED APART. The totals, the peak and the timeline come from the interface's own
/// counters, which are exact however hard the box is hit. The breakdown - sources, sizes, ports,
/// pattern - comes from tshark, which can fall behind a big enough flood; when it does, the
/// kernel says by how much, and the report says so rather than presenting a sample as a census.
/// </remarks>
public sealed record AttackReport(
    string Host,
    string Interface,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<LinkSecond> Timeline,
    int ActiveSeconds,
    FloodBreakdown Breakdown,
    PlayerImpact Players,
    CaptureHealth Capture,
    string? PcapPath,
    string? Statistics = null,
    ShieldOutcome? Shield = null,
    string? Iftop = null)
{
    /// <summary>Packets the shield dropped, against the attack's own packets when there is a capture.</summary>
    public double DroppedShare
    {
        get
        {
            if (Shield?.Stats is not { } stats) return 0;
            var whole = Capture.Ran && Breakdown.AttackPackets > 0 ? Breakdown.AttackPackets : LinkPackets;
            return whole == 0 ? 0 : Math.Min(1, (double)stats.Dropped / whole);
        }
    }

    public long LinkPackets => Timeline.Sum(s => s.Packets);

    public long LinkBytes => Timeline.Sum(s => s.Bytes);

    public LinkSecond Peak => Timeline.Count == 0 ? default : Timeline.MaxBy(s => s.Packets);

    public TimeSpan Window => End - Start;

    /// <summary>Average over the seconds that were actually over the trigger, not over the quiet tail.</summary>
    public double AverageActiveMbps =>
        ActiveSeconds == 0 ? 0 : Timeline.OrderByDescending(s => s.Packets).Take(ActiveSeconds).Average(s => s.Mbps);

    public string Pattern => Capture.Ran ? Breakdown.Pattern : "Flood";

    public string Verdict =>
        $"{Pattern} on {Host} · {Duration(TimeSpan.FromSeconds(ActiveSeconds))} · peak {Count(Peak.Packets)} pkt/s, {Peak.Mbps:0.0} Mbit/s" +
        (Shield is { Engaged: true, Stats: not null } ? $" · {Percent(DroppedShare)} dropped by the filter" : "") +
        (Players.Drops.Count > 0 ? $" · {Players.Drops.Count} player(s) dropped" : "");

    /// <summary>The attached report: everything, including the addresses the embed leaves out.</summary>
    public string ToText()
    {
        var b = new StringBuilder();
        b.AppendLine("pavlov bot attack report");
        Line(b, "window", $"{Stamp(Start)} -> {Stamp(End)} ({Duration(Window)})");
        Line(b, "verdict", Verdict);
        Line(b, "interface", $"{Interface} on {Host}");
        b.AppendLine();

        Line(b, "link traffic", $"{Count(LinkPackets)} packets, {Bytes(LinkBytes)}, active {Duration(TimeSpan.FromSeconds(ActiveSeconds))}, avg {AverageActiveMbps:0.0} Mbit/s");
        Line(b, "peak", $"{Count(Peak.Packets)} pkt/s, {Peak.Mbps:0.0} Mbit/s at {Stamp(Peak.At)}");
        b.AppendLine();

        if (!Capture.Ran)
        {
            Line(b, "breakdown", $"none - {Capture.Problem}");
        }
        else
        {
            var d = Breakdown;
            Line(b, "captured", $"{Count(Capture.Captured)} packets, {Count(Capture.DroppedByKernel)} dropped by the kernel ({Percent(Capture.Completeness)} seen)");
            Line(b, "attack packets", $"{Count(d.AttackPackets)}, {Bytes(d.AttackPayloadBytes)} payload, from {Count(d.DistinctSources)} address(es){(d.SourcesCapped ? " (more than tracked)" : "")}");
            Line(b, "protocols", $"UDP {Count(d.Udp)} · TCP {Count(d.Tcp)} (SYN {Count(d.Syn)}) · ICMP {Count(d.Icmp)} · other {Count(d.Other)}");
            Line(b, "sizes", $"{d.MinSize}-{d.MaxSize} B, {d.DistinctSizes} different");
            foreach (var (label, packets) in d.Bands)
                b.AppendLine(CultureInfo.InvariantCulture, $"  {label,-12} {Share(packets, d.AttackPackets),6}  {Count(packets)}");

            b.AppendLine();
            b.AppendLine("targets (destination port)");
            foreach (var (port, packets) in d.TopPorts)
                b.AppendLine(CultureInfo.InvariantCulture, $"  {port,-6} {Share(packets, d.AttackPackets),6}  {Count(packets)}");

            b.AppendLine();
            b.AppendLine(CultureInfo.InvariantCulture, $"top {d.TopSources.Count} sources");
            foreach (var s in d.TopSources)
                b.AppendLine(CultureInfo.InvariantCulture, $"  {s.Address,-15} {Count(s.Packets),12} pkts  {Bytes(s.PayloadBytes),10}");
        }

        b.AppendLine();
        Line(b, "players", $"online {Players.Before} before · {Players.Lowest} lowest · {Players.After} after");
        Line(b, "dropped", Players.Drops.Count == 0
            ? "nobody disconnected during the attack"
            : $"{Players.Drops.Count} disconnected, {Players.Rejoined} rejoined");
        foreach (var drop in Players.Drops)
        {
            b.AppendLine(CultureInfo.InvariantCulture,
                $"  {drop.At.UtcDateTime:HH:mm:ss}  {drop.Name,-32} {(drop.Back is { } back ? $"back at {back.UtcDateTime:HH:mm:ss} ({Duration(back - drop.At)} later)" : "not back")}");
        }
        if (Players.StaleSeconds > 0)
            Line(b, "rosters", $"RCON could not be read for {Players.StaleSeconds}s - a disconnect in that time may be missing");
        Line(b, "player packets", Capture.Ran
            ? $"{Count(Breakdown.PlayerPackets)} from {Players.AddressesKnown} known player address(es) - not counted as attack"
            : "unknown without a capture");
        if (PcapPath is not null) Line(b, "pcap", $"{PcapPath} - /packettools analyze reads it again");

        b.AppendLine();
        if (Shield is null)
        {
            Line(b, "filter", "off (ATTACK_MITIGATION is not on) - nothing was blocked");
        }
        else if (!Shield.Engaged)
        {
            Line(b, "filter", $"could not engage - {Shield.Problem}");
        }
        else
        {
            var l = Shield.Limits;
            Line(b, "filter", $"nftables: over {Count(l.PerSourcePps)} pkt/s from one address blocked for {l.BlockMinutes} min; unknown traffic to {l.GamePorts} capped at {Count(l.UnknownPps)} pkt/s");
            if (Shield.Stats is { } s)
            {
                Line(b, "dropped", $"{Count(s.Dropped)} packets ({Percent(DroppedShare)}): {Count(s.BlockedPackets)} from blocked addresses, {Count(s.PerSourcePackets)} over the per-address limit, {Count(s.CappedPackets)} over the unknown-traffic cap");
                Line(b, "blocked", $"{Count(s.BlockedSources)} address(es) on the block list now");
            }
        }

        if (!string.IsNullOrWhiteSpace(Iftop))
        {
            b.AppendLine();
            b.AppendLine("iftop -nNP, a few seconds into the attack (bandwidth by connection)");
            b.AppendLine(Iftop.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(Statistics))
        {
            b.AppendLine();
            b.AppendLine("tshark statistics of the pcap (Wireshark's Statistics menu)");
            b.AppendLine(Statistics.TrimEnd());
        }

        b.AppendLine();
        b.AppendLine("timeline (packets/s, Mbit/s)");
        foreach (var s in Timeline)
            b.AppendLine(CultureInfo.InvariantCulture, $"  {s.At.UtcDateTime:HH:mm:ss}  {s.Packets,9}  {s.Mbps,8:0.0}");

        return b.ToString();
    }

    private static void Line(StringBuilder b, string label, string value) =>
        b.AppendLine(CultureInfo.InvariantCulture, $"{label,-16}{value}");

    public static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public static string Duration(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m {span.Seconds}s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds}s");

    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string Share(long part, long whole) => whole == 0 ? "0.0%" : Percent((double)part / whole);

    /// <summary>"97.2%": the invariant culture's P1 puts a space before the sign, which reads oddly.</summary>
    public static string Percent(double fraction) =>
        fraction.ToString("P1", CultureInfo.InvariantCulture).Replace(" ", "", StringComparison.Ordinal);

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0.0} MB"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e3:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    /// <summary>A one-line chart of <paramref name="values"/>, squeezed into <paramref name="width"/> blocks.</summary>
    public static string Sparkline(IReadOnlyList<long> values, int width = 40)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return "";

        const string blocks = "▁▂▃▄▅▆▇█";
        var buckets = Math.Min(width, values.Count);
        var max = Math.Max(1, values.Max());
        var b = new StringBuilder(buckets);
        for (var i = 0; i < buckets; i++)
        {
            var from = i * values.Count / buckets;
            var to = Math.Max(from + 1, (i + 1) * values.Count / buckets);
            var peak = values.Skip(from).Take(to - from).Max();
            b.Append(blocks[(int)Math.Round((double)peak / max * (blocks.Length - 1))]);
        }
        return b.ToString();
    }

    /// <summary>A bar for a share, <paramref name="width"/> blocks long at 100%.</summary>
    public static string Bar(long part, long whole, int width = 20) =>
        whole <= 0 ? "" : new string('█', (int)Math.Round((double)part / whole * width));
}
