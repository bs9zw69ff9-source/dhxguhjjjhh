using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
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

    /// <summary>
    /// Narcotics Bureau behaves like every other sub-class, including the one-at-a-time rule.
    /// </summary>
    /// <remarks>
    /// Driven entirely off the registry data, so this is really asking whether adding a
    /// sub-class needs anything beyond a line of data. It should not, and if that ever stops
    /// being true this is what says so.
    /// </remarks>
    [Fact]
    public async Task NarcoticsBureauIsAssignableAndStillOnlyOneSubClass()
    {
        await _rosters.JoinAsync(Nypd, "Alice");

        Assert.True((await _rosters.ChangeSubclassAsync(Nypd, "Alice", "Narcotics Bureau", removing: false)).IsAllowed);
        Assert.Contains("Alice", Contents("policenarcotics.txt"));
        Assert.Contains("Alice", Contents("policecadet.txt"));       // additive, not a rank move

        var second = await _rosters.ChangeSubclassAsync(Nypd, "Alice", "Detective", removing: false);
        Assert.Equal(MembershipOutcome.AlreadyHasSubclass, second.Outcome);
        Assert.Equal("Narcotics Bureau", second.Conflict);
    }

    /// <summary>Leaving the faction takes the new sub-class with it.</summary>
    /// <remarks>
    /// A sub-class file left behind is access nobody can see: the rank files say the player is
    /// gone, and the game still honours the sub-class whitelist. Worth pinning per sub-class
    /// rather than trusting the loop, since the loop reads the registry and a sub-class added
    /// to the wrong dictionary would be silently skipped.
    /// </remarks>
    [Fact]
    public async Task LeavingClearsTheNarcoticsSubClassToo()
    {
        await _rosters.JoinAsync(Nypd, "Alice");
        await _rosters.ChangeSubclassAsync(Nypd, "Alice", "Narcotics Bureau", removing: false);

        await _rosters.LeaveAsync(Nypd, "Alice");

        Assert.DoesNotContain("Alice", Contents("policenarcotics.txt"));
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

    /* ─── THE SPAWN FILE ───────────────────────────────────────────────────────────────

       WHAT WENT WRONG. The rank files say what a member is; the SPAWN file says whether they
       are in the faction at all, and it is the one the game consults before letting somebody
       play as it. The port wrote the rank files and not the spawn file, so /whitelist add
       reported success, /whitelist list showed the member, and in game nothing changed.

       It is invisible from inside the bot: every surface the staff can see reads the rank
       files, which were correct. The only witness is the install, where the Node bot's spawn
       file is byte-for-byte the union of the rank files. These tests are that witness. */

    [Fact]
    public async Task JoiningWritesTheSpawnFileAsWellAsTheRankFile()
    {
        await _rosters.JoinAsync(Nypd, "Alice");

        Assert.Contains("Alice", Contents("policecadet.txt"));
        Assert.Contains("Alice", Contents(Nypd.SpawnFile));
    }

    [Fact]
    public async Task LeavingClearsTheSpawnFileToo()
    {
        await _rosters.JoinAsync(Nypd, "Alice");

        await _rosters.LeaveAsync(Nypd, "Alice");

        /* The dangerous asymmetry, and the reason this is asserted separately from the rank
           file: a member cleared from the ranks but left in the spawn file reads as removed
           on every staff-facing surface while the game still lets them play. */
        Assert.DoesNotContain("Alice", Contents(Nypd.SpawnFile));
        Assert.DoesNotContain("Alice", Contents("policecadet.txt"));
    }

    [Fact]
    public async Task PromotionKeepsTheMemberInTheSpawnFile()
    {
        await _rosters.JoinAsync(Nypd, "Alice");

        var decision = await _rosters.ChangeRankAsync(Nypd, "Alice", +1);

        Assert.True(decision.IsAllowed);
        Assert.Contains("Alice", Contents("policepatrolman.txt"));
        Assert.Contains("Alice", Contents(Nypd.SpawnFile));
    }

    /// <summary>
    /// A promotion repairs a member who is missing from the spawn file.
    /// </summary>
    /// <remarks>
    /// The install already holds members whitelisted while the port was dropping the spawn
    /// write. Without this they stay unable to spawn until somebody notices and re-adds them
    /// by hand, which means until a player complains.
    /// </remarks>
    [Fact]
    public async Task PromotionRestoresAMissingSpawnEntry()
    {
        Seed("policecadet.txt", "Alice");        // on the ladder, absent from the spawn file

        await _rosters.ChangeRankAsync(Nypd, "Alice", +1);

        Assert.Contains("Alice", Contents(Nypd.SpawnFile));
    }

    /// <summary>
    /// A faction with no ladder uses one file for both, and is not listed in it twice.
    /// </summary>
    /// <remarks>
    /// Nothing de-duplicates a roster on read, so a doubled name would show the faction one
    /// member over its real size and count twice against a cap.
    /// </remarks>
    [Fact]
    public async Task ALadderlessFactionIsWrittenOnceNotTwice()
    {
        var spawnOnly = FactionRegistry.All.Values.First(f => !f.HasRanks);
        var service = new RosterService(_directory, NullLogger<RosterService>.Instance, _backups);

        await service.JoinAsync(spawnOnly, "Alice");

        Assert.Equal(["Alice"], service.Read(spawnOnly.SpawnFile)!);
    }

    /* ─── WIPING A ROSTER ──────────────────────────────────────────────────────────────

       The one operation whose purpose is mass deletion, and therefore the one place the
       destruction guard is deliberately bypassed. These pin both halves: that it really does
       empty the faction, and that it does not reach past it. */

    [Fact]
    public async Task WipingClearsEveryFileTheFactionOwns()
    {
        Seed("policecadet.txt", "Alice");
        Seed("policecaptain.txt", "Bob");
        Seed("policevice.txt", "Alice");            // a sub-class, held alongside a rank
        Seed(Nypd.SpawnFile, "Alice", "Bob");

        var result = await _rosters.WipeAsync(Nypd);

        Assert.Equal(MembershipOutcome.Allowed, result.Outcome);
        Assert.Empty(Contents("policecadet.txt"));
        Assert.Empty(Contents("policecaptain.txt"));
        Assert.Empty(Contents("policevice.txt"));
        Assert.Empty(Contents(Nypd.SpawnFile));
    }

    /// <summary>
    /// The count is people, not lines.
    /// </summary>
    /// <remarks>
    /// Everybody appears at least twice - once in a rank file, once in the spawn file - and a
    /// sub-class holder three times. Counting rows would report a two-member faction as five
    /// and make the confirmation prompt lie about the size of what is being destroyed.
    /// </remarks>
    [Fact]
    public async Task TheWipeCountsMembersRatherThanRows()
    {
        Seed("policecadet.txt", "Alice");
        Seed("policecaptain.txt", "Bob");
        Seed("policevice.txt", "Alice");
        Seed(Nypd.SpawnFile, "Alice", "Bob");

        Assert.Equal(2, (await _rosters.WipeAsync(Nypd)).Removed);
    }

    /// <summary>
    /// A wipe is not stopped by the guard that refuses mass deletion.
    /// </summary>
    /// <remarks>
    /// THE GUARD EXISTS FOR THE OPPOSITE CASE. Everywhere else, a write that drops most of a
    /// roster is a failed read about to be saved as truth, so it is refused. Here it is the
    /// entire point, and without the bypass the command would report success on every wipe
    /// while the guard quietly rejected each write - which is exactly the failure shape this
    /// codebase keeps producing.
    /// </remarks>
    [Fact]
    public async Task TheWipeIsNotBlockedByTheBulkDeletionGuard()
    {
        var many = Enumerable.Range(0, RosterWriteGuard.BulkDropLimit * 4).Select(i => $"Player{i}").ToArray();
        Seed("policecadet.txt", many);
        Seed(Nypd.SpawnFile, many);

        var result = await _rosters.WipeAsync(Nypd);

        Assert.Equal(MembershipOutcome.Allowed, result.Outcome);
        Assert.Equal(many.Length, result.Removed);
        Assert.Empty(Contents("policecadet.txt"));
    }

    /// <summary>A wipe keeps the pre-write copy that makes it recoverable by hand.</summary>
    /// <remarks>
    /// The confirmation prompt tells an owner the backup exists. If that were not true the
    /// prompt would be talking somebody into an irreversible action by promising an undo.
    /// </remarks>
    [Fact]
    public async Task WipingBacksUpEachRosterFirst()
    {
        Seed("policecadet.txt", "Alice", "Bob");

        await _rosters.WipeAsync(Nypd);

        var backup = await File.ReadAllTextAsync(Path.Combine(_backups, "policecadet.txt.bak"));
        Assert.Contains("Alice", backup, StringComparison.Ordinal);
        Assert.Contains("Bob", backup, StringComparison.Ordinal);
    }

    /// <summary>A wipe does not reach into another faction.</summary>
    [Fact]
    public async Task WipingOneFactionLeavesTheOthersAlone()
    {
        var other = FactionRegistry.All.Values.First(f => f.Name != Nypd.Name);
        Seed("policecadet.txt", "Alice");
        Seed(other.SpawnFile, "Bob");

        await _rosters.WipeAsync(Nypd);

        Assert.Contains("Bob", Contents(other.SpawnFile));
    }

    /// <summary>
    /// With no roster directory the wipe reports it rather than reporting success.
    /// </summary>
    /// <remarks>
    /// A destructive command that answers "done" when it did nothing is worse than one that
    /// fails: staff move on believing the faction is empty and never look again.
    /// </remarks>
    [Fact]
    public async Task WipingWithNoRosterDirectoryIsReportedRatherThanClaimed()
    {
        var service = new RosterService(Path.Combine(_directory, "nope"), NullLogger<RosterService>.Instance, _backups);

        var result = await service.WipeAsync(Nypd);

        Assert.Equal(MembershipOutcome.RosterUnavailable, result.Outcome);
        Assert.Equal(0, result.Removed);
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
