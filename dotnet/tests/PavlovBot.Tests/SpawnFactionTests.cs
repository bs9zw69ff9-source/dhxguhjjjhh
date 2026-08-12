using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The mafias as spawn access, and members reachable by Discord account.
/// </summary>
public class SpawnFactionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"members-{Guid.NewGuid():N}");

    private FactionMembers Members()
    {
        Directory.CreateDirectory(_directory);
        return new FactionMembers(new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec()));
    }

    // ---- the factions ----

    [Theory]
    [InlineData("Gambino")]
    [InlineData("Colombo")]
    public void AMafiaIsSpawnAccessWithNoLadder(string name)
    {
        var faction = FactionRegistry.Get(name);

        Assert.NotNull(faction);
        Assert.False(faction!.HasRanks);
        Assert.Single(faction.Order);
        Assert.Single(faction.RankFiles);
        Assert.Empty(faction.RankCaps);      // nothing to cap when there is one rank
        Assert.Empty(faction.Subclasses);
    }

    [Fact]
    public void NypdStillHasItsLadder()
    {
        /* The regression that would matter most: making the mafias rank-less must not flatten
           the police, whose eight ranks and caps are the reason the ladder exists at all. */
        var nypd = FactionRegistry.Get("NYPD")!;

        Assert.True(nypd.HasRanks);
        Assert.Equal(8, nypd.Order.Count);
        Assert.NotEmpty(nypd.RankCaps);
    }

    [Fact]
    public void EachFactionWritesItsOwnFile()
    {
        // Two factions sharing a roster file would whitelist each other's members.
        var files = FactionRegistry.All.Values.SelectMany(f => f.RankFiles.Values).ToList();

        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---- the Discord index ----

    [Fact]
    public async Task AMembershipIsFoundByDiscordAccount()
    {
        var members = Members();
        await members.RememberAsync(42, new FactionMember("Gambino", "Butter Life", DateTimeOffset.UtcNow, "mod"));

        var found = members.Of(42);

        Assert.NotNull(found);
        Assert.Equal("Gambino", found!.Faction);
        Assert.Equal("Butter Life", found.Name);
    }

    [Fact]
    public async Task RecordingAgainReplacesRatherThanDuplicates()
    {
        /* One faction per player is already the rule, so a second record for one account
           describes a state that cannot exist - and would make removal depend on which entry
           happened to be found first. */
        var members = Members();
        await members.RememberAsync(42, new FactionMember("Gambino", "Old Name", DateTimeOffset.UtcNow, "mod"));
        await members.RememberAsync(42, new FactionMember("NYPD", "New Name", DateTimeOffset.UtcNow, "mod"));

        Assert.Equal("NYPD", members.Of(42)!.Faction);
        Assert.Single(members.InFaction("NYPD"));
        Assert.Empty(members.InFaction("Gambino"));
    }

    [Fact]
    public async Task ForgettingLeavesNothingBehind()
    {
        var members = Members();
        await members.RememberAsync(42, new FactionMember("Colombo", "Alice", DateTimeOffset.UtcNow, "mod"));

        await members.ForgetAsync(42);

        Assert.Null(members.Of(42));
    }

    [Fact]
    public async Task ForgettingSomebodyWithNoRecordIsHarmless()
    {
        var members = Members();
        await members.ForgetAsync(999);   // must not throw, must not create an entry

        Assert.Null(members.Of(999));
    }

    [Fact]
    public async Task ASnowflakeSurvivesTheRoundTrip()
    {
        /* Discord ids run past 2^53, where a JSON number would lose precision. Stored as a
           string for exactly that reason - a mangled id points the command at nobody. */
        const ulong big = 1014251293159731310UL;
        var members = Members();
        await members.RememberAsync(big, new FactionMember("NYPD", "Alice", DateTimeOffset.UtcNow, "mod"));

        Assert.Equal(big, members.InFaction("NYPD").Single().DiscordId);
        Assert.Equal("Alice", members.Of(big)!.Name);
    }

    [Fact]
    public async Task TheIndexFollowsTheRosterFiles()
    {
        /* The file is the source of truth. An entry whose name is no longer on any roster
           describes a membership that does not exist, and acting on it would promote or
           remove somebody who is not in the faction. */
        var members = Members();
        await members.RememberAsync(1, new FactionMember("NYPD", "StillHere", DateTimeOffset.UtcNow, "mod"));
        await members.RememberAsync(2, new FactionMember("NYPD", "RemovedByHand", DateTimeOffset.UtcNow, "mod"));

        var dropped = await members.ForgetMissingAsync(["StillHere"]);

        Assert.Equal(1, dropped);
        Assert.NotNull(members.Of(1));
        Assert.Null(members.Of(2));
    }

    [Fact]
    public async Task AnEmptyRosterDoesNotWipeTheIndexSilently()
    {
        // It DOES clear it - that is correct - but the count is reported so a caller that
        // just failed to read the files can tell the difference from a real removal.
        var members = Members();
        await members.RememberAsync(1, new FactionMember("NYPD", "Alice", DateTimeOffset.UtcNow, "mod"));

        Assert.Equal(1, await members.ForgetMissingAsync([]));
    }

    /// <summary>
    /// Wiping a faction forgets that faction's records and nobody else's.
    /// </summary>
    /// <remarks>
    /// The index maps a Discord account to an in-game name, and /promotion, /demotion and
    /// /whitelist remove all act through it. Left behind after a wipe it points at people who
    /// are on no roster, and every one of those commands then acts on a membership that does
    /// not exist.
    ///
    /// The other half - not touching the other factions - is the reason this exists rather
    /// than reusing ForgetMissingAsync, which takes the names still whitelisted ANYWHERE and
    /// would need every other roster assembled correctly to avoid forgetting live members.
    /// </remarks>
    [Fact]
    public async Task WipingAFactionForgetsOnlyThatFactionsRecords()
    {
        var members = Members();
        await members.RememberAsync(1, new FactionMember("NYPD", "Officer", DateTimeOffset.UtcNow, "owner"));
        await members.RememberAsync(2, new FactionMember("NYPD", "Sergeant", DateTimeOffset.UtcNow, "owner"));
        await members.RememberAsync(3, new FactionMember("Gambino", "Wiseguy", DateTimeOffset.UtcNow, "owner"));

        var dropped = await members.ForgetFactionAsync("NYPD");

        Assert.Equal(2, dropped);
        Assert.Null(members.Of(1));
        Assert.Null(members.Of(2));
        Assert.NotNull(members.Of(3));
    }

    [Fact]
    public async Task ForgettingAFactionWithNoRecordsChangesNothing()
    {
        var members = Members();
        await members.RememberAsync(1, new FactionMember("NYPD", "Officer", DateTimeOffset.UtcNow, "owner"));

        Assert.Equal(0, await members.ForgetFactionAsync("Colombo"));
        Assert.NotNull(members.Of(1));
    }

    /// <summary>The faction name is matched the way Discord hands it back: case-insensitively.</summary>
    [Fact]
    public async Task ForgettingAFactionIgnoresCase()
    {
        var members = Members();
        await members.RememberAsync(1, new FactionMember("NYPD", "Officer", DateTimeOffset.UtcNow, "owner"));

        Assert.Equal(1, await members.ForgetFactionAsync("nypd"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
