using PavlovBot.Core.Network;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Rcon;

namespace PavlovBot.Host.Network;

/// <summary>
/// The online players and their addresses, from the RCON rosters and the IP history.
/// </summary>
/// <remarks>
/// CONFIRMED ADDRESSES ONLY. The IP history also holds guesses, made when a join line could not
/// be tied to one player for certain; counting a guess as a player would hide an attacker's
/// packets inside the "real players" figure.
/// </remarks>
public sealed class PlayerTraffic(RconRegistry rcon, IpTrackingService ips) : IPlayerTraffic
{
    /// <remarks>
    /// EVERY server's roster, stale ones included, so a server RCON lost touch with keeps its
    /// players rather than reporting them all as disconnected; <see cref="RosterFresh"/> says
    /// when that is happening.
    /// </remarks>
    public IReadOnlyCollection<string> OnlineNames() => rcon.AllOnlinePlayers();

    public bool RosterFresh => ((IOnlineRoster)rcon).IsTrustworthy;

    /// <summary>How recently a player must have been seen to stay on the shield's allow list.</summary>
    private static readonly TimeSpan KnownFor = TimeSpan.FromDays(30);

    /// <remarks>
    /// EVERY RECENT PLAYER, not just who is on now, so somebody reconnecting after the attack
    /// knocked them off is let back in rather than counted against the cap with the flood.
    /// </remarks>
    public IReadOnlySet<uint> KnownAddresses()
    {
        var since = DateTimeOffset.UtcNow - KnownFor;
        var addresses = new HashSet<uint>(OnlineAddresses());
        foreach (var account in ips.Accounts().Values)
        {
            if (account.LastSeen is { } seen && seen < since) continue;
            foreach (var ip in account.ConfirmedIps)
                if (CapturedPacket.Number(ip) is { } n) addresses.Add(n);
        }
        return addresses;
    }

    public IReadOnlySet<uint> OnlineAddresses()
    {
        var addresses = new HashSet<uint>();
        foreach (var name in rcon.AllOnlinePlayers())
        {
            if (ips.AccountByName(name) is not { } account) continue;
            foreach (var ip in account.ConfirmedIps)
                if (CapturedPacket.Number(ip) is { } n) addresses.Add(n);
        }
        return addresses;
    }
}
