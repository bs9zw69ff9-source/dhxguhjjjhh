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
            var created = service.EnsureRosterFiles();

            Assert.Equal(5, created);   // six owned, one already there
            Assert.All(RosterService.RosterFilesOf(TwoFactions()),
                f => Assert.True(File.Exists(Path.Combine(directory, f)), $"{f} should exist"));

            // The one with members is untouched, contents and all.
            Assert.Equal("SomeExistingPlayer\n", File.ReadAllText(populated));

            // And a second start creates nothing, rather than rewriting what it just made.
            Assert.Equal(0, service.EnsureRosterFiles());
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

        Assert.Equal(0, service.EnsureRosterFiles());
        Assert.False(Directory.Exists(missing), "the roster directory must never be created");
    }
}
