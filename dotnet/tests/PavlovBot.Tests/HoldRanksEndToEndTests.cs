using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// hold_ranks, through the wiring Program actually uses.
/// </summary>
/// <remarks>
/// The existing coverage drives RosterService with a hand-written callback, which proves the
/// roster writer honours the flag and nothing about whether the flag reaches it. The live
/// path is /whitelist add -> FactionMembers.RememberAsync -> the store -> a callback that
/// reads it back by NAME. Every one of those is a place it can be lost.
/// </remarks>
public class HoldRanksEndToEndTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-hold-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly FactionMembers _members;
    private readonly RosterService _rosters;

    private static readonly FactionDefinition Nypd = FactionRegistry.Get("NYPD")!;
    private const ulong Discord = 424242424242424242;

    public HoldRanksEndToEndTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new SerializedStore(new FileKeyValueBackend(Path.Combine(_directory, "data")), new SystemTextJsonCodec());
        _members = new FactionMembers(_store);

        // Exactly how Program wires it: resolved on each call, by name, off the index.
        _rosters = new RosterService(_directory, NullLogger<RosterService>.Instance, factions: FactionRegistry.Police,
            backupDirectory: null, holdsAllRanks: name => _members.HoldsAllRanks(name));
    }

    private IReadOnlyList<string> Contents(string file) => _rosters.Read(file) ?? [];

    private Task RememberAsync(bool hold) =>
        _members.RememberAsync(Discord,
            new FactionMember("NYPD", "Alice", DateTimeOffset.UtcNow, "Mod") { HoldsAllRanks = hold });

    [Fact]
    public async Task TheFlagSurvivesTheStore()
    {
        await RememberAsync(hold: true);

        Assert.True(_members.Of(Discord)!.HoldsAllRanks);
        Assert.True(_members.HoldsAllRanks("Alice"));
    }

    [Fact]
    public async Task TheFlagIsFoundByNameWhateverTheCasing()
    {
        // The roster writer works in names read out of a file; the index was written from
        // what a moderator typed. They are the same person and need not match in case.
        await RememberAsync(hold: true);

        Assert.True(_members.HoldsAllRanks("alice"));
        Assert.True(_members.HoldsAllRanks("ALICE"));
    }

    [Fact]
    public async Task APromotionKeepsEveryRankBelow()
    {
        await RememberAsync(hold: true);
        await _rosters.JoinAsync(Nypd, "Alice");

        await _rosters.ChangeRankAsync(Nypd, "Alice", +1);
        await _rosters.ChangeRankAsync(Nypd, "Alice", +1);

        foreach (var rank in Nypd.Order.Take(3))
            Assert.Contains("Alice", Contents(Nypd.RankFiles[rank]));
    }

    [Fact]
    public async Task WithoutTheFlagOnlyTheCurrentRankIsHeld()
    {
        await RememberAsync(hold: false);
        await _rosters.JoinAsync(Nypd, "Alice");

        await _rosters.ChangeRankAsync(Nypd, "Alice", +1);

        Assert.Contains("Alice", Contents(Nypd.RankFiles[Nypd.Order[1]]));
        Assert.DoesNotContain("Alice", Contents(Nypd.RankFiles[Nypd.Order[0]]));
    }

    [Fact]
    public async Task ReRecordingWithoutTheFlagIsWhatTurnsItOff()
    {
        /* THE MECHANISM. hold_ranks is an OPTIONAL boolean, and /whitelist add read it as
           `as bool? ?? false` - so leaving the box unticked wrote false over a previously set
           true. Any later add for that member turned the preference off silently: fixing a
           typo in their in-game name, or using the new rank option to place them, both go
           through the same handler.

           The symptom is not "the flag does not work". It is a promotion stripping the ranks
           below, weeks after somebody ticked the box, with nothing in between that looks like
           it touched the setting. */
        await RememberAsync(hold: true);
        Assert.True(_members.HoldsAllRanks("Alice"));

        // What the handler did when the option was omitted.
        await RememberAsync(hold: false);

        Assert.False(_members.HoldsAllRanks("Alice"));
    }

    [Fact]
    public async Task AnOmittedOptionNowKeepsWhateverWasAlreadySet()
    {
        /* THE FIX, at the level the command works at: null is "not supplied" and false is
           "supplied false". Only what was named is changed - the same rule /setroles and
           /setrconroles already follow, for the same reason. */
        await RememberAsync(hold: true);

        var omitted = (bool?)null;
        var effective = omitted ?? _members.Of(Discord)?.HoldsAllRanks ?? false;

        Assert.True(effective);
    }

    [Fact]
    public async Task AnExplicitFalseStillTurnsItOff()
    {
        // Preserving an omitted option must not make the preference impossible to clear.
        await RememberAsync(hold: true);

        var supplied = (bool?)false;
        var effective = supplied ?? _members.Of(Discord)?.HoldsAllRanks ?? false;

        Assert.False(effective);
    }

    [Fact]
    public async Task APromotionAfterTheFlagIsClearedStripsTheRanksBelow()
    {
        /* THE REPORTED SYMPTOM, end to end: the names come out of the lower files and the
           member simply moves up. This is what a cleared flag looks like from the .txt side,
           and it is why the clearing had to be found rather than the writer blamed. */
        await RememberAsync(hold: true);
        await _rosters.JoinAsync(Nypd, "Alice");
        await _rosters.ChangeRankAsync(Nypd, "Alice", +1);
        Assert.Contains("Alice", Contents(Nypd.RankFiles[Nypd.Order[0]]));

        await RememberAsync(hold: false);
        await _rosters.ChangeRankAsync(Nypd, "Alice", +1);

        Assert.DoesNotContain("Alice", Contents(Nypd.RankFiles[Nypd.Order[0]]));
        Assert.DoesNotContain("Alice", Contents(Nypd.RankFiles[Nypd.Order[1]]));
        Assert.Contains("Alice", Contents(Nypd.RankFiles[Nypd.Order[2]]));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
