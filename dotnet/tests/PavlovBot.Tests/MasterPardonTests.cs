using PavlovBot.Core.Data;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A master owner's unban is a pardon: every automated path treats the player as protected until
/// a master owner bans them again.
/// </summary>
public class MasterPardonTests
{
    private static MasterNames Masters() =>
        new([], new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec()));

    [Fact]
    public async Task APardonedPlayerIsProtectedFromEveryAutomatedPath()
    {
        var masters = Masters();

        Assert.True(await masters.PardonAsync("Forgiven"));

        // IsProtected is what the sweep, the reconcile and both responders consult.
        Assert.True(masters.IsPardoned("forgiven"));
        Assert.True(masters.IsProtected("FORGIVEN"));
        Assert.False(masters.IsProtected("SomeoneElse"));
    }

    [Fact]
    public async Task ClearingThePardonEndsTheProtection()
    {
        var masters = Masters();
        await masters.PardonAsync("Forgiven");

        await masters.ClearPardonAsync("Forgiven");

        Assert.False(masters.IsPardoned("Forgiven"));
        Assert.False(masters.IsProtected("Forgiven"));
    }

    [Fact]
    public async Task APardonIsSeparateFromTheNeverBanList()
    {
        // Unprotecting a player through /configure must not drop a master owner's pardon.
        var masters = Masters();
        await masters.PardonAsync("Forgiven");

        await masters.UnprotectAsync("Forgiven");

        Assert.True(masters.IsProtected("Forgiven"));
        Assert.DoesNotContain("Forgiven", masters.Protected().Keys);
    }
}
