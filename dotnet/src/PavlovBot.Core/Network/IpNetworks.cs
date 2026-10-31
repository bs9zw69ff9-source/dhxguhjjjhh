using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PavlovBot.Core.Network;

/// <summary>
/// Addresses and networks typed by a person, made safe to hand to nft and cscli.
/// </summary>
/// <remarks>
/// PARSED, NEVER PATTERN-MATCHED. Only what <see cref="IPAddress"/> accepts gets through, and what
/// comes out is the canonical text, never the input - so <c>010.0.0.1</c>, a trailing script or an
/// IPv4-mapped IPv6 spelling can never reach a command line or two lists disagree on one address.
/// </remarks>
public static class IpNetworks
{
    /// <summary>The widest network an owner may trust: a /24 or a /48. Anything wider is a hole, not an allow list.</summary>
    public const int WidestTrusted4 = 24;
    public const int WidestTrusted6 = 48;

    /// <summary>The widest network an owner may block by hand: one rack of a hosting provider, not the provider.</summary>
    public const int WidestBlocked4 = 24;
    public const int WidestBlocked6 = 48;

    /// <summary>An address or network in its one canonical form.</summary>
    /// <param name="Family">4 or 6.</param>
    /// <param name="Address">The network's first address, canonical.</param>
    /// <param name="PrefixLength">32 or 128 for a single address.</param>
    public sealed record Network(int Family, string Address, int PrefixLength)
    {
        public bool IsSingle => PrefixLength == (Family == 4 ? 32 : 128);

        /// <summary>The address alone, or <c>address/prefix</c>.</summary>
        public string Text => IsSingle ? Address : $"{Address}/{PrefixLength.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>Whether it is no wider than <paramref name="widest4"/> / <paramref name="widest6"/>.</summary>
        public bool NoWiderThan(int widest4, int widest6) => PrefixLength >= (Family == 4 ? widest4 : widest6);

        /// <summary>Whether <paramref name="address"/> is in this network.</summary>
        public bool Contains(string address)
        {
            if (!IPAddress.TryParse(address, out var ip)) return false;
            ip = Unmap(ip);
            if ((ip.AddressFamily == AddressFamily.InterNetwork ? 4 : 6) != Family) return false;
            return new IPNetwork(IPAddress.Parse(Address), PrefixLength).Contains(ip);
        }
    }

    /// <summary>
    /// The canonical form of an address (<c>203.0.113.5</c>) or network (<c>203.0.113.0/24</c>),
    /// or null when it is neither - including a network written with host bits set, which is
    /// refused rather than silently widened or narrowed.
    /// </summary>
    public static Network? Canonical(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.Length > 64) return null;

        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? trimmed : trimmed[..slash];
        if (!IPAddress.TryParse(addressText, out var address)) return null;
        // TryParse also accepts "1" and "1.2" (inet_aton forms); a person typing those meant something else.
        if (address.AddressFamily == AddressFamily.InterNetwork && addressText.Count(c => c == '.') != 3) return null;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return null;

        var mapped = address.IsIPv4MappedToIPv6;
        address = Unmap(address);
        var family = address.AddressFamily == AddressFamily.InterNetwork ? 4 : 6;
        var bits = family == 4 ? 32 : 128;

        var prefix = bits;
        if (slash >= 0)
        {
            if (!int.TryParse(trimmed[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)) return null;
            if (mapped) prefix -= 96;
            if (prefix < 0 || prefix > bits) return null;
        }

        IPNetwork network;
        try
        {
            network = new IPNetwork(address, prefix);
        }
        catch (ArgumentException)
        {
            // Host bits set below the prefix: 10.0.0.5/24.
            return null;
        }

        return new Network(family, network.BaseAddress.ToString(), prefix);
    }

    /// <summary>Whether a network is or overlaps the loopback range, which nothing may ever block.</summary>
    public static bool IsLoopback(Network network)
    {
        ArgumentNullException.ThrowIfNull(network);
        var address = IPAddress.Parse(network.Address);
        return network.Family == 4
            ? (network.PrefixLength <= 8 ? new IPNetwork(address, network.PrefixLength).Contains(IPAddress.Loopback) : IPAddress.IsLoopback(address))
            : network.PrefixLength == 0 || IPAddress.IsLoopback(address);
    }

    private static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
