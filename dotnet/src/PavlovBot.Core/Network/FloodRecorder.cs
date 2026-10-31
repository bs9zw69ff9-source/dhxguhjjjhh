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

/// <summary>One inbound packet as tshark described it.</summary>
/// <param name="Source">The sender's IPv4 address.</param>
/// <param name="DestinationPort">0 for ICMP, fragments and anything without ports.</param>
/// <param name="Payload">Bytes after the transport header (after the IP header for ICMP and fragments).</param>
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
    /// The fields tshark prints for each packet, in order, by <see cref="ParseFields"/>.
    /// </summary>
    /// <remarks>
    /// <c>ip.len</c> is the whole IP packet, used for ICMP and fragments, which have no transport
    /// length of their own; <c>udp.length</c> includes UDP's 8-byte header.
    /// </remarks>
    public static readonly string[] TsharkFields =
    [
        "ip.src", "ip.proto", "udp.dstport", "tcp.dstport", "udp.length", "tcp.len",
        "tcp.flags.syn", "tcp.flags.ack", "ip.len", "ip.frag_offset",
    ];

    /// <summary>
    /// One line of <c>tshark -T fields -E separator=, -E occurrence=f</c> with <see cref="TsharkFields"/>,
    /// or null when it is not one.
    /// </summary>
    /// <remarks>
    /// From tshark 4.2 on a live interface:
    /// <code>
    /// 203.0.113.5,17,7777,,1208,,,,1228,0        UDP to 7777, 1,200 bytes of payload
    /// 203.0.113.5,6,,22,,0,True,False,60,0       a TCP SYN to 22
    /// 127.0.0.1,1,7779,,1224,,,,576,0            ICMP port-unreachable
    /// </code>
    /// THE PROTOCOL NUMBER DECIDES WHICH FIELDS COUNT. An ICMP error quotes the packet it is about,
    /// so tshark fills in the quoted packet's UDP port and length too - the third line is not UDP
    /// traffic to 7779, and taking the UDP fields because they are present would say it was.
    /// <c>occurrence=f</c> keeps tshark to the first (outer) value of each field for the same reason.
    ///
    /// A LINE IS UNTRUSTED TEXT. Its values come from a packet an attacker chose, so a field that
    /// does not parse drops the line rather than being guessed at.
    /// </remarks>
    public static CapturedPacket? ParseFields(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var f = line.Trim().Split(',');
        if (f.Length < TsharkFields.Length) return null;

        if (Number(f[0]) is not { } source) return null;
        if (!int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var proto)) return null;

        var fragment = int.TryParse(f[9], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset > 0;
        var ipLength = int.TryParse(f[8], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ? total : 0;
        var withoutIpHeader = Math.Max(0, ipLength - 20);

        // A non-first fragment has no transport header of its own, whatever its protocol says.
        if (fragment) return new CapturedPacket(source, 0, PacketProtocol.Other, withoutIpHeader);

        switch (proto)
        {
            case 17:
                if (!ushort.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var udpPort)) return null;
                var udpLength = int.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out var ul) ? ul : 8;
                return new CapturedPacket(source, udpPort, PacketProtocol.Udp, Math.Max(0, udpLength - 8));

            case 6:
                if (!ushort.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var tcpPort)) return null;
                var tcpLength = int.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var tl) ? tl : 0;
                return new CapturedPacket(source, tcpPort, PacketProtocol.Tcp, tcpLength, IsSet(f[6]) && !IsSet(f[7]));

            case 1:
                return new CapturedPacket(source, 0, PacketProtocol.Icmp, withoutIpHeader);

            default:
                return new CapturedPacket(source, 0, PacketProtocol.Other, withoutIpHeader);
        }
    }

    /// <summary>tshark prints a flag as True/False, and older releases as 1/0.</summary>
    private static bool IsSet(string value) =>
        value.Trim() is "1" || value.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The breakdown of an attack: who sent it, how big, at what, and how much reached players.
/// </summary>
/// <remarks>
/// FED FROM ONE THREAD, READ FROM ANOTHER - tshark's reader adds, the monitor's timer reads -
/// so every member takes the same lock. At tens of thousands of packets a second an
/// uncontended lock is a rounding error next to reading the line in the first place.
///
/// BOUNDED. A spoofed flood can come from millions of addresses, and a dictionary with one
/// entry per address would be the attack taking the bot's memory instead of the game's. Past
/// <see cref="MaxSources"/> distinct senders, new ones are counted together rather than tracked.
/// </remarks>
/// <param name="targeted">Whether a destination port is one of the game's: a source's packets to those are counted apart, for banning.</param>
public sealed class FloodRecorder(int maxSources = FloodRecorder.MaxSources, Func<ushort, bool>? targeted = null)
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
    private readonly Dictionary<uint, (long Packets, long Bytes, long Targeted)> _sources = [];
    private readonly Dictionary<ushort, (long Packets, long Bytes)> _ports = [];
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

            var hit = packet.DestinationPort != 0 && packet.Protocol is PacketProtocol.Udp or PacketProtocol.Tcp
                                                   && targeted?.Invoke(packet.DestinationPort) == true ? 1 : 0;
            if (_sources.TryGetValue(packet.Source, out var seen))
                _sources[packet.Source] = (seen.Packets + 1, seen.Bytes + packet.Payload, seen.Targeted + hit);
            else if (_sources.Count < maxSources)
                _sources[packet.Source] = (1, packet.Payload, hit);
            else
                _untrackedPackets++;

            if (packet.DestinationPort != 0)
            {
                var port = _ports.GetValueOrDefault(packet.DestinationPort);
                _ports[packet.DestinationPort] = (port.Packets + 1, port.Bytes + packet.Payload);
            }

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
    /// <summary>Attack packets and payload bytes so far, for a per-second series.</summary>
    public (long Packets, long Bytes) Totals
    {
        get
        {
            lock (_gate) return (_attackPackets, _attackBytes);
        }
    }

    /// <summary>
    /// Every recorded source that sent at least <paramref name="minPackets"/> packets to the game's
    /// ports, busiest first. These are what the attack was made of; a server browser's query or a
    /// monitoring ping sends a handful, and replies to the server's own connections go to other ports.
    /// </summary>
    public IReadOnlyList<string> TargetingSources(long minPackets)
    {
        lock (_gate)
        {
            return [.. _sources.Where(s => s.Value.Targeted >= Math.Max(1, minPackets))
                .OrderByDescending(s => s.Value.Targeted)
                .Select(s => CapturedPacket.Address(s.Key))];
        }
    }

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
                TopPorts: [.. _ports.OrderByDescending(p => p.Value.Packets).Take(topPorts).Select(p => (p.Key, p.Value.Packets))],
                Bands: [.. SizeBands.Select((b, i) => (b.Label, _bands[i]))],
                Udp: _protocols[(int)PacketProtocol.Udp],
                Tcp: _protocols[(int)PacketProtocol.Tcp],
                Icmp: _protocols[(int)PacketProtocol.Icmp],
                Other: _protocols[(int)PacketProtocol.Other],
                Syn: _syn,
                DistinctSizes: _distinctSizes,
                MinSize: _attackPackets == 0 ? 0 : _minSize,
                MaxSize: _maxSize,
                PortBytes: _ports.OrderByDescending(p => p.Value.Packets).Take(topPorts).ToDictionary(p => p.Key, p => p.Value.Bytes));
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
    int MaxSize,
    IReadOnlyDictionary<ushort, long>? PortBytes = null)
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
