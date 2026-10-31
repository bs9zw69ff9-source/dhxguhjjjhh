using System.Globalization;
using PavlovBot.Core.Network;

namespace PavlovBot.Host.Network;

/// <summary>Reads <see cref="LinkHealth"/> from <c>/sys</c> and <c>/proc</c>: a few small files, once a minute.</summary>
public static class LinkHealthReader
{
    /// <summary>The interface's drop counters and state, the kernel's input-queue and conntrack drops. Null when the interface cannot be read.</summary>
    public static LinkHealth? Read(string iface, string sys = LinkCounters.SysClassNet, string proc = "/proc")
    {
        if (!LinkCounters.IsPlausibleInterface(iface)) return null;
        var stats = Path.Combine(sys, iface, "statistics");
        if (!Directory.Exists(stats)) return null;

        long Stat(string name) => Number(Path.Combine(stats, name));
        return new LinkHealth(
            Stat("rx_dropped"), Stat("rx_missed_errors"), Stat("rx_fifo_errors"), Stat("rx_over_errors"),
            LinkHealth.SoftnetDrops(Text(Path.Combine(proc, "net", "softnet_stat"))),
            LinkHealth.ConntrackDropCount(Text(Path.Combine(proc, "net", "stat", "nf_conntrack"))),
            Number(Path.Combine(proc, "sys", "net", "netfilter", "nf_conntrack_count")),
            Number(Path.Combine(proc, "sys", "net", "netfilter", "nf_conntrack_max")),
            Text(Path.Combine(sys, iface, "operstate"))?.Trim() is { Length: > 0 } state ? state : "unknown");
    }

    private static string? Text(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long Number(string path) =>
        long.TryParse(Text(path)?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
