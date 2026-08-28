using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Creating the roster files the loaded factions expect, at startup.
/// </summary>
/// <remarks>
/// A faction whose file does not exist is indistinguishable in game from one whose file is
/// empty, so this is not about the game's behaviour: it is that the whole set is present and
/// visible on disk from the first start, rather than appearing one file at a time as each
/// faction gains its first member.
/// </remarks>
public class RosterFileCreationTests
{
    private static FactionSet TwoFactions() => FactionSet.Of(
    [
        new FactionDefinition
        {
            Name = "NCR",
            SpawnFile = "ncrspawn.txt",
            RankFiles = new Dictionary<string, string>
            {
                ["Recruit"] = "ncrrecruit.txt",
                ["Trooper"] = "ncrtrooper.txt",
            },
            Order = ["Recruit", "Trooper"],
            Default = "Recruit",
            Subclasses = new Dictionary<string, string> { ["Ranger"] = "ncrranger.txt" },
        },
        new FactionDefinition
        {
            Name = "Legion",
            SpawnFile = "legionspawn.txt",
            RankFiles = new Dictionary<string, string> { ["Recruit"] = "legionrecruit.txt" },
            Order = ["Recruit"],
            Default = "Recruit",
        },
    ]);

    [Fact]
    public void EveryRosterFileAFactionOwnsIsListed()
    {
        // Spawn files, rank files and sub-classes alike - all of them are files the game reads.
        Assert.Equal(
        [
            "legionrecruit.txt",
            "legionspawn.txt",
            "ncrranger.txt",
            "ncrrecruit.txt",
            "ncrspawn.txt",
            "ncrtrooper.txt",
        ], RosterService.RosterFilesOf(TwoFactions()));
    }

    [Fact]
    public void AFileServingAsBothSpawnAndRankIsListedOnce()
    {
        /* A spawn-only faction uses one file as both its membership and its single rank, which
           FactionSet allows deliberately. Listing it twice would mean trying to create it twice. */
        var set = FactionSet.Of(
        [
            new FactionDefinition
            {
                Name = "Gambino",
                SpawnFile = "gambinospawn.txt",
                RankFiles = new Dictionary<string, string> { ["Member"] = "gambinospawn.txt" },
                Order = ["Member"],
                Default = "Member",
            },
        ]);

        Assert.Equal(["gambinospawn.txt"], RosterService.RosterFilesOf(set));
    }

    [Fact]
    public void MissingFilesAreCreatedEmptyAndExistingOnesAreLeftAlone()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pavlov-rosters-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            // One roster already has members in it. Nothing here may touch that.
            var populated = Path.Combine(directory, "ncrspawn.txt");
            File.WriteAllText(populated, "SomeExistingPlayer\n");

            var service = new RosterService(directory, NullLogger<RosterService>.Instance, factions: TwoFactions());
            var report = service.EnsureRosterFiles();

            Assert.Equal(5, report.Created);   // six owned, one already there
            Assert.Equal(1, report.Present);
            Assert.Equal(6, report.Expected);
            Assert.Empty(report.Failed);
            Assert.All(RosterService.RosterFilesOf(TwoFactions()),
                f => Assert.True(File.Exists(Path.Combine(directory, f)), $"{f} should exist"));

            // The one with members is untouched, contents and all.
            Assert.Equal("SomeExistingPlayer\n", File.ReadAllText(populated));

            /* And a second start creates nothing, rather than rewriting what it just made - but
               it still says so, and says it in terms of files that ARE there. */
            var again = service.EnsureRosterFiles();
            Assert.Equal(0, again.Created);
            Assert.Equal(6, again.Present);
            Assert.Contains("6 already there", again.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NoRosterDirectoryMeansNoFilesAndNoDirectoryCreated()
    {
        /* THE RULE THAT DOES NOT BEND: the bot never creates a directory inside a game install.
           A missing one means the configured path is wrong, and building a tree the game never
           reads is worse than doing nothing. */
        var missing = Path.Combine(Path.GetTempPath(), $"pavlov-absent-{Guid.NewGuid():N}");

        var service = new RosterService(missing, NullLogger<RosterService>.Instance, factions: TwoFactions());

        var report = service.EnsureRosterFiles();

        Assert.Equal(0, report.Created);
        Assert.False(Directory.Exists(missing), "the roster directory must never be created");

        /* AND IT SAYS WHY, naming the setting and the path. This is the case that actually
           happens - a typo in FACTION_ROLES_PATH - and reporting it as a plain zero left the
           bot looking like the feature was broken rather than pointed somewhere wrong. */
        Assert.Contains("FACTION_ROLES_PATH", report.Summary, StringComparison.Ordinal);
        Assert.Contains(missing, report.Summary, StringComparison.Ordinal);
        Assert.Contains("does not exist", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRosterPathConfiguredSaysSoRatherThanReportingZero()
    {
        // Not set at all is a different answer from set-and-wrong, and both are different from
        // "there was nothing to do". The startup line has to be able to tell them apart.
        var service = new RosterService(null, NullLogger<RosterService>.Instance, factions: TwoFactions());

        var report = service.EnsureRosterFiles();

        Assert.Equal(0, report.Created);
        Assert.Equal(6, report.Expected);
        Assert.Contains("FACTION_ROLES_PATH not set", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileTheGuardRefusesIsReportedByName()
    {
        /* IGNORE_PATHS covering a roster is the configuration that produces an empty directory
           and no explanation. The file is left alone, and the reason travels back with its
           name attached so the startup log names the file rather than the count. */
        var directory = Path.Combine(Path.GetTempPath(), $"pavlov-guarded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var guard = new PavlovBot.Host.Storage.GameFileGuard([Path.Combine(directory, "ncrspawn.txt")], null);
            var service = new RosterService(directory, NullLogger<RosterService>.Instance,
                factions: TwoFactions(), guard: guard);

            var report = service.EnsureRosterFiles();

            Assert.Equal(5, report.Created);
            Assert.False(File.Exists(Path.Combine(directory, "ncrspawn.txt")));
            Assert.Single(report.Failed);
            Assert.StartsWith("ncrspawn.txt: ", report.Failed[0], StringComparison.Ordinal);
            Assert.Contains("COULD NOT BE CREATED", report.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
