namespace PavlovBot.Core.Provisioning;

/// <summary>
/// The default ports for server N, stepped so a second server is not a copy of the first.
/// </summary>
/// <remarks>
/// EVERY DEFAULT IS A FUNCTION OF THE SLOT, and that is the whole point. They used to be three
/// fixed constants, so provisioning a second server proposed exactly the first server's ports:
/// the RCON clash was caught by validation and the command simply refused, and if the operator
/// worked around that by naming an RCON port by hand, the game and query ports still collided -
/// silently, because the bot only knows other servers' RCON ports and cannot see the rest. Two
/// servers sharing a game port do not fail cleanly; the second one loses the bind and is just
/// absent from the browser.
///
/// The three strides are not the same, and each matches how the game itself is laid out:
///
///   GAME PORT steps by ONE - 7777, 7778, 7779 - which is the convention the Pavlov wiki uses
///   for several instances on one box.
///
///   QUERY PORT is the game port PLUS 400, not an independent counter. That offset is Pavlov's
///   own (7777 pairs with 8177), so deriving it keeps the pair correct even when the operator
///   names a game port by hand rather than taking the default.
///
///   RCON PORT steps by ONE HUNDRED - 9100, 9200, 9300 - deliberately coarse. RCON is the port
///   an operator types into a client and reads in a config file, so the round numbers are worth
///   more than the density, and the wide gap leaves room for a box to grow game ports without
///   ever walking into the RCON range.
/// </remarks>
public static class ServerPortDefaults
{
    /// <summary>Server 1's game port. The wiki's default, and the base every other steps from.</summary>
    public const int BaseGamePort = 7777;

    /// <summary>Pavlov's own game-to-query offset: 7777 pairs with 8177.</summary>
    public const int QueryPortOffset = 400;

    /// <summary>Server 1's RCON port.</summary>
    public const int BaseRconPort = 9100;

    /// <summary>How far apart consecutive servers' RCON ports sit.</summary>
    public const int RconPortStride = 100;

    /// <summary>The default game port for a 1-based slot: 7777, 7778, 7779…</summary>
    public static int GamePort(int slot) => BaseGamePort + (slot - 1);

    /// <summary>
    /// The default query port for a game port: always that port plus Pavlov's offset.
    /// </summary>
    /// <remarks>
    /// Takes the GAME PORT rather than the slot so an operator who names a game port by hand
    /// still gets the matching query port, instead of one derived from a slot they did not
    /// choose the game port from.
    /// </remarks>
    public static int QueryPortFor(int gamePort) => gamePort + QueryPortOffset;

    /// <summary>The default RCON port for a 1-based slot: 9100, 9200, 9300…</summary>
    public static int RconPort(int slot) => BaseRconPort + ((slot - 1) * RconPortStride);
}
