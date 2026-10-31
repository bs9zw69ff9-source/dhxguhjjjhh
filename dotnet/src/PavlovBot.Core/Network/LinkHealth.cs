using System.Globalization;

namespace PavlovBot.Core.Network;

/// <summary>
/// What the kernel lost before anything could count it, and whether the link is up.
/// </summary>
/// <remarks>
/// THE PACKETS NOBODY SAW. A flood that outruns the network card or the kernel's input queue is
/// dropped there - before nftables, before tshark, before any per-source counter - and the only
/// trace is these counters. Under a flood that fills the link they are often the most telling
/// numbers in the log: the interface's own received count stays high while the drops climb.
///
/// CUMULATIVE COUNTERS AND GAUGES. The NIC, softnet and conntrack drop counts only grow (until a
/// reboot); <see cref="Since"/> turns two readings into what happened in between. The conntrack
/// table size and the link state are gauges, kept as they are.
/// </remarks>
/// <param name="NicDropped">rx_dropped: frames the driver received and discarded.</param>
/// <param name="NicMissed">rx_missed_errors: frames the card had no room for.</param>
/// <param name="NicFifo">rx_fifo_errors: the card's receive FIFO overran.</param>
/// <param name="NicOver">rx_over_errors: the receive ring overran.</param>
/// <param name="SoftnetDropped">Packets the kernel's per-CPU input queue (backlog) dropped, all CPUs.</param>
/// <param name="ConntrackDrops">Connection tracking insert_failed + drop + early_drop: new flows turned away because the table was full.</param>
/// <param name="ConntrackCount">Connections being tracked now.</param>
/// <param name="ConntrackMax">The table's size.</param>
/// <param name="OperState">The link's state: up, down, unknown.</param>
public sealed record LinkHealth(
    long NicDropped,
    long NicMissed,
    long NicFifo,
    long NicOver,
    long SoftnetDropped,
    long ConntrackDrops,
    long ConntrackCount,
    long ConntrackMax,
    string OperState)
{
    /// <summary>Every packet lost before the firewall, of every kind.</summary>
    public long Lost => NicDropped + NicMissed + NicFifo + NicOver + SoftnetDropped + ConntrackDrops;

    public bool Up => OperState is "up" or "unknown";

    /// <summary>How full the connection-tracking table is, 0-1. Full, new connections are dropped.</summary>
    public double ConntrackFill => ConntrackMax <= 0 ? 0 : (double)ConntrackCount / ConntrackMax;

    /// <summary>What happened since <paramref name="before"/>: counters as differences, gauges as now. A counter that went down was reset by a reboot.</summary>
    public LinkHealth Since(LinkHealth? before)
    {
        if (before is null) return this;
        static long D(long now, long then) => now >= then ? now - then : now;
        return this with
        {
            NicDropped = D(NicDropped, before.NicDropped),
            NicMissed = D(NicMissed, before.NicMissed),
            NicFifo = D(NicFifo, before.NicFifo),
            NicOver = D(NicOver, before.NicOver),
            SoftnetDropped = D(SoftnetDropped, before.SoftnetDropped),
            ConntrackDrops = D(ConntrackDrops, before.ConntrackDrops),
        };
    }

    /// <summary>
    /// Dropped packets from <c>/proc/net/softnet_stat</c>: one line per CPU, hexadecimal, the second column.
    /// </summary>
    public static long SoftnetDrops(string? text) =>
        Lines(text).Sum(fields => fields.Length > 1 ? Hex(fields[1]) : 0);

    /// <summary>
    /// insert_failed + drop + early_drop from <c>/proc/net/stat/nf_conntrack</c>: a header naming the
    /// columns, then one hexadecimal line per CPU. Read by name, since the columns vary by kernel.
    /// </summary>
    public static long ConntrackDropCount(string? text)
    {
        var lines = Lines(text).ToList();
        if (lines.Count < 2) return 0;
        var header = lines[0];
        int[] columns = [.. new[] { "insert_failed", "drop", "early_drop" }.Select(n => Array.IndexOf(header, n)).Where(i => i >= 0)];
        return lines.Skip(1).Sum(fields => columns.Sum(i => i < fields.Length ? Hex(fields[i]) : 0));
    }

    private static IEnumerable<string[]> Lines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', '\t').Where(f => f.Length > 0).ToArray())
            .Where(f => f.Length > 0);

    private static long Hex(string field) =>
        long.TryParse(field, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
