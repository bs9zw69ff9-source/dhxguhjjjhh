using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// <c>/subclass</c> end to end for the GWPD, on a bot configured with nothing at all.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS AS ITS OWN FILE. The sub-classes were correct in the data for a week and
/// reached nobody: a JSON copy no deploy updated, then a deleted file the bot refused to boot
/// without, then a command list Discord had cached. Every one of those was invisible from the
/// code, and none of them would have been caught by testing the rules in isolation.
///
/// So this runs the REAL roster service against REAL files in a temp directory, with the
/// default faction set - no FACTION_SET, no FACTIONS_PATH - and asserts what ends up on disk,
/// which is the only thing the game reads.
/// </remarks>
public class DefaultSubclassTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pavlovbot-subclass-" + Guid.NewGuid().ToString("N"));

    private readonly RosterService _rosters;

    /// <summary>The set a bot gets with nothing configured. That is the point of the test.</summary>
    private static readonly FactionDefinition Gwpd = FactionRegistry.Default.Get("GWPD")!;

    public DefaultSubclassTests()
    {
        Directory.CreateDirectory(_directory);
        _rosters = new RosterService(_directory, NullLogger<RosterService>.Instance,
            Path.Combine(_directory, "_bak"));
    }

    private IReadOnlyList<string> Lines(string file) => _rosters.Read(file) ?? [];

    private async Task<string> JoinedMemberAsync(string name = "Officer12")
    {
        var join = await _rosters.JoinAsync(Gwpd, name);
        Assert.True(join.IsAllowed);
        return name;
    }

    [Fact]
    public void EverySubclassExistsOnTheDefaultSetWithItsOwnFile()
    {
        Assert.Equal("policevice.txt", Gwpd.Subclasses["Vice Officer"]);
        Assert.Equal("policedetective.txt", Gwpd.Subclasses["Detective"]);
        Assert.Equal("policetacticalresponse.txt", Gwpd.Subclasses["Tactical Response Unit"]);
        Assert.Equal("policenarcotics.txt", Gwpd.Subclasses["Narcotics Bureau"]);

        // None collides with a rank file, which would hand out the wrong loadout.
        foreach (var file in Gwpd.Subclasses.Values)
            Assert.DoesNotContain(file, Gwpd.RankFiles.Values, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssigningDetectiveWritesTheNameIntoTheDetectiveFile()
    {
        var player = await JoinedMemberAsync();

        var decision = await _rosters.ChangeSubclassAsync(Gwpd, player, "Detective", removing: false);

        Assert.True(decision.IsAllowed);
        Assert.Contains(player, Lines("policedetective.txt"), StringComparer.OrdinalIgnoreCase);

        // The OTHER file stays empty. Two sub-classes crossed in the data look exactly like
        // this on disk.
        Assert.DoesNotContain(player, Lines("policevice.txt"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssigningViceWritesTheNameIntoTheViceFile()
    {
        var player = await JoinedMemberAsync();

        Assert.True((await _rosters.ChangeSubclassAsync(Gwpd, player, "Vice Officer", removing: false)).IsAllowed);

        Assert.Contains(player, Lines("policevice.txt"), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(player, Lines("policedetective.txt"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASubclassDoesNotTouchTheirRankOrTheirSpawnAccess()
    {
        /* A sub-class is held ALONGSIDE a rank. If the write moved them out of their rank
           file or their spawn file, they would lose the ability to play the faction at all -
           and the command would still report success. */
        var player = await JoinedMemberAsync();

        await _rosters.ChangeSubclassAsync(Gwpd, player, "Detective", removing: false);

        Assert.Contains(player, Lines(Gwpd.RankFiles[Gwpd.Lowest]), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(player, Lines(Gwpd.SpawnFile), StringComparer.OrdinalIgnoreCase);

        var membership = await _rosters.FindAsync(player);
        Assert.Equal("GWPD", membership!.Faction.Name);
        Assert.Equal(Gwpd.Lowest, membership.Rank);
    }

    [Fact]
    public async Task OneSubclassAtATime()
    {
        var player = await JoinedMemberAsync();
        await _rosters.ChangeSubclassAsync(Gwpd, player, "Detective", removing: false);

        var second = await _rosters.ChangeSubclassAsync(Gwpd, player, "Vice Officer", removing: false);

        // Refused, and it NAMES the one they already hold - "you cannot" without saying what
        // is in the way is the reply that generates a second question.
        Assert.False(second.IsAllowed);
        Assert.Equal(MembershipOutcome.AlreadyHasSubclass, second.Outcome);
        Assert.Equal("Detective", second.Conflict);
        Assert.DoesNotContain(player, Lines("policevice.txt"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovingOneFreesThemForTheOther()
    {
        var player = await JoinedMemberAsync();
        await _rosters.ChangeSubclassAsync(Gwpd, player, "Detective", removing: false);

        Assert.True((await _rosters.ChangeSubclassAsync(Gwpd, player, "Detective", removing: true)).IsAllowed);
        Assert.DoesNotContain(player, Lines("policedetective.txt"), StringComparer.OrdinalIgnoreCase);

        Assert.True((await _rosters.ChangeSubclassAsync(Gwpd, player, "Vice Officer", removing: false)).IsAllowed);
        Assert.Contains(player, Lines("policevice.txt"), StringComparer.OrdinalIgnoreCase);

        // Still a GWPD member at their rank, after both writes.
        Assert.Contains(player, Lines(Gwpd.SpawnFile), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovingOneTheyDoNotHoldChangesNothing()
    {
        var player = await JoinedMemberAsync();

        var decision = await _rosters.ChangeSubclassAsync(Gwpd, player, "Detective", removing: true);

        Assert.Equal(MembershipOutcome.NoChange, decision.Outcome);
        Assert.Empty(Lines("policedetective.txt"));
    }

    [Fact]
    public async Task ASubclassTheFactionDoesNotDefineIsRefused()
    {
        var player = await JoinedMemberAsync();

        var decision = await _rosters.ChangeSubclassAsync(Gwpd, player, "Sniper", removing: false);

        Assert.Equal(MembershipOutcome.NoSuchSubclass, decision.Outcome);
        Assert.DoesNotContain(Directory.EnumerateFiles(_directory), f => f.Contains("sniper", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EverySubclassAppearsInThePickerLabelledWithItsFaction()
    {
        /* The choices are built from the loaded set at registration time, so this is what
           Discord is handed - the half of "it is not showing up" that lives in this repo. */
        var choices = SubclassCommand.SubclassChoices(FactionRegistry.Default);

        var gwpd = choices.Where(c => c.Faction == "GWPD").Select(c => c.Label).ToList();

        Assert.Equal(["GWPD - Detective", "GWPD - Narcotics Bureau", "GWPD - Tactical Response Unit", "GWPD - Vice Officer"], gwpd);
        Assert.Contains(choices, c => c.Value == "GWPD:Detective");
    }

    [Fact]
    public void EverySubclassFileIsCreatedAtStartup()
    {
        // EnsureRosterFiles only creates what RosterFilesOf lists, and a sub-class whose file
        // is not on that list is one nobody can be added to on a fresh install.
        var report = _rosters.EnsureRosterFiles();

        Assert.Empty(report.Failed);
        foreach (var file in Gwpd.Subclasses.Values)
            Assert.True(File.Exists(Path.Combine(_directory, file)), file);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
