using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The filesystem half of the cross-install ModSave sync, driven over real temp installs.
/// </summary>
/// <remarks>
/// The planner and the guards are unit-tested purely; this exists to prove the parts a pure
/// test cannot reach - that the newest bytes actually land in the other install, that the
/// destination is stamped so the sweep CONVERGES rather than ping-pongs, that nothing is ever
/// deleted, and that the online and money guards hold when wired to real files.
/// </remarks>
public sealed class ModSaveSyncTests : IDisposable
{
    private sealed record FakeRoster(bool IsTrustworthy, IReadOnlyList<string> Online) : IOnlineRoster;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pavlovbot-modsync-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Make an install directory with its ModSave tree, and return its root.</summary>
    private string Install(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(PavlovInstalls.ModSavePath(root));
        return root;
    }

    private static string LedgerPath(string install, string player) =>
        Path.Combine(PavlovInstalls.ModSavePath(install), $"{player}.txt");

    private static void WriteLedger(string install, string player, string content, DateTime? modifiedUtc = null)
    {
        var path = LedgerPath(install, player);
        File.WriteAllText(path, content);
        if (modifiedUtc is { } when) File.SetLastWriteTimeUtc(path, when);
    }

    private ModSaveSync Sync(IReadOnlyList<string> installs, IOnlineRoster? roster = null, bool enabled = true) =>
        new(installs, roster ?? new FakeRoster(true, []), enabled, NullLogger<ModSaveSync>.Instance);

    [Fact]
    public async Task TheNewerBalanceLandsInTheOtherInstall()
    {
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        var now = DateTime.UtcNow;
        WriteLedger(a, "Bob", "1250", now);
        WriteLedger(b, "Bob", "300", now.AddMinutes(-10));

        var copied = await Sync([a, b]).SweepAsync();

        Assert.Equal(1, copied);
        Assert.Equal("1250", File.ReadAllText(LedgerPath(b, "Bob")));
    }

    [Fact]
    public async Task ASecondSweepConvergesAndCopiesNothing()
    {
        // The mtime is carried across, so once propagated the two compare equal.
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        WriteLedger(a, "Bob", "1250", DateTime.UtcNow);
        WriteLedger(b, "Bob", "300", DateTime.UtcNow.AddMinutes(-10));

        var sync = Sync([a, b]);
        Assert.Equal(1, await sync.SweepAsync());
        Assert.Equal(0, await sync.SweepAsync());
    }

    [Fact]
    public async Task AFileOnlyOneInstallHasIsCopiedIn_AndNeverDeletedFromTheSource()
    {
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        WriteLedger(a, "Solo", "42", DateTime.UtcNow);

        await Sync([a, b]).SweepAsync();

        Assert.True(File.Exists(LedgerPath(a, "Solo")));   // still there
        Assert.Equal("42", File.ReadAllText(LedgerPath(b, "Solo")));   // and now here
    }

    [Fact]
    public async Task AnOnlinePlayersLedgerIsNotTouchedByTheSweep()
    {
        // Their balance is still moving in server memory, so mtime cannot be trusted.
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        WriteLedger(a, "Pkd", "999", DateTime.UtcNow);
        WriteLedger(b, "Pkd", "500", DateTime.UtcNow.AddMinutes(-10));

        var copied = await Sync([a, b], roster: new FakeRoster(true, ["Pkd"])).SweepAsync();

        Assert.Equal(0, copied);
        Assert.Equal("500", File.ReadAllText(LedgerPath(b, "Pkd")));   // untouched
    }

    [Fact]
    public async Task AnEmptyOrZeroFileNeverWipesAPositiveBalance()
    {
        // A server that just made a fresh 0-cap file must not propagate it over real caps.
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        WriteLedger(a, "Rich", "0", DateTime.UtcNow);                  // fresh zero, newest
        WriteLedger(b, "Rich", "5000", DateTime.UtcNow.AddMinutes(-5));

        var copied = await Sync([a, b]).SweepAsync();

        Assert.Equal(0, copied);
        Assert.Equal("5000", File.ReadAllText(LedgerPath(b, "Rich")));   // caps kept
    }

