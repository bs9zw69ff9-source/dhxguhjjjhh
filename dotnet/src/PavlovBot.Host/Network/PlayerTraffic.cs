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
    public int OnlineCount => rcon.AllOnlinePlayers().Count;

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
