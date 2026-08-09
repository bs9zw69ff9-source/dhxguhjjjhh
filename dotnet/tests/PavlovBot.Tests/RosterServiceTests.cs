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
    private static readonly FactionDefinition Gambino = TestFactions.Other;

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

    /* ONE FACTION PER PLAYER IS STILL THE RULE, and it is still tested - just not here.

       This drove it through RosterService, which resolves a conflicting membership by
       looking the other faction up in the registry. With Gambino and Colombo removed there
       is only NYPD, so "already in another faction" cannot be reached through this door at
       all: any stand-in faction is absent from the registry and therefore invisible to the
       conflict check.

       The rule itself is covered where it lives, in FactionTests against
       MembershipRules.Join, which takes the existing factions as an argument and does not
       need them to exist on this server. Restore a RosterService-level version of this the
       day a second faction is added. */
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

    /// <summary>
    /// With no roster directory, every membership change says so - it does not blame the
    /// faction, and it does not claim the member is not whitelisted.
    /// </summary>
    /// <remarks>
    /// THE BUG THIS PINS. /whitelist add answered "Refused / UnknownFaction" for a faction
    /// picked from a dropdown, because JoinAsync mapped an unreadable roster onto the same
    /// outcome as a faction that does not exist. Reported that way it sends somebody looking
    /// for a spelling mistake instead of at FACTION_ROLES_PATH.
    ///
    /// LeaveAsync was worse than useless: it reported "not whitelisted", which reads as
    /// "already done" for an account whose access is in fact untouched.
    /// </remarks>
    [Fact]
    public async Task WithNoRosterDirectoryEveryChangeReportsTheRosterNotTheFaction()
    {
        var disabled = new RosterService(null, NullLogger<RosterService>.Instance);

        Assert.Equal(MembershipOutcome.RosterUnavailable, (await disabled.JoinAsync(Nypd, "Alice")).Outcome);
        Assert.Equal(MembershipOutcome.RosterUnavailable, (await disabled.LeaveAsync(Nypd, "Alice")).Outcome);
        Assert.Equal(MembershipOutcome.RosterUnavailable, (await disabled.ChangeRankAsync(Nypd, "Alice", +1)).Outcome);
        Assert.Equal(MembershipOutcome.RosterUnavailable,
            (await disabled.ChangeSubclassAsync(Nypd, "Alice", Nypd.Subclasses.Keys.First(), removing: false)).Outcome);
    }

    /// <summary>A configured path that does not exist is the same as no path at all.</summary>
    /// <remarks>
    /// The likelier production shape: FACTION_ROLES_PATH set, pointing somewhere wrong. The
    /// bot deliberately does not create the directory, so this has to report rather than fix.
    /// </remarks>
    [Fact]
    public async Task AMissingDirectoryIsReportedAsUnavailableRatherThanCreated()
    {
        var missing = Path.Combine(_directory, "no-such-directory");
        var service = new RosterService(missing, NullLogger<RosterService>.Instance, _backups);

        Assert.False(service.Enabled);
        Assert.Equal(MembershipOutcome.RosterUnavailable, (await service.JoinAsync(Nypd, "Alice")).Outcome);
        Assert.False(Directory.Exists(missing));
    }

    /* THE OTHER HALF OF THE SPLIT is already pinned by
       RemovingSomebodyWhoIsNotThereIsRefusedRatherThanSilentlySucceeding above: with a
       readable roster, an absent member is still NotWhitelisted. Without it the two tests
       here would pass against a service that answered RosterUnavailable to everything. */

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
