using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>NYPD is the GWPD: the name moved, and nothing filed under the old one is lost.</summary>
public class FactionRenameTests
{
    private static SerializedStore Store() => new(new MemoryBackend(), new SystemTextJsonCodec());

    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public void ThePoliceAreTheGwpd()
    {
        Assert.Equal(["GWPD"], FactionRegistry.Police.Names);
        Assert.Empty(FactionRegistry.Police.Problems());
    }

    [Fact]
    public void OnlyTheNameMovedNotTheFiles()
    {
        /* The roster files are what the game reads. Repointing one takes the rank away, in
           game, from everybody already listed in it. */
        var gwpd = FactionRegistry.Police.Get("GWPD")!;

        Assert.Equal("policespawn.txt", gwpd.SpawnFile);
        Assert.Equal("policecadet.txt", gwpd.RankFiles["Cadet"]);
        Assert.Equal("policechief.txt", gwpd.RankFiles["Chief of Police"]);
        Assert.Equal("policedetective.txt", gwpd.Subclasses["Detective"]);
    }

    [Theory]
    [InlineData("NYPD")]
    [InlineData("nypd")]
    [InlineData("GWPD")]
    public void TheOldNameStillFindsTheFaction(string name)
    {
        Assert.Equal("GWPD", FactionRegistry.Police.Get(name)!.Name);
        Assert.Equal("GWPD", FactionRegistry.Get(name)!.Name);
    }

    [Fact]
    public void TheOldNameIsNeverListed() => Assert.DoesNotContain("NYPD", FactionRegistry.Police.Names);

    [Theory]
    [InlineData("police")]
    [InlineData("gwpd")]
    [InlineData("nypd")]
    public void EveryPoliceSpellingPicksThePoliceSet(string preset) =>
        Assert.Same(FactionRegistry.Police, FactionRegistry.Preset(preset));

    [Fact]
    public void AFormerNameThatIsAFactionOfItsOwnIsAProblem()
    {
        var set = FactionSet.Of([
            Faction("A", former: ["B"]),
            Faction("B"),
        ]);

        Assert.Contains(set.Problems(), p => p.Contains("former name", StringComparison.Ordinal));
        Assert.Equal("B", set.Get("B")!.Name);   // and the current name still wins the lookup
    }

    [Fact]
    public void AFormerNameClaimedTwiceIsAProblem()
    {
        var set = FactionSet.Of([
            Faction("A", former: ["Old"]),
            Faction("B", former: ["Old"]),
        ]);

        Assert.Contains(set.Problems(), p => p.Contains("both list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordsUnderTheOldNameAreRefiled()
    {
        var store = Store();
        await store.WriteAsync(Datasets.FactionMembers, new Dictionary<string, FactionMember>
        {
            ["1"] = new("NYPD", "Officer", At, "mod"),
            ["2"] = new("Gambino", "Tony", At, "mod"),
        });
        await store.WriteAsync(Datasets.RankSuspensions, new Dictionary<string, RankSuspension>
        {
            ["officer"] = new("Officer", "NYPD", "Sergeant", "admin", At),
        });
        await store.WriteAsync(Datasets.Roles, RoleMap.Empty with
        {
            FactionRoles = new Dictionary<string, ulong> { ["NYPD"] = 55 },
        });
        var payroll = PayrollState.New();
        payroll.LastPaid["NYPD"] = At;
        payroll.Previous["NYPD"] = ["Officer"];
        await store.WriteAsync(Datasets.PayrollState, payroll);

        var changed = await FactionRenames.CarryOverAsync(store, FactionRegistry.Police, NullLogger.Instance);

        Assert.Equal(4, changed);

        var members = new FactionMembers(store);
        Assert.Equal("GWPD", members.Of(1)!.Faction);
        Assert.Equal("Gambino", members.Of(2)!.Faction);
        Assert.Single(members.InFaction("GWPD"));

        var suspensions = store.Read(Datasets.RankSuspensions, new Dictionary<string, RankSuspension>());
        Assert.Equal("GWPD", suspensions["officer"].Faction);

        var roles = store.Read(Datasets.Roles, RoleMap.Empty);
        Assert.Equal(55UL, roles.RoleFor("GWPD"));
        Assert.False(roles.FactionRoles.ContainsKey("NYPD"));

        var state = store.Read(Datasets.PayrollState, PayrollState.New());
        Assert.Equal(At, state.LastPaid["GWPD"]);
        Assert.Equal(["Officer"], state.Previous["GWPD"]);
        Assert.False(state.LastPaid.ContainsKey("NYPD"));
    }

    [Fact]
    public async Task TheSecondRunChangesNothing()
    {
        var store = Store();
        await store.WriteAsync(Datasets.FactionMembers, new Dictionary<string, FactionMember>
        {
            ["1"] = new("NYPD", "Officer", At, "mod"),
        });

        Assert.Equal(1, await FactionRenames.CarryOverAsync(store, FactionRegistry.Police, NullLogger.Instance));
        Assert.Equal(0, await FactionRenames.CarryOverAsync(store, FactionRegistry.Police, NullLogger.Instance));
    }

    [Fact]
    public async Task ABotWithNoRenamesWritesNothing()
    {
        // The Fallout bot: no faction there was ever renamed, so no dataset is touched.
        var store = Store();
        await store.WriteAsync(Datasets.FactionMembers, new Dictionary<string, FactionMember>
        {
            ["1"] = new("NYPD", "Officer", At, "mod"),
        });
        var before = store.ReadRaw(Datasets.FactionMembers);

        Assert.Equal(0, await FactionRenames.CarryOverAsync(store, FactionRegistry.FalloutSet, NullLogger.Instance));
        Assert.Equal(before, store.ReadRaw(Datasets.FactionMembers));
        Assert.Null(store.ReadRaw(Datasets.Roles));
    }

    [Fact]
    public void ARoleAlreadySetUnderTheNewNameIsKept()
    {
        // Set after the rename, so set deliberately - it beats the one inherited from NYPD.
        var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { ["NYPD"] = 1, ["GWPD"] = 2 };

        FactionRenames.RenameKeys(map, FactionRegistry.Police.Renames);

        Assert.Equal(2UL, map["GWPD"]);
        Assert.False(map.ContainsKey("NYPD"));
    }

    private const string SpawnOnly = "Member";

    private static FactionDefinition Faction(string name, IReadOnlyList<string>? former = null) => new()
    {
        Name = name,
        Order = [SpawnOnly],
        Default = SpawnOnly,
        SpawnFile = $"{name.ToLowerInvariant()}spawn.txt",
        RankFiles = new Dictionary<string, string> { [SpawnOnly] = $"{name.ToLowerInvariant()}spawn.txt" },
        FormerNames = former ?? [],
    };
}
