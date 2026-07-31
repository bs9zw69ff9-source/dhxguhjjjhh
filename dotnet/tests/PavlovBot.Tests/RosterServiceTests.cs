using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The rosters are plain text the game reads LIVE. There is no transaction and no undo, so
/// every one of these is about not corrupting a file that is already in use.
/// </summary>
public class RosterServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-roster-" + Guid.NewGuid().ToString("N"));
    private readonly string _backups;
    private readonly RosterService _rosters;

    private static readonly FactionDefinition Nypd = FactionRegistry.Get("NYPD")!;
    private static readonly FactionDefinition Gambino = FactionRegistry.Get("Gambino")!;

    public RosterServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _backups = Path.Combine(_directory, "_bak");
        _rosters = new RosterService(_directory, NullLogger<RosterService>.Instance, _backups);
    }

    private void Seed(string file, params string[] names) =>
        File.WriteAllText(Path.Combine(_directory, file), string.Join("\n", names) + "\n");

    private IReadOnlyList<string> Contents(string file) => _rosters.Read(file) ?? [];

    [Fact]
    public async Task JoiningLandsAtTheDefaultRank()
    {
        var decision = await _rosters.JoinAsync(Nypd, "Alice");

        Assert.True(decision.IsAllowed);
        Assert.Equal("Cadet", decision.Rank);
        Assert.Contains("Alice", Contents("policecadet.txt"));
    }

    [Fact]
    public async Task APlayerCannotBeInTwoFactions()
    {
        await _rosters.JoinAsync(Gambino, "Alice");
        var decision = await _rosters.JoinAsync(Nypd, "Alice");

        Assert.Equal(MembershipOutcome.AlreadyInAnotherFaction, decision.Outcome);
        Assert.Equal("Gambino", decision.Conflict);
        Assert.DoesNotContain("Alice", Contents("policecadet.txt"));
    }

    [Fact]
    public async Task AFullEntryRankRefusesTheJoin()
    {
        Seed("policecadet.txt", Enumerable.Range(0, 50).Select(i => $"Player{i}").ToArray());

        var decision = await _rosters.JoinAsync(Nypd, "Alice");
        Assert.Equal(MembershipOutcome.RankFull, decision.Outcome);
        Assert.Equal(50, decision.Cap);
    }

    [Fact]
    public async Task APromotionMovesThemBetweenFilesRatherThanAddingASecondRank()
    {
        await _rosters.JoinAsync(Nypd, "Alice");
        var decision = await _rosters.ChangeRankAsync(Nypd, "Alice", +1);

        Assert.Equal("Patrolman", decision.Rank);
        Assert.Contains("Alice", Contents("policepatrolman.txt"));
        Assert.DoesNotContain("Alice", Contents("policecadet.txt"));
    }

    [Fact]
    public async Task APromotionCleansUpAStaleDuplicateRank()
    {
        /* The storage permits a name in two rank files at once. Removing only from their
           CURRENT rank would leave the other behind, and then a promotion silently gives
           them two ranks - one of which outranks where they were just moved to. */
        Seed("policecadet.txt", "Alice");
        Seed("policesergeant.txt", "Alice");

        // They are found at their HIGHEST rank, so the promotion is from Sergeant.
        var decision = await _rosters.ChangeRankAsync(Nypd, "Alice", +1);
        Assert.Equal("Lieutenant", decision.Rank);

        // Exactly one rank file mentions them afterwards - across ALL of them.
        Assert.Equal(1, Nypd.RankFiles.Values.Sum(f => Contents(f).Count(n => n == "Alice")));
        Assert.Contains("Alice", Contents("policelieutenant.txt"));
    }

    [Fact]
    public async Task ADemotionIntoAFullRankIsRefused()
    {
        // Overflow is overflow regardless of direction. Checking only the promote path is
        // a real bug in this shape of code.
        Seed("policesergeant.txt", "Alice");
        Seed("policecorporal.txt", Enumerable.Range(0, 15).Select(i => $"Player{i}").ToArray());

        var decision = await _rosters.ChangeRankAsync(Nypd, "Alice", -1);
        Assert.Equal(MembershipOutcome.RankFull, decision.Outcome);
        Assert.Contains("Alice", Contents("policesergeant.txt"));
    }

    [Fact]
    public async Task LeavingClearsEveryFileIncludingSubClasses()
    {
        /* A member listed in two files - which the storage permits - would otherwise be
           half-removed and reappear on the next read. */
        Seed("policecadet.txt", "Alice", "Bob");
        Seed("policesergeant.txt", "Alice");
        Seed("policedetective.txt", "Alice");

        Assert.True((await _rosters.LeaveAsync(Nypd, "Alice")).IsAllowed);

        Assert.DoesNotContain("Alice", Contents("policecadet.txt"));
        Assert.DoesNotContain("Alice", Contents("policesergeant.txt"));
        Assert.DoesNotContain("Alice", Contents("policedetective.txt"));
        Assert.Contains("Bob", Contents("policecadet.txt"));
    }

    [Fact]
    public async Task RemovingSomebodyWhoIsNotThereIsRefusedRatherThanSilentlySucceeding()
    {
        Assert.Equal(MembershipOutcome.NotWhitelisted, (await _rosters.LeaveAsync(Nypd, "Nobody")).Outcome);
    }

    [Fact]
    public async Task AWriteThatWouldWipeARosterIsRefused()
    {
        /* An empty roster file is perfectly VALID to the game, so a parsing bug producing an
           empty list would silently strip a faction mid-round with no error anywhere. The
           guard is on the SIZE OF THE CHANGE, not the shape of the data. */
        Seed("policecadet.txt", Enumerable.Range(0, 20).Select(i => $"Player{i}").ToArray());

        Assert.False(await _rosters.WriteAsync("policecadet.txt", []));
        Assert.Equal(20, Contents("policecadet.txt").Count);
    }

    [Fact]
    public async Task ADeliberateWipeIsAllowedWithAllowBulk()
    {
        Seed("policecadet.txt", Enumerable.Range(0, 20).Select(i => $"Player{i}").ToArray());

        Assert.True(await _rosters.WriteAsync("policecadet.txt", [], allowBulk: true));
        Assert.Empty(Contents("policecadet.txt"));
    }

    [Fact]
    public async Task APreWriteBackupIsKeptOutsideTheGameTree()
    {
        /* A .bak sitting next to the live files is one glob away from being read as a
           roster - which is why it goes somewhere else entirely. */
        Seed("policecadet.txt", "Alice");
        await _rosters.JoinAsync(Nypd, "Bob");

        var backup = Path.Combine(_backups, "policecadet.txt.bak");
        Assert.True(File.Exists(backup));
        Assert.Contains("Alice", File.ReadAllText(backup));
    }

    [Fact]
    public async Task ASubClassIsAdditiveAndTheMemberKeepsTheirRank()
    {
        await _rosters.JoinAsync(Nypd, "Alice");
        Assert.True((await _rosters.ChangeSubclassAsync(Nypd, "Alice", "Detective", removing: false)).IsAllowed);

        Assert.Contains("Alice", Contents("policedetective.txt"));
        Assert.Contains("Alice", Contents("policecadet.txt"));
    }

    [Fact]
    public async Task OnlyOneSubClassAtATime()
    {
        await _rosters.ChangeSubclassAsync(Nypd, "Alice", "Detective", removing: false);
        var decision = await _rosters.ChangeSubclassAsync(Nypd, "Alice", "Vice Officer", removing: false);

        Assert.Equal(MembershipOutcome.AlreadyHasSubclass, decision.Outcome);
        Assert.Equal("Detective", decision.Conflict);
    }

    [Fact]
    public async Task AMemberIsReportedAtTheirHighestRank()
    {
        // The top rank is the one that actually governs what they can do in game.
        Seed("policecadet.txt", "Alice");
        Seed("policecaptain.txt", "Alice");

        Assert.Equal("Captain", (await _rosters.FindAsync("Alice"))!.Rank);
    }

    [Fact]
    public async Task TheRosterListsEveryMemberOnce()
    {
        Seed("policecadet.txt", "Alice", "Bob");
        Seed("policesergeant.txt", "Alice");

        var roster = await _rosters.RosterAsync(Nypd);
        Assert.Equal(2, roster.Count);
        Assert.Equal("Sergeant", roster.Single(m => m.Player == "Alice").Rank);
    }

    [Fact]
    public void AnUnconfiguredDirectoryDisablesEverythingSafely()
    {
        var disabled = new RosterService(null, NullLogger<RosterService>.Instance);
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.Read("policecadet.txt"));
    }

    [Fact]
    public async Task ConcurrentEditsToOneRosterDoNotLoseMembers()
    {
        // Two commands read-modify-writing one file would otherwise silently drop a member.
        Seed("policecadet.txt", "Existing");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            var current = _rosters.Read("policecadet.txt") ?? [];
            await _rosters.WriteAsync("policecadet.txt", [.. current, $"Player{i}"]);
        }));

        Assert.Contains("Existing", Contents("policecadet.txt"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
