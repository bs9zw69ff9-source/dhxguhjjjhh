using System.Globalization;

namespace PavlovBot.Host.Network;

/// <summary>
/// An interface's inbound packet and byte counters, straight from the kernel.
/// </summary>
/// <remarks>
/// <c>/sys/class/net/&lt;if&gt;/statistics</c> rather than a capture, because these are EXACT
/// at any rate: the driver counts every frame it receives, so a flood that tshark cannot keep
/// up with is still measured to the packet. Reading them costs two small file reads a second,
/// which is why detection runs on these all the time and the capture only during an attack.
/// </remarks>
public static class LinkCounters
{
    public const string SysClassNet = "/sys/class/net";

    /// <summary>Received packets and bytes so far, or null when the interface cannot be read.</summary>
    public static (long Packets, long Bytes)? Read(string iface, string root = SysClassNet)
    {
        if (!IsPlausibleInterface(iface)) return null;

        var stats = Path.Combine(root, iface, "statistics");
        return Number(Path.Combine(stats, "rx_packets")) is { } packets && Number(Path.Combine(stats, "rx_bytes")) is { } bytes
            ? (packets, bytes)
            : null;
    }

    /// <summary>
    /// The interface the default route leaves by, from <c>/proc/net/route</c>.
    /// </summary>
    /// <remarks>
    /// That is the one the internet reaches the servers through, so it is where a flood arrives.
    /// The default route is the line whose destination is <c>00000000</c>.
    /// </remarks>
    public static string? DefaultInterface(string routeTable = "/proc/net/route")
    {
        try
        {
            foreach (var line in File.ReadLines(routeTable).Skip(1))
            {
                var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length > 2 && fields[1] == "00000000" && IsPlausibleInterface(fields[0])) return fields[0];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No route table to read: the caller falls back to ATTACK_INTERFACE or gives up.
        }

        return null;
    }

    /// <summary>
    /// A Linux interface name: up to 15 characters, nothing that could be a path or a flag.
    /// </summary>
    /// <remarks>It becomes both a path segment here and an argument to tshark, so it is allow-listed.</remarks>
    public static bool IsPlausibleInterface(string? name) =>
        name is { Length: > 0 and <= 15 } &&
        char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@') &&
        name is not "." and not "..";

    private static long? Number(string path)
    {
        try
        {
            return long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
