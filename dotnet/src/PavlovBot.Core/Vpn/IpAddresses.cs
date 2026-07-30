using System.Net;
using System.Net.Sockets;

namespace PavlovBot.Core.Vpn;

/// <summary>Address classification that has to happen before any lookup is spent.</summary>
public static class IpAddresses
{
    /// <summary>
    /// Whether an address is private, loopback, link-local or otherwise unroutable.
    /// </summary>
    /// <remarks>
    /// Players on the same LAN as the server connect from a private address. No provider
    /// can say anything about one, and asking costs a lookup per detector per join - on
    /// proxycheck's keyless 100/day tier that is a whole day's quota from one LAN player
    /// reconnecting twenty times.
    ///
    /// Short-circuited AND CACHED, so the answer is produced once rather than re-derived
    /// on every reconnect.
    /// </remarks>
    public static bool IsUnroutable(string? ip)
    {
        var text = (ip ?? "").Trim();
        if (text.Length == 0) return true;

        if (!IPAddress.TryParse(text, out var address))
        {
            /* Not parseable as an address at all. Treated as unroutable so nothing is
               spent looking it up, but it is a different situation from a private
               address - a malformed value here means the log parser fed us garbage. */
            return true;
        }

        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            // fc00::/7 - unique local addresses. .NET has no property for these.
            var v6 = address.GetAddressBytes();
            if ((v6[0] & 0xFE) == 0xFC) return true;

            // An IPv4-mapped address is really an IPv4 one; classify it as such.
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            else return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        return bytes[0] switch
        {
            0 => true,                                  // this-network
            10 => true,                                 // private
            127 => true,                                // loopback
            169 when bytes[1] == 254 => true,           // link-local
            172 when bytes[1] is >= 16 and <= 31 => true,// private
            192 when bytes[1] == 168 => true,           // private
            >= 224 => true,                             // multicast and reserved
            _ => false,
        };
    }

    /// <summary>The record stored for an address nobody can check.</summary>
    public static VpnRecord LocalRecord(string ip, DateTimeOffset checkedAt) => new()
    {
        Ip = ip,
        Decision = new VpnDecision(false, null, false, "private/unroutable address - detection skipped"),
        Local = true,
        CheckedAt = checkedAt,
        Country = "Local network",
        Isp = "private address",
    };
}
