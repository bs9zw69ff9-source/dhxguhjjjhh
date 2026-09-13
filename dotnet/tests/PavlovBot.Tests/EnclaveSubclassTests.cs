using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// <c>/subclass</c> end to end for the Enclave, on a bot configured with nothing at all.
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
public class EnclaveSubclassTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pavlovbot-enclave-" + Guid.NewGuid().ToString("N"));

    private readonly RosterService _rosters;

    /// <summary>The set a bot gets with nothing configured. That is the point of the test.</summary>
    private static readonly FactionDefinition Enclave = FactionRegistry.Default.Get("Enclave")!;

    public EnclaveSubclassTests()
    {
        Directory.CreateDirectory(_directory);
        _rosters = new RosterService(_directory, NullLogger<RosterService>.Instance,
            Path.Combine(_directory, "_bak"));
    }

    private IReadOnlyList<string> Lines(string file) => _rosters.Read(file) ?? [];

    private async Task<string> JoinedMemberAsync(string name = "Courier6")
    {
        var join = await _rosters.JoinAsync(Enclave, name);
        Assert.True(join.IsAllowed);
        return name;
    }

    [Fact]
    public void BothSubclassesExistOnTheDefaultSetWithTheirOwnFiles()
    {
        Assert.Equal("enclavehellfire.txt", Enclave.Subclasses["Hellfire"]);
        Assert.Equal("enclavedemolition.txt", Enclave.Subclasses["Demolition"]);

        // Neither collides with a rank file, which would hand out the wrong loadout.
        Assert.DoesNotContain(Enclave.Subclasses["Hellfire"], Enclave.RankFiles.Values, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Enclave.Subclasses["Demolition"], Enclave.RankFiles.Values, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssigningHellfireWritesTheNameIntoTheHellfireFile()
    {
        var player = await JoinedMemberAsync();

        var decision = await _rosters.ChangeSubclassAsync(Enclave, player, "Hellfire", removing: false);

        Assert.True(decision.IsAllowed);
        Assert.Contains(player, Lines("enclavehellfire.txt"), StringComparer.OrdinalIgnoreCase);

        // The OTHER file stays empty. These two were briefly crossed in the data, and that
        // is exactly what it looked like on disk.
        Assert.DoesNotContain(player, Lines("enclavedemolition.txt"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssigningDemolitionWritesTheNameIntoTheDemolitionFile()
    {
        var player = await JoinedMemberAsync();

        Assert.True((await _rosters.ChangeSubclassAsync(Enclave, player, "Demolition", removing: false)).IsAllowed);

        Assert.Contains(player, Lines("enclavedemolition.txt"), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(player, Lines("enclavehellfire.txt"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASubclassDoesNotTouchTheirRankOrTheirSpawnAccess()
    {
        /* A sub-class is held ALONGSIDE a rank. If the write moved them out of their rank
           file or their spawn file, they would lose the ability to play the faction at all -
           and the command would still report success. */
        var player = await JoinedMemberAsync();

        await _rosters.ChangeSubclassAsync(Enclave, player, "Hellfire", removing: false);

        Assert.Contains(player, Lines(Enclave.RankFiles[Enclave.Lowest]), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(player, Lines(Enclave.SpawnFile), StringComparer.OrdinalIgnoreCase);

        var membership = await _rosters.FindAsync(player);
        Assert.Equal("Enclave", membership!.Faction.Name);
        Assert.Equal(Enclave.Lowest, membership.Rank);
    }

    [Fact]
    public async Task OneSubclassAtATime()
    {
        var player = await JoinedMemberAsync();
        await _rosters.ChangeSubclassAsync(Enclave, player, "Hellfire", removing: false);

        var second = await _rosters.ChangeSubclassAsync(Enclave, player, "Demolition", removing: false);

        // Refused, and it NAMES the one they already hold - "you cannot" without saying what
        // is in the way is the reply that generates a second question.
        Assert.False(second.IsAllowed);
        Assert.Equal(MembershipOutcome.AlreadyHasSubclass, second.Outcome);
        Assert.Equal("Hellfire", second.Conflict);
        Assert.DoesNotContain(player, Lines("enclavedemolition.txt"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovingOneFreesThemForTheOther()
    {
        var player = await JoinedMemberAsync();
        await _rosters.ChangeSubclassAsync(Enclave, player, "Hellfire", removing: false);

        Assert.True((await _rosters.ChangeSubclassAsync(Enclave, player, "Hellfire", removing: true)).IsAllowed);
        Assert.DoesNotContain(player, Lines("enclavehellfire.txt"), StringComparer.OrdinalIgnoreCase);

        Assert.True((await _rosters.ChangeSubclassAsync(Enclave, player, "Demolition", removing: false)).IsAllowed);
        Assert.Contains(player, Lines("enclavedemolition.txt"), StringComparer.OrdinalIgnoreCase);

        // Still an Enclave member at their rank, after both writes.
        Assert.Contains(player, Lines(Enclave.SpawnFile), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovingOneTheyDoNotHoldChangesNothing()
    {
        var player = await JoinedMemberAsync();

        var decision = await _rosters.ChangeSubclassAsync(Enclave, player, "Hellfire", removing: true);

        Assert.Equal(MembershipOutcome.NoChange, decision.Outcome);
        Assert.Empty(Lines("enclavehellfire.txt"));
    }

    [Fact]
    public async Task ASubclassTheFactionDoesNotDefineIsRefused()
    {
        var player = await JoinedMemberAsync();

        var decision = await _rosters.ChangeSubclassAsync(Enclave, player, "Veteran Ranger", removing: false);

        // NCR's, not the Enclave's. Writing it would put an Enclave member in an NCR file.
        Assert.Equal(MembershipOutcome.NoSuchSubclass, decision.Outcome);
        Assert.Empty(Lines("ncrranger.txt"));
    }

    [Fact]
    public void BothAppearInThePickerLabelledWithTheirFaction()
    {
        /* The choices are built from the loaded set at registration time, so this is what
           Discord is handed - the half of "it is not showing up" that lives in this repo. */
        var choices = SubclassCommand.SubclassChoices(FactionRegistry.Default);

        var hellfire = Assert.Single(choices, c => c.Name == "Hellfire");
        var demolition = Assert.Single(choices, c => c.Name == "Demolition");

        Assert.Equal(["Enclave"], hellfire.Owners);
        Assert.Equal(["Enclave"], demolition.Owners);
    }

    [Fact]
    public void EveryEnclaveFileIsCreatedAtStartup()
    {
        // EnsureRosterFiles only creates what RosterFilesOf lists, and a sub-class whose file
        // is not on that list is one nobody can be added to on a fresh install.
        var report = _rosters.EnsureRosterFiles();

        Assert.Empty(report.Failed);
        Assert.True(File.Exists(Path.Combine(_directory, "enclavehellfire.txt")));
        Assert.True(File.Exists(Path.Combine(_directory, "enclavedemolition.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
