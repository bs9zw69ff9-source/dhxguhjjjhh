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
