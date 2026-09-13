using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A member who keeps every rank at or below their own.
/// </summary>
/// <remarks>
/// Each rank file is what grants that rank's gear in game, so a Sergeant who should also have
/// the corporal and trooper loadouts has to be listed in all of them. The writer removed a
/// member from every file but the target, which is right for the normal case and is exactly
/// what this has to stop doing.
///
/// READS ALREADY COPED. FindAsync and RosterAsync report a member listed several times at
/// their TOP rank, and did so before this existed - so the only thing that changes is what a
/// promotion WRITES.
/// </remarks>
public class HoldAllRanksTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pavlov-holdranks-" + Guid.NewGuid().ToString("N"));

    private static readonly FactionDefinition Nypd = FactionRegistry.Get("NYPD")!;

    public HoldAllRanksTests() => Directory.CreateDirectory(_directory);

    private RosterService Service(bool holding) => new(
        _directory, NullLogger<RosterService>.Instance, backupDirectory: _directory, factions: FactionRegistry.Police,
        holdsAllRanks: holding ? _ => true : null);

    private IReadOnlyList<string> Contents(string file)
    {
        var path = Path.Combine(_directory, file);
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList()
            : [];
    }

    private string FileFor(string rank) => Nypd.RankFiles[rank];

    [Fact]
    public async Task APromotionKeepsEveryRankBelowTheNewOne()
    {
        // THE FEATURE. Cadet -> Patrolman -> Corporal, holding all three by the end.
        var rosters = Service(holding: true);
        await rosters.JoinAsync(Nypd, "Alice");

        await rosters.ChangeRankAsync(Nypd, "Alice", +1);
        await rosters.ChangeRankAsync(Nypd, "Alice", +1);

        Assert.Contains("Alice", Contents(FileFor("Cadet")));
        Assert.Contains("Alice", Contents(FileFor("Patrolman")));
        Assert.Contains("Alice", Contents(FileFor("Corporal")));
        Assert.DoesNotContain("Alice", Contents(FileFor("Sergeant")));
    }

    [Fact]
    public async Task WithoutTheFlagAPromotionStillMovesThemBetweenFiles()
    {
        /* THE CONTROL, and the behaviour every existing member keeps: exactly one rank file
           mentions them, which is what the writer has always guaranteed. */
        var rosters = Service(holding: false);
        await rosters.JoinAsync(Nypd, "Bob");

        await rosters.ChangeRankAsync(Nypd, "Bob", +1);

        Assert.DoesNotContain("Bob", Contents(FileFor("Cadet")));
        Assert.Contains("Bob", Contents(FileFor("Patrolman")));
        Assert.Equal(1, Nypd.RankFiles.Values.Sum(f => Contents(f).Count(n => n == "Bob")));
    }

    [Fact]
    public async Task ADemotionStillTakesBackWhatIsNowAboveThem()
    {
        /* The set only ever grows downward. A demotion that left the higher rank in place
           would make demoting somebody do nothing at all in game. */
        var rosters = Service(holding: true);
        await rosters.JoinAsync(Nypd, "Cara");
        await rosters.ChangeRankAsync(Nypd, "Cara", +1);
        await rosters.ChangeRankAsync(Nypd, "Cara", +1);

        await rosters.ChangeRankAsync(Nypd, "Cara", -1);

        Assert.DoesNotContain("Cara", Contents(FileFor("Corporal")));
        Assert.Contains("Cara", Contents(FileFor("Patrolman")));
        Assert.Contains("Cara", Contents(FileFor("Cadet")));
    }

    [Fact]
    public async Task TheirRankIsStillReportedAsTheHighestOneTheyHold()
    {
        /* Everything downstream - /promotion, the roster listing, payroll - asks FindAsync
           where somebody sits. Reporting the lowest of several would make the next promotion
           move them from Cadet to Patrolman again, forever. */
        var rosters = Service(holding: true);
        await rosters.JoinAsync(Nypd, "Dana");
        await rosters.ChangeRankAsync(Nypd, "Dana", +1);
        await rosters.ChangeRankAsync(Nypd, "Dana", +1);

        var membership = await rosters.FindAsync("Dana");

        Assert.Equal("Corporal", membership!.Rank);
    }

    [Fact]
    public async Task LeavingClearsEveryRankTheyHeld()
    {
        // Removal has to take all of them, or a held rank keeps their access after removal.
        var rosters = Service(holding: true);
        await rosters.JoinAsync(Nypd, "Eve");
        await rosters.ChangeRankAsync(Nypd, "Eve", +1);

        await rosters.LeaveAsync(Nypd, "Eve");

        Assert.All(Nypd.RankFiles.Values, f => Assert.DoesNotContain("Eve", Contents(f)));
        Assert.DoesNotContain("Eve", Contents(Nypd.SpawnFile));
    }

    [Fact]
    public async Task TheyAreStillInTheSpawnFile()
    {
        // The file that decides whether they can play as the faction at all.
        var rosters = Service(holding: true);
        await rosters.JoinAsync(Nypd, "Frank");
        await rosters.ChangeRankAsync(Nypd, "Frank", +1);

        Assert.Contains("Frank", Contents(Nypd.SpawnFile));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is not a failure */ }
    }
}