    [Fact]
    public async Task MenuAccessFilesAreNeverMirrored()
    {
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        Directory.CreateDirectory(Path.Combine(PavlovInstalls.ModSavePath(a), "RconPlus"));
        Directory.CreateDirectory(Path.Combine(PavlovInstalls.ModSavePath(b), "RconPlus"));
        var relA = Path.Combine(PavlovInstalls.ModSavePath(a), "RconPlus", "MenuAccess.txt");
        var relB = Path.Combine(PavlovInstalls.ModSavePath(b), "RconPlus", "MenuAccess.txt");
        File.WriteAllText(relA, "granted"); File.SetLastWriteTimeUtc(relA, DateTime.UtcNow);
        File.WriteAllText(relB, "revoked"); File.SetLastWriteTimeUtc(relB, DateTime.UtcNow.AddMinutes(-10));

        await Sync([a, b]).SweepAsync();

        Assert.Equal("revoked", File.ReadAllText(relB));   // its own live state kept
    }

    [Fact]
    public async Task ANonLedgerFileIsMirroredNewestWins()
    {
        // Faction roles, gamemode saves - not ledgers, no money/online guard, just newest-wins.
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        var relA = Path.Combine(PavlovInstalls.ModSavePath(a), "roles.txt");
        var relB = Path.Combine(PavlovInstalls.ModSavePath(b), "roles.txt");
        File.WriteAllText(relA, "Captain=Bob"); File.SetLastWriteTimeUtc(relA, DateTime.UtcNow);
        File.WriteAllText(relB, "old"); File.SetLastWriteTimeUtc(relB, DateTime.UtcNow.AddHours(-1));

        await Sync([a, b]).SweepAsync();

        Assert.Equal("Captain=Bob", File.ReadAllText(relB));
    }

    [Fact]
    public async Task ASingleInstallDoesNothing()
    {
        var a = Install("pavlovserver");
        WriteLedger(a, "Bob", "1250", DateTime.UtcNow);

        Assert.Equal(0, await Sync([a]).SweepAsync());
        Assert.False(Sync([a]).CanSync);
    }

    [Fact]
    public async Task DisabledSyncsNothingEvenWithManyInstalls()
    {
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        WriteLedger(a, "Bob", "1250", DateTime.UtcNow);
        WriteLedger(b, "Bob", "1", DateTime.UtcNow.AddMinutes(-10));

        Assert.Equal(0, await Sync([a, b], enabled: false).SweepAsync());
        Assert.Equal("1", File.ReadAllText(LedgerPath(b, "Bob")));
    }

    [Fact]
    public async Task TheBotNeverCreatesAModSaveTreeInAMisdiscoveredInstall()
    {
        // If a "second install" has no ModSave dir, a copy into it must fail rather than build
        // a tree beside the real one that the game never reads.
        var a = Install("pavlovserver");
        var b = Path.Combine(_root, "pavlovserver1");
        Directory.CreateDirectory(b);   // an install root, but NO Pavlov/Saved/Config/ModSave
        WriteLedger(a, "Bob", "1250", DateTime.UtcNow);

        var copied = await Sync([a, b]).SweepAsync();

        Assert.Equal(0, copied);
        Assert.False(Directory.Exists(PavlovInstalls.ModSavePath(b)));
    }

    [Fact]
    public async Task TheJoinFastPathPushesOneLedgerEvenForAnOnlinePlayer()
    {
        // The sweep skips online players; the join path is exactly how they get synced.
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        WriteLedger(a, "Hopper", "800", DateTime.UtcNow);
        WriteLedger(b, "Hopper", "100", DateTime.UtcNow.AddMinutes(-10));

        await Sync([a, b], roster: new FakeRoster(true, ["Hopper"])).SyncPlayerLedgerAsync("Hopper");

        Assert.Equal("800", File.ReadAllText(LedgerPath(b, "Hopper")));
    }

