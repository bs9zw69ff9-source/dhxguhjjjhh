using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The built-in Fallout set, and that it says the same thing the file said.
/// </summary>
/// <remarks>
/// THE RISK IN SHIPPING LADDERS AS CODE is that they drift from the file they replaced. Every
/// name here is a roster FILE on a live server, so a typo does not fail - it writes a brand
/// new file the game never opens, reports success, and leaves the player unable to spawn.
/// That exact bug is recorded in FactionRegistry, over gambino.txt against gambinospawn.txt.
///
/// So the preset is compared against the shipped example JSON, parsed by the real loader,
/// rather than against a restatement of itself.
/// </remarks>
public class FalloutPresetTests
{
    private static FactionSet FromExample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "factions.fallout.example.json");
        var loaded = FactionsFile.Load(Path.GetFullPath(path));
        Assert.NotNull(loaded.Set);
        return loaded.Set!;
    }

    [Fact]
    public void ThePresetMatchesTheExampleFileItReplaces()
    {
        /* Faction for faction, rank for rank, file for file. If the two ever disagree, one of
           them is wrong about what is on disk and there is no way to tell which from either
           one alone. */
        var preset = FactionRegistry.FalloutSet;
        var file = FromExample();

        Assert.Equal(file.Names.OrderBy(n => n, StringComparer.Ordinal),
                     preset.Names.OrderBy(n => n, StringComparer.Ordinal));

        foreach (var name in file.Names)
        {
            var expected = file.Get(name)!;
            var actual = preset.Get(name)!;

            Assert.Equal(expected.Order, actual.Order);
            Assert.Equal(expected.Default, actual.Default);
            Assert.Equal(expected.SpawnFile, actual.SpawnFile);
            Assert.Equal(
                expected.RankFiles.OrderBy(r => r.Key, StringComparer.Ordinal),
                actual.RankFiles.OrderBy(r => r.Key, StringComparer.Ordinal));
            Assert.Equal(
                expected.Subclasses.OrderBy(r => r.Key, StringComparer.Ordinal),
                actual.Subclasses.OrderBy(r => r.Key, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void EachEnclaveSubclassWritesToItsOwnFile()
    {
        /* PINNED BECAUSE REPOINTING ONE IS SILENT. The file is what the game reads and the
           name is only what staff type into /subclass, so aiming a sub-class at a different
           file takes the class away from everybody already listed in the old one - in game,
           with the command still reporting success. These two were briefly crossed; this is
           what stops that happening again without somebody deciding to. */
        var enclave = FactionRegistry.FalloutSet.Get("Enclave")!;

        Assert.Equal("enclavehellfire.txt", enclave.Subclasses["Hellfire"]);
        Assert.Equal("enclavedemolition.txt", enclave.Subclasses["Demolition"]);
    }

    [Fact]
    public void BothEnclaveSubclassFilesAreCreatedAtStartup()
    {
        // A sub-class whose file the bot does not know about is one nobody can be added to:
        // EnsureRosterFiles only creates what RosterFilesOf lists.
        var files = RosterService.RosterFilesOf(FactionRegistry.FalloutSet);

        Assert.Contains("enclavedemolition.txt", files);
        Assert.Contains("enclavehellfire.txt", files);
    }

    [Fact]
    public void ThePresetIsAValidSetInItsOwnRight()
    {
        // Same validation startup runs: a rank with no file, a default that is not a rank.
        Assert.Empty(FactionRegistry.FalloutSet.Problems());
    }

    [Fact]
    public void NeitherKingsNorFollowersExists()
    {
        // The point of the change: they are gone from the pickers because they are gone
        // from the set, with no file to edit for it.
        Assert.Null(FactionRegistry.FalloutSet.Get("Kings"));
        Assert.Null(FactionRegistry.FalloutSet.Get("Followers"));
        Assert.Equal(4, FactionRegistry.FalloutSet.Names.Count);
    }

    [Fact]
    public void VeteranRangerKeepsTheOldRangerFile()
    {
        /* Renaming a sub-class must not repoint its file: the file is what the game reads,
           and everybody already in ncrranger.txt would silently lose the access. */
        Assert.Equal("ncrranger.txt", FactionRegistry.FalloutSet.Get("NCR")!.Subclasses["Veteran Ranger"]);
        Assert.Equal("ncrpatrolranger.txt", FactionRegistry.FalloutSet.Get("NCR")!.Subclasses["Patrol Ranger"]);
    }

    [Fact]
    public void TheFalloutSetIsWhatABotRunsWithNoConfigurationAtAll()
    {
        /* THE DEFAULT MOVED. There is one bot now and it is the Fallout one, so the ladders
           nothing is configured for are that server's - a line in a .env no longer stands
           between working and a bot quietly writing the police roster.

           The police ladders are still shipped whole, one name away. */
        Assert.Equal(["NCR", "Legion", "Brotherhood of Steel", "Enclave"], FactionRegistry.Default.Names);
        Assert.Equal(["Gambino", "Colombo", "NYPD"], FactionRegistry.Police.Names);
        Assert.Same(FactionRegistry.FalloutSet, FactionRegistry.Preset("default"));
        Assert.Same(FactionRegistry.Police, FactionRegistry.Preset("police"));
    }

    [Theory]
    [InlineData("fallout")]
    [InlineData("Fallout")]
    [InlineData("  FALLOUT  ")]
    public void ThePresetNameIsForgivingAboutCaseAndSpace(string name)
    {
        // It is typed into a .env by hand, and a trailing space is not a different set.
        Assert.Same(FactionRegistry.FalloutSet, FactionRegistry.Preset(name));
    }

    [Theory]
    [InlineData("falout")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownNameIsNullSoStartupCanRefuseIt(string? name)
    {
        /* Null rather than a fallback. Quietly running the built-in factions because the name
           was misspelled is a themed bot writing the other bot's roster files. */
        Assert.Null(FactionRegistry.Preset(name));
    }
}

/// <summary>
/// Where the whitelist bot's commands are registered, which decides how long a change to
/// them takes to appear.
/// </summary>
/// <remarks>
/// GLOBAL COMMANDS TAKE UP TO AN HOUR. The whitelist bot registered globally and nowhere
/// else, so adding a sub-class looked exactly like the bot ignoring it - the picker kept the
/// old list for the rest of the hour with nothing saying why. Guild-scoped registration is
/// immediate, and the client already knows which guilds it is in.
/// </remarks>
public class WhitelistCommandScopeTests
{
    [Fact]
    public void TheGlobalFallbackIsClearedOnlyWhenEveryGuildTookTheUpdate()
    {
        /* THE OUTAGE THIS AVOIDS. Clearing the global set while one guild failed leaves that
           faction's staff with no /whitelist at all - and a guild-scoped registration that
           never happened leaves nothing behind to fall back to. */
        Assert.True(DiscordGateway.ClearGlobals(registered: 3, failed: 0));
        Assert.False(DiscordGateway.ClearGlobals(registered: 2, failed: 1));
    }

    [Fact]
    public void WithNoGuildsAtAllTheGlobalSetIsLeftAlone()
    {
        // Guild scoping has nothing to aim at, so global registration is still the only way
        // the commands exist anywhere.
        Assert.False(DiscordGateway.ClearGlobals(registered: 0, failed: 0));
    }
}
