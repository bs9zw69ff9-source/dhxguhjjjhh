using PavlovBot.Core.Sync;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The newest-wins decision that keeps every install's ModSave tree identical.
/// </summary>
/// <remarks>
/// The money and online guards read file content and are tested next door in
/// <see cref="LedgerFileTests"/>; this covers the pure mtime arithmetic - which copy is the
/// source, which destinations are stale, and the cases that must produce NO copy at all,
/// because a sync that mirrors the wrong direction moves a balance backwards.
/// </remarks>
public class ModSaveSyncPlannerTests
{
    private static ModSaveSnapshot Install(string root, params (string Rel, long Mtime)[] files) =>
        new(root, files.ToDictionary(f => f.Rel, f => f.Mtime, StringComparer.Ordinal));

    [Fact]
    public void TheNewestCopyIsPropagatedToTheStaleInstall()
    {
        var a = Install("/a", ("econ/Bob.txt", 5_000));
        var b = Install("/b", ("econ/Bob.txt", 1_000));

        var copies = ModSaveSyncPlanner.Candidates([a, b]);

        var copy = Assert.Single(copies);
        Assert.Equal("econ/Bob.txt", copy.RelPath);
        Assert.Equal("/a", copy.FromRoot);
        Assert.Equal("/b", copy.ToRoot);
        Assert.Equal(5_000, copy.ModifiedUnixMs);
    }

    [Fact]
    public void AFileMissingFromOneInstallIsCopiedIntoIt()
    {
        var a = Install("/a", ("econ/Bob.txt", 5_000));
        var b = Install("/b");   // nothing

        var copies = ModSaveSyncPlanner.Candidates([a, b]);

        Assert.Equal("/b", Assert.Single(copies).ToRoot);
    }

    [Fact]
    public void CopiesReachEveryOtherInstall_NotJustOne()
    {
        var a = Install("/a", ("f.txt", 9_000));
        var b = Install("/b", ("f.txt", 1_000));
        var c = Install("/c");

        var copies = ModSaveSyncPlanner.Candidates([a, b, c]);

        Assert.Equal(2, copies.Count);
        Assert.All(copies, x => Assert.Equal("/a", x.FromRoot));
        Assert.Equal(["/b", "/c"], copies.Select(x => x.ToRoot).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ADestinationWithinTheToleranceWindowIsLeftAlone()
    {
        // A copy re-stamps the destination from the source, so two copies a tick apart must
        // read as equal - otherwise they bounce between installs on every sweep forever.
        var a = Install("/a", ("f.txt", 10_000));
        var b = Install("/b", ("f.txt", 10_000 - (ModSaveSyncPlanner.DefaultToleranceMs - 1)));

        Assert.Empty(ModSaveSyncPlanner.Candidates([a, b]));
    }

    [Fact]
    public void ADestinationOlderThanTheToleranceIsCopied()
    {
        var a = Install("/a", ("f.txt", 10_000));
        var b = Install("/b", ("f.txt", 10_000 - (ModSaveSyncPlanner.DefaultToleranceMs + 1)));

        Assert.Equal("/b", Assert.Single(ModSaveSyncPlanner.Candidates([a, b])).ToRoot);
    }

    [Fact]
    public void SkippedPathsAreNeverMirrored()
    {
        var a = Install("/a", ("rconplus/menuaccess.txt", 9_000));
        var b = Install("/b", ("rconplus/menuaccess.txt", 1_000));

        Assert.Empty(ModSaveSyncPlanner.Candidates([a, b], skip: rel => rel.Contains("menuaccess")));
    }

    [Fact]
    public void OneInstallIsNothingToSync()
    {
        var a = Install("/a", ("f.txt", 9_000));

        Assert.Empty(ModSaveSyncPlanner.Candidates([a]));
    }

    [Fact]
    public void IdenticalTreesProduceNoCopies()
    {
        var a = Install("/a", ("f.txt", 5_000), ("g.txt", 6_000));
        var b = Install("/b", ("f.txt", 5_000), ("g.txt", 6_000));

        Assert.Empty(ModSaveSyncPlanner.Candidates([a, b]));
    }

    [Fact]
    public void TheOutputIsDeterministicallyOrdered()
    {
        var a = Install("/a", ("b.txt", 9_000), ("a.txt", 9_000));
        var b = Install("/b");

        var copies = ModSaveSyncPlanner.Candidates([a, b]);

        Assert.Equal(["a.txt", "b.txt"], copies.Select(c => c.RelPath));
    }
}