    [Fact]
    public async Task TheJoinFastPathStillRefusesToWipeABalance()
    {
        var a = Install("pavlovserver");
        var b = Install("pavlovserver1");
        WriteLedger(a, "Hopper", "0", DateTime.UtcNow);                   // fresh zero, newest
        WriteLedger(b, "Hopper", "2500", DateTime.UtcNow.AddMinutes(-5));

        await Sync([a, b]).SyncPlayerLedgerAsync("Hopper");

        Assert.Equal("2500", File.ReadAllText(LedgerPath(b, "Hopper")));
    }

    // ---- faction rosters on a freshly provisioned server ----

    private static string Rosters(string install) => Path.Combine(PavlovInstalls.ModSavePath(install), "FactionRoles");

    private static void WriteRoster(string install, string file, string content, DateTime modifiedUtc)
    {
        Directory.CreateDirectory(Rosters(install));
        var path = Path.Combine(Rosters(install), file);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    private ModSaveSync RosterSync(IReadOnlyList<string> installs) =>
        new(installs, new FakeRoster(true, []), enabled: true, NullLogger<ModSaveSync>.Instance,
            rosterDirectory: Rosters(installs[0]));

    /// <summary>A real install with nothing under Config: what /provisionserver leaves.</summary>
    private string BareInstall(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(root, "Pavlov", "Saved", "Config"));
        return root;
    }

    [Fact]
    public async Task AFreshServerGetsItsFactionRolesFolderAndEveryRoster()
    {
        /* THE BUG. Servers 2 and 3 had no ModSave/FactionRoles, every copy into them was
           skipped for want of the folder, and they never had a whitelist at all. */
        var one = Install("pavlovserver");
        var two = BareInstall("pavlovserver2");
        var three = BareInstall("pavlovserver3");
        WriteRoster(one, "gambinospawn.txt", "Alice\nBob\n", DateTime.UtcNow);
        WriteRoster(one, "policecadet.txt", "Carol\n", DateTime.UtcNow);

        await RosterSync([one, two, three]).SweepAsync();

        foreach (var server in new[] { two, three })
        {
            Assert.Equal("Alice\nBob\n", File.ReadAllText(Path.Combine(Rosters(server), "gambinospawn.txt")));
            Assert.Equal("Carol\n", File.ReadAllText(Path.Combine(Rosters(server), "policecadet.txt")));
        }
    }

    [Fact]
    public async Task AnEmptyRosterOnANewServerNeverWipesTheRealOne()
    {
        // Newer, but empty, and not from the folder the bot edits: it must not win.
        var one = Install("pavlovserver");
        var two = Install("pavlovserver2");
        WriteRoster(one, "gambinospawn.txt", "Alice\n", DateTime.UtcNow.AddHours(-1));
        WriteRoster(two, "gambinospawn.txt", "", DateTime.UtcNow);

        await RosterSync([one, two]).SweepAsync();

        Assert.Equal("Alice\n", File.ReadAllText(Path.Combine(Rosters(one), "gambinospawn.txt")));
    }

    [Fact]
    public async Task AWipeFromTheBotsOwnRosterFolderStillGoesOut()
    {
        // /whitelist wipe empties the file the bot edits; that is deliberate and must reach every server.
        var one = Install("pavlovserver");
        var two = Install("pavlovserver2");
        WriteRoster(two, "gambinospawn.txt", "Alice\n", DateTime.UtcNow.AddHours(-1));
        WriteRoster(one, "gambinospawn.txt", "", DateTime.UtcNow);

        await RosterSync([one, two]).SweepAsync();

        Assert.Equal("", File.ReadAllText(Path.Combine(Rosters(two), "gambinospawn.txt")));
    }
}
