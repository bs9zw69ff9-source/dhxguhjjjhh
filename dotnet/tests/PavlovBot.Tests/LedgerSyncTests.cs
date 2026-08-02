using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Economy;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Keeping a player's caps ledger identical across every install.
/// </summary>
/// <remarks>
/// NEVER PORTED. The Node bot copies a player's ledger onto every install when they join
/// and again when they leave, so their money follows them between servers. The C# bot had
/// no equivalent, so each install kept its own copy - whichever server they last played on
/// held the real balance and the rest were stale, which from outside looks like money
/// vanishing on one server and files never appearing on another.
/// </remarks>
public class LedgerSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pavlovbot-sync-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _installs = [];

    private const string Rel = "Pavlov/Saved/Config/ModSave";

    public LedgerSyncTests()
    {
        foreach (var name in new[] { "pavlovserver", "pavlovserver1", "pavlovserver2" })
        {
            var install = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(install, Rel));
            _installs.Add(install);
        }
    }

    private string Ledger(int install, string player) =>
        Path.Combine(_installs[install], Rel, $"{player}.txt");

    private LedgerSync Build() =>
        new(_installs, Path.Combine(_installs[0], Rel), NullLogger<LedgerSync>.Instance);

    private void Write(int install, string player, string balance, DateTime? at = null)
    {
        var path = Ledger(install, player);
        File.WriteAllText(path, balance);
        File.SetLastWriteTimeUtc(path, at ?? DateTime.UtcNow);
    }

    // ---- the wipe guard ----

    [Fact]
    public void AZeroNeverOverwritesARealBalance()
    {
        /* THE RULE THAT PROTECTS SOMEBODY'S MONEY. An install that has never seen a player
           writes 0 for them, and without this the first sync after they hop servers pushes
           that zero over the balance they actually have. */
        Assert.True(LedgerSync.WouldWipe("0", "5000"));
        Assert.True(LedgerSync.WouldWipe("", "5000"));
        Assert.True(LedgerSync.WouldWipe("   ", "1"));
    }

    [Fact]
    public void ARealBalanceMayOverwriteAZero()
    {
        // Deliberately asymmetric: real beats zero, zero never beats real.
        Assert.False(LedgerSync.WouldWipe("5000", "0"));
        Assert.False(LedgerSync.WouldWipe("5000", ""));
        Assert.False(LedgerSync.WouldWipe("0", "0"));
        Assert.False(LedgerSync.WouldWipe("0", null));
    }

    [Fact]
    public void ANonNumericDestinationIsNotTreatedAsABalance()
    {
        // Only a positive integer is a balance worth protecting; anything else is not a
        // ledger and guessing at it would block a legitimate repair.
        Assert.False(LedgerSync.WouldWipe("0", "not a number"));
        Assert.False(LedgerSync.WouldWipe("0", "000"));
    }

    [Fact]
    public void AZeroIsNotCopiedOverARealBalanceEndToEnd()
    {
        var now = DateTime.UtcNow;
        Write(0, "Alice", "0", now);                       // newest, and empty
        Write(1, "Alice", "5000", now.AddMinutes(-10));    // older, and real

        Build().Sync("Alice");

        Assert.Equal("5000", File.ReadAllText(Ledger(1, "Alice")));
    }

    // ---- the sync itself ----

    [Fact]
    public void TheNewestLedgerReachesEveryInstall()
    {
        var now = DateTime.UtcNow;
        Write(0, "Alice", "1200", now);
        Write(1, "Alice", "300", now.AddMinutes(-30));

        var synced = Build().Sync("Alice");

        Assert.Equal(2, synced);                                    // install 1 updated, install 2 created
        Assert.Equal("1200", File.ReadAllText(Ledger(1, "Alice")));
        Assert.Equal("1200", File.ReadAllText(Ledger(2, "Alice")));
    }

    [Fact]
    public void TheModifiedTimeIsPreservedSoTheNextSweepAgrees()
    {
        /* Without this every copy looks freshly written, and the "newest" on the next sweep
           is whichever one was synced last rather than the one the game actually wrote. */
        var written = DateTime.UtcNow.AddMinutes(-5);
        Write(0, "Alice", "999", written);

        Build().Sync("Alice");

        Assert.Equal(written, File.GetLastWriteTimeUtc(Ledger(1, "Alice")), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AlreadyCurrentInstallsAreNotRewritten()
    {
        // Rewriting identical files every join would churn the game's own directory for
        // nothing, and would keep resetting the timestamps that decide the winner.
        var now = DateTime.UtcNow;
        Write(0, "Alice", "500", now);
        Write(1, "Alice", "500", now);
        Write(2, "Alice", "500", now);

        Assert.Equal(0, Build().Sync("Alice"));
    }

    [Fact]
    public void APlayerWithNoLedgerAnywhereSyncsNothing()
    {
        Assert.Equal(0, Build().Sync("NeverPlayed"));
    }

    [Fact]
    public void NamesWithSpacesAreKept()
    {
        // The file must match what the GAME writes - "Butter Life.txt", not "ButterLife.txt".
        Write(0, "Butter Life", "77", DateTime.UtcNow);

        Build().Sync("Butter Life");

        Assert.True(File.Exists(Ledger(1, "Butter Life")));
    }

    [Fact]
    public void SyncIsOffWithASingleInstall()
    {
        // Nothing to mirror to, and the work would be pure churn.
        var one = new LedgerSync([_installs[0]], Path.Combine(_installs[0], Rel), NullLogger<LedgerSync>.Instance);

        Assert.False(one.Enabled);
        Assert.Equal(0, one.Sync("Alice"));
    }

    [Fact]
    public void SyncCanBeTurnedOff()
    {
        var off = new LedgerSync(_installs, Path.Combine(_installs[0], Rel),
            NullLogger<LedgerSync>.Instance, enabled: false);

        Write(0, "Alice", "1200", DateTime.UtcNow);

        Assert.False(off.Enabled);
        Assert.Equal(0, off.Sync("Alice"));
        Assert.False(File.Exists(Ledger(1, "Alice")));
    }

    // ---- mirroring ----

    [Fact]
    public void APathIsMirroredIntoEveryInstall()
    {
        var mirrored = LedgerSync.Mirror(_installs, Ledger(0, "Alice"));

        Assert.Equal(3, mirrored.Count);
        Assert.Contains(Ledger(1, "Alice"), mirrored, StringComparer.Ordinal);
        Assert.Contains(Ledger(2, "Alice"), mirrored, StringComparer.Ordinal);
    }

    [Fact]
    public void APathOutsideEveryInstallIsNotScattered()
    {
        /* A misconfigured ledger directory must sync NOTHING rather than copy files into
           three places that have no relationship to it. */
        var stray = Path.Combine(_root, "somewhere-else", "Alice.txt");

        Assert.Equal([stray], LedgerSync.Mirror(_installs, stray));
    }

    [Fact]
    public void AnInstallPrefixDoesNotMatchASiblingByAccident()
    {
        /* "pavlovserver" is a string prefix of "pavlovserver1". Matching on the bare prefix
           would treat a file in install 1 as living inside install 0 and mirror it wrongly. */
        var mirrored = LedgerSync.Mirror(_installs, Ledger(1, "Alice"));

        Assert.Equal(3, mirrored.Count);
        Assert.Contains(Ledger(0, "Alice"), mirrored, StringComparer.Ordinal);
        Assert.Contains(Ledger(2, "Alice"), mirrored, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
