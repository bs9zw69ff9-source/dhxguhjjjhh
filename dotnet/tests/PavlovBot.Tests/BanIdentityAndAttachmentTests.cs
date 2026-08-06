using PavlovBot.Host.Moderation;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Bans filed under a readable name, and attached accounts caught on sight.
/// </summary>
/// <remarks>
/// Both reported from production: "bans are doing eos id not username", and blacklisted
/// addresses not reaching the accounts attached to them.
/// </remarks>
public class BanIdentityAndAttachmentTests
{
    private const string Eos = "0002a1b3c4d5e6f708192a3b4c5d6e7f";

    [Fact]
    public void AnInGameBanIsFiledUnderTheUsernameWhenTheIdIsKnown()
    {
        /* Pavlov writes the UniqueID as the block header for bans made in-game, so importing
           it verbatim filed those under a 32-character id while the bot's own bans used
           names - the same player appearing as two unrelated entries. */
        Assert.Equal("Holosight1", ModsaveBanlist.ResolveName(Eos, id => id == Eos ? "Holosight1" : null));
    }

    [Fact]
    public void AnUnknownIdKeepsTheIdRatherThanBeingDropped()
    {
        // A ban you cannot name is still a ban. Showing it awkwardly beats losing it.
        Assert.Equal(Eos, ModsaveBanlist.ResolveName(Eos, _ => null));
        Assert.Equal(Eos, ModsaveBanlist.ResolveName(Eos, _ => "   "));
    }

    [Fact]
    public void AnAlreadyNamedEntryIsLeftAlone()
    {
        // Bans the bot wrote itself already carry a username, and a resolver that returned
        // something else for one would rename a ban out from under the moderator who made it.
        Assert.Equal("Alice", ModsaveBanlist.ResolveName("Alice", _ => null));
    }

    [Fact]
    public void WithNoResolverConfiguredNothingChanges()
        => Assert.Equal(Eos, ModsaveBanlist.ResolveName(Eos, resolve: null));
}
