using System.Globalization;
using System.Text;

namespace PavlovBot.Core.Network;

/// <summary>
/// Every source address that sent anything, a minute at a time: the always-on record behind the
/// question "who was hitting us at 13:52?" asked after the fact.
/// </summary>
/// <remarks>
/// ALWAYS ON, NOT ONLY DURING AN ATTACK. Detection can miss an attack whole: a host that
/// null-routes the server under a flood cuts its traffic to zero, which looks like quiet, and a
/// flood that fills the uplink can take the bot's own connection down with it. A minute-by-minute
/// log that was being written all along has the data either way.
///
/// ONE CSV LINE PER SOURCE PER MINUTE, the busiest first and capped at <see cref="TopPerMinute"/>,
/// plus a <c>*</c> line with the minute's totals - so a spoofed flood from a hundred thousand
/// addresses is a hundred lines a minute, not a hundred thousand, and the totals still say how
/// many there were. Plain text, so <c>grep 203.0.113.5</c> answers "was this address in it".
///
/// BOUNDED in memory the same way as <see cref="FloodRecorder"/>: past <see cref="MaxSources"/>
/// distinct addresses in one minute, the rest are counted in the totals but not tracked singly.
/// </remarks>
public sealed class SourceLog
{
    public const int MaxSources = 100_000;
    public const int TopPerMinute = 100;

    /// <summary>The first line of every daily file.</summary>
    public const string Header = "minute_utc,source,packets,payload_bytes,top_port,protocol,player";

    private readonly object _gate = new();
    private Dictionary<uint, Entry> _sources = [];
    private long _packets;
    private long _bytes;
    private long _untracked;

    private sealed class Entry
    {
        public long Packets;
        public long Bytes;
        public bool Player;
        public readonly Dictionary<ushort, long> Ports = [];
        public readonly long[] Protocols = new long[4];
    }

    /// <summary>Count one packet into the current minute.</summary>
    public void Add(in CapturedPacket packet, bool fromPlayer)
    {
        lock (_gate)
        {
            _packets++;
            _bytes += packet.Payload;

            if (!_sources.TryGetValue(packet.Source, out var entry))
            {
                if (_sources.Count >= MaxSources)
                {
                    _untracked++;
                    return;
                }
                entry = new Entry();
                _sources[packet.Source] = entry;
            }

            entry.Packets++;
            entry.Bytes += packet.Payload;
            entry.Player |= fromPlayer;
            entry.Protocols[(int)packet.Protocol]++;

            // A spoofed flood rotates ports per packet; a handful per source is all that is worth keeping.
            if (packet.DestinationPort != 0 && (entry.Ports.Count < 16 || entry.Ports.ContainsKey(packet.DestinationPort)))
                entry.Ports[packet.DestinationPort] = entry.Ports.GetValueOrDefault(packet.DestinationPort) + 1;
        }
    }

    /// <summary>
    /// The minute's lines, and a clean slate for the next. Empty when nothing arrived.
    /// </summary>
    public IReadOnlyList<string> Flush(DateTimeOffset minute)
    {
        Dictionary<uint, Entry> sources;
        long packets, bytes, untracked;
        lock (_gate)
        {
            (sources, packets, bytes, untracked) = (_sources, _packets, _bytes, _untracked);
            _sources = [];
            (_packets, _bytes, _untracked) = (0, 0, 0);
        }

        if (packets == 0) return [];

        var stamp = minute.UtcDateTime.ToString("yyyy-MM-ddTHH:mmZ", CultureInfo.InvariantCulture);
        var lines = new List<string>(Math.Min(sources.Count, TopPerMinute) + 1)
        {
            string.Create(CultureInfo.InvariantCulture,
                $"{stamp},*,{packets},{bytes},,{sources.Count}{(untracked > 0 ? "+" : "")} sources,"),
        };

        foreach (var (source, e) in sources.OrderByDescending(s => s.Value.Packets).Take(TopPerMinute))
        {
            var port = e.Ports.Count == 0 ? "" : e.Ports.MaxBy(p => p.Value).Key.ToString(CultureInfo.InvariantCulture);
            var protocol = (PacketProtocol)Array.IndexOf(e.Protocols, e.Protocols.Max());
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{stamp},{CapturedPacket.Address(source)},{e.Packets},{e.Bytes},{port},{protocol.ToString().ToLowerInvariant()},{(e.Player ? 1 : 0)}"));
        }

        return lines;
    }

    /// <summary>The daily file a minute belongs to: <c>sources-2026-10-29.csv</c>.</summary>
    public static string FileName(DateTimeOffset minute) =>
        "sources-" + minute.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";

    /// <summary>Whether a source log line is about <paramref name="address"/>: its second field, exactly.</summary>
    public static bool IsFor(string line, string address)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(address);
        var comma = line.IndexOf(',', StringComparison.Ordinal);
        return comma >= 0 && line.AsSpan(comma + 1).StartsWith(address + ",", StringComparison.Ordinal);
    }

    /// <summary>What a set of an address's lines adds up to.</summary>
    public readonly record struct Totals(int Minutes, long Packets, long Bytes, string? First, string? Last, bool Player);

    /// <summary>Totals across <paramref name="lines"/>, which are in time order: minutes seen, packets, bytes, first and last.</summary>
    public static Totals Summarise(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var minutes = 0;
        long packets = 0, bytes = 0;
        string? first = null, last = null;
        var player = false;
        foreach (var line in lines)
        {
            var f = line.Split(',');
            if (f.Length < 7) continue;
            minutes++;
            packets += long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0;
            bytes += long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : 0;
            player |= f[6] == "1";
            first ??= f[0];
            last = f[0];
        }
        return new Totals(minutes, packets, bytes, first, last, player);
    }

    /// <summary>Lines as file text, one per line.</summary>
    public static string Join(IEnumerable<string> lines)
    {
        var b = new StringBuilder();
        foreach (var line in lines) b.Append(line).Append('\n');
        return b.ToString();
    }
}
