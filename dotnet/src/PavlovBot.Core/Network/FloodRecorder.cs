using System.Globalization;
using System.Net;

namespace PavlovBot.Core.Network;

public enum PacketProtocol
{
    Udp,
    Tcp,
    Icmp,
    Other,
}

/// <summary>One inbound packet as tcpdump described it.</summary>
/// <param name="Source">The sender's IPv4 address.</param>
/// <param name="DestinationPort">0 for ICMP, fragments and anything without ports.</param>
/// <param name="Payload">Bytes after the transport header, as tcpdump's "length" reports it.</param>
/// <param name="Syn">A TCP SYN without an ACK: a new connection attempt.</param>
public readonly record struct CapturedPacket(
    uint Source, ushort DestinationPort, PacketProtocol Protocol, int Payload, bool Syn = false)
{
    /// <summary>Dotted-quad text for an address held as a big-endian number.</summary>
    public static string Address(uint source) => string.Create(CultureInfo.InvariantCulture,
        $"{source >> 24}.{(source >> 16) & 0xFF}.{(source >> 8) & 0xFF}.{source & 0xFF}");

    /// <summary>An address as the number <see cref="Source"/> holds, or null when it is not IPv4.</summary>
    public static uint? Number(string? address)
    {
        if (!IPAddress.TryParse(address?.Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <summary>
    /// One line of <c>tcpdump -n -t</c>, or null when it is not an IPv4 packet line.
    /// </summary>
    /// <remarks>
    /// The shapes, from tcpdump 4.99:
    /// <code>
    /// IP 203.0.113.5.51234 > 10.0.0.2.7777: UDP, length 1200
    /// IP 203.0.113.5.40000 > 10.0.0.2.22: Flags [S], seq 1, win 64240, options [...], length 0
    /// IP 203.0.113.5 > 10.0.0.2: ICMP echo request, id 1, seq 1, length 64
    /// IP 203.0.113.5 > 10.0.0.2: ip-proto-17
    /// </code>
    /// The last is a non-first fragment, which carries no ports: counted, as Other.
    ///
    /// A LINE IS UNTRUSTED TEXT. It comes from a packet an attacker chose, through tcpdump's own
    /// printing, so nothing here assumes a field is present: anything that does not fit is
    /// dropped rather than guessed at.
    /// </remarks>
    public static CapturedPacket? Parse(string? line)
    {
        if (line is null || !line.StartsWith("IP ", StringComparison.Ordinal)) return null;

        var colon = line.IndexOf(": ", 3, StringComparison.Ordinal);
        if (colon < 0) return null;

        var arrow = line.IndexOf(" > ", 3, StringComparison.Ordinal);
        if (arrow < 0 || arrow > colon) return null;

        var from = line[3..arrow];
        var to = line[(arrow + 3)..colon];
        var rest = line[(colon + 2)..];

        PacketProtocol protocol;
        bool hasPorts;
        if (rest.StartsWith("UDP", StringComparison.Ordinal)) (protocol, hasPorts) = (PacketProtocol.Udp, true);
        else if (rest.StartsWith("Flags [", StringComparison.Ordinal)) (protocol, hasPorts) = (PacketProtocol.Tcp, true);
        else if (rest.StartsWith("ICMP", StringComparison.Ordinal)) (protocol, hasPorts) = (PacketProtocol.Icmp, false);
        else (protocol, hasPorts) = (PacketProtocol.Other, false);

        var source = Number(hasPorts ? StripPort(from) : from);
        if (source is null) return null;

        ushort port = 0;
        if (hasPorts)
        {
            var dot = to.LastIndexOf('.');
            if (dot < 0 || !ushort.TryParse(to.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port))
                return null;
        }

        var syn = protocol == PacketProtocol.Tcp &&
                  rest.Length > 8 && rest.IndexOf(']', 7) is var close and > 7 &&
                  rest[7..close] is var flags && flags.Contains('S') && !flags.Contains('.');

        return new CapturedPacket(source.Value, port, protocol, LastLength(rest), syn);
    }

    private static string StripPort(string endpoint)
    {
        var dot = endpoint.LastIndexOf('.');
        return dot < 0 ? endpoint : endpoint[..dot];
    }

    /// <summary>The number after the LAST "length ", which is the payload; 0 when there is none.</summary>
    private static int LastLength(string rest)
    {
        var at = rest.LastIndexOf("length ", StringComparison.Ordinal);
        if (at < 0) return 0;

        var start = at + "length ".Length;
        var end = start;
        while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
        return int.TryParse(rest.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}

/// <summary>
/// The breakdown of an attack: who sent it, how big, at what, and how much reached players.
/// </summary>
/// <remarks>
/// FED FROM ONE THREAD, READ FROM ANOTHER - tcpdump's reader adds, the monitor's timer reads -
/// so every member takes the same lock. At tens of thousands of packets a second an
/// uncontended lock is a rounding error next to reading the line in the first place.
///
/// BOUNDED. A spoofed flood can come from millions of addresses, and a dictionary with one
/// entry per address would be the attack taking the bot's memory instead of the game's. Past
/// <see cref="MaxSources"/> distinct senders, new ones are counted together rather than tracked.
/// </remarks>
public sealed class FloodRecorder(int maxSources = FloodRecorder.MaxSources)
{
    public const int MaxSources = 100_000;

    /// <summary>The payload bands the report draws, with their upper bounds.</summary>
    public static readonly (string Label, int Max)[] SizeBands =
    [
        ("0-128 B", 128),
        ("129-400 B", 400),
        ("401-600 B", 600),
        ("601-1024 B", 1024),
        (">1024 B", int.MaxValue),
    ];

    private readonly object _gate = new();
    private readonly Dictionary<uint, (long Packets, long Bytes)> _sources = [];
    private readonly Dictionary<ushort, long> _ports = [];
    private readonly long[] _bands = new long[SizeBands.Length];
    private readonly long[] _protocols = new long[4];
    private readonly System.Collections.BitArray _sizesSeen = new(65536);
    private long _untrackedPackets;
    private long _attackPackets;
    private long _attackBytes;
    private long _playerPackets;
    private long _syn;
    private int _distinctSizes;
    private int _minSize = int.MaxValue;
    private int _maxSize;

    /// <summary>Count one packet. A packet from a known player's address is counted apart from the attack.</summary>
    public void Add(in CapturedPacket packet, bool fromPlayer)
    {
        lock (_gate)
        {
            if (fromPlayer)
            {
                _playerPackets++;
                return;
            }

            _attackPackets++;
            _attackBytes += packet.Payload;
            _protocols[(int)packet.Protocol]++;
            if (packet.Syn) _syn++;

            if (_sources.TryGetValue(packet.Source, out var seen))
                _sources[packet.Source] = (seen.Packets + 1, seen.Bytes + packet.Payload);
            else if (_sources.Count < maxSources)
                _sources[packet.Source] = (1, packet.Payload);
            else
                _untrackedPackets++;

            if (packet.DestinationPort != 0)
                _ports[packet.DestinationPort] = _ports.GetValueOrDefault(packet.DestinationPort) + 1;

            var size = Math.Clamp(packet.Payload, 0, 65535);
            for (var i = 0; i < SizeBands.Length; i++)
            {
                if (size > SizeBands[i].Max) continue;
                _bands[i]++;
                break;
            }

            if (!_sizesSeen[size])
            {
                _sizesSeen[size] = true;
                _distinctSizes++;
            }
            _minSize = Math.Min(_minSize, size);
            _maxSize = Math.Max(_maxSize, size);
        }
    }

    /// <summary>Everything counted so far, as an immutable copy.</summary>
    public FloodBreakdown Snapshot(int topSources = 50, int topPorts = 10)
    {
        lock (_gate)
        {
            return new FloodBreakdown(
                AttackPackets: _attackPackets,
                AttackPayloadBytes: _attackBytes,
                PlayerPackets: _playerPackets,
                DistinctSources: _sources.Count,
                SourcesCapped: _untrackedPackets > 0,
                TopSources: [.. _sources.OrderByDescending(s => s.Value.Packets).Take(topSources)
                    .Select(s => new SourceCount(CapturedPacket.Address(s.Key), s.Value.Packets, s.Value.Bytes))],
                TopPorts: [.. _ports.OrderByDescending(p => p.Value).Take(topPorts).Select(p => (p.Key, p.Value))],
                Bands: [.. SizeBands.Select((b, i) => (b.Label, _bands[i]))],
                Udp: _protocols[(int)PacketProtocol.Udp],
                Tcp: _protocols[(int)PacketProtocol.Tcp],
                Icmp: _protocols[(int)PacketProtocol.Icmp],
                Other: _protocols[(int)PacketProtocol.Other],
                Syn: _syn,
                DistinctSizes: _distinctSizes,
                MinSize: _attackPackets == 0 ? 0 : _minSize,
                MaxSize: _maxSize);
        }
    }
}

public sealed record SourceCount(string Address, long Packets, long PayloadBytes);

/// <summary>What <see cref="FloodRecorder"/> counted.</summary>
public sealed record FloodBreakdown(
    long AttackPackets,
    long AttackPayloadBytes,
    long PlayerPackets,
    int DistinctSources,
    bool SourcesCapped,
    IReadOnlyList<SourceCount> TopSources,
    IReadOnlyList<(ushort Port, long Packets)> TopPorts,
    IReadOnlyList<(string Label, long Packets)> Bands,
    long Udp,
    long Tcp,
    long Icmp,
    long Other,
    long Syn,
    int DistinctSizes,
    int MinSize,
    int MaxSize)
{
    public static FloodBreakdown Empty { get; } = new FloodRecorder().Snapshot();

    /// <summary>
    /// What kind of flood this was, in the words an operator would use.
    /// </summary>
    /// <remarks>
    /// By the dominant protocol, then by size for UDP: big packets fill the pipe (bandwidth), small
    /// ones exhaust the CPU and the NIC (packet rate), and the two are mitigated differently.
    /// </remarks>
    public string Pattern
    {
        get
        {
            if (AttackPackets == 0) return "Flood";
            var share = (double)Math.Max(Math.Max(Udp, Tcp), Math.Max(Icmp, Other)) / AttackPackets;
            if (share < 0.8) return "Mixed flood";
            if (Tcp >= Udp && Tcp >= Icmp && Tcp >= Other) return Syn * 2 >= Tcp ? "TCP SYN flood" : "TCP flood";
            if (Icmp >= Udp && Icmp >= Other) return "ICMP flood";
            if (Other >= Udp) return "Fragment flood";

            var big = Bands.Where(b => b.Label is "601-1024 B" or ">1024 B").Sum(b => b.Packets);
            var small = Bands.Where(b => b.Label is "0-128 B").Sum(b => b.Packets);
            return big * 2 >= AttackPackets ? "Big-packet UDP flood"
                : small * 2 >= AttackPackets ? "Small-packet UDP flood"
                : "UDP flood";
        }
    }
}
