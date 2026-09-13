using System.Text.Json;
using PavlovBot.Core.Data;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Reading the roles dataset the Node bot wrote.
/// </summary>
/// <remarks>
/// THE FILE ON A LIVE SERVER IS STILL THAT FILE. Startup imports roles.json into the store
/// verbatim, and the Node bot wrote suffixed keys with STRING ids - discord.js hands out
/// snowflakes as text, because they do not survive a double. Against RoleMap's unsuffixed
/// names every one of those keys was an unknown property, so the whole mapping read as null:
/// /setroles reported roles that were set, and every role-gated command refused everybody.
/// Owners kept working throughout, being matched by user id, which made it look like one
/// tier misbehaving rather than the mapping being absent.
///
/// The exact literal below is the Node bot's own seed, from database/index.js at the commit
/// before it was removed, with ids filled in the way it filled them.
/// </remarks>
public class RoleMapLegacyShapeTests
{
    private const ulong Mod = 1400000000000000001;
    private const ulong Admin = 1400000000000000002;
    private const ulong Leader = 1400000000000000003;
    private const ulong Police = 1400000000000000004;
    private const ulong Gambino = 1400000000000000005;
    private const ulong Colombo = 1400000000000000006;
    private const ulong Nypd = 1400000000000000007;

    private static readonly string NodeFile = $$"""
        {
          "modRoleId": "{{Mod}}",
          "adminRoleId": "{{Admin}}",
          "factionLeaderRoleId": "{{Leader}}",
          "policeRoleId": "{{Police}}",
          "gambinoRoleId": "{{Gambino}}",
          "colomboRoleId": "{{Colombo}}",
          "nypdRoleId": "{{Nypd}}"
        }
        """;

    private static SerializedStore Store(string? seeded = null)
    {
        var backend = new MemoryBackend();
        if (seeded is not null) backend.Write(Datasets.Roles, seeded);
        return new SerializedStore(backend, new SystemTextJsonCodec());
    }

    [Fact]
    public void EveryTierTheNodeBotStoredIsRead()
    {
        var read = Store(NodeFile).Read(Datasets.Roles, RoleMap.Empty);

        Assert.Equal(Mod, read.ModRole);
        Assert.Equal(Admin, read.AdminRole);
        Assert.Equal(Leader, read.FactionLeaderRole);
        Assert.Equal(Police, read.PoliceRole);
        Assert.Equal(Nypd, read.NypdRole);
    }

    /// <summary>
    /// Node held the two mafia rosters as separate roles, and they must stay separate.
    /// </summary>
    /// <remarks>
    /// Folding them into MafiaRole would hand each family's leader the other family's
    /// whitelist, which is the one thing the per-faction split exists to prevent.
    /// </remarks>
    [Fact]
    public void TheTwoMafiaRolesArriveAsSeparateFactionEntries()
    {
        var read = Store(NodeFile).Read(Datasets.Roles, RoleMap.Empty);

        Assert.Equal(Gambino, read.RoleFor("Gambino"));
        Assert.Equal(Colombo, read.RoleFor("Colombo"));
        Assert.NotEqual(read.RoleFor("Gambino"), read.RoleFor("Colombo"));
    }

    /// <summary>The old seed's "unset" was an empty string, not a missing key.</summary>
    [Fact]
    public void AnEmptyStringIsNoRoleRatherThanAnUnreadableDataset()
    {
        const string unset = """{"modRoleId":"","adminRoleId":"","policeRoleId":""}""";

        var read = Store(unset).Read(Datasets.Roles, RoleMap.Empty);

        Assert.Null(read.ModRole);
        Assert.Null(read.AdminRole);
        Assert.Null(read.PoliceRole);
    }

    /// <summary>A hand-edited file with a numeric id reads the same as the quoted one.</summary>
    [Fact]
    public void ANumericLegacyIdIsAcceptedToo()
    {
        var read = Store($$"""{"modRoleId":{{Mod}}}""").Read(Datasets.Roles, RoleMap.Empty);

        Assert.Equal(Mod, read.ModRole);
    }

    /// <summary>Garbage in one key must not cost the whole mapping.</summary>
    [Fact]
    public void AnUnparseableIdIsNoRoleAndLeavesTheRestIntact()
    {
        var read = Store($$"""{"modRoleId":"not-a-snowflake","adminRoleId":"{{Admin}}"}""")
            .Read(Datasets.Roles, RoleMap.Empty);

        Assert.Null(read.ModRole);
        Assert.Equal(Admin, read.AdminRole);
    }

    /// <summary>The name this bot writes wins over the one it only reads.</summary>
    [Fact]
    public void TheCanonicalKeyBeatsTheLegacyAlias()
    {
        var read = Store($$"""{"modRole":{{Mod}},"modRoleId":"{{Admin}}"}""")
            .Read(Datasets.Roles, RoleMap.Empty);

        Assert.Equal(Mod, read.ModRole);
    }

    /// <summary>
    /// The aliases are read-only: a write must not put the old names back.
    /// </summary>
    /// <remarks>
    /// Emitting both shapes would mean the two could disagree, and then which one wins is a
    /// question about deserialiser ordering rather than about what an admin set.
    /// </remarks>
    [Fact]
    public async Task WritingNormalisesTheDatasetToTheUnsuffixedNames()
    {
        var store = Store(NodeFile);

        await store.UpdateAsync(Datasets.Roles, RoleMap.Empty, current => current with { ModRole = Mod });

        var raw = store.ReadRaw(Datasets.Roles)!;
        using var document = JsonDocument.Parse(raw);
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.DoesNotContain("modRoleId", keys, StringComparer.Ordinal);
        Assert.DoesNotContain("gambinoRoleId", keys, StringComparer.Ordinal);
        Assert.Contains("modRole", keys, StringComparer.Ordinal);

        // And the tiers the old file carried are still there afterwards, in the new shape.
        var read = store.Read(Datasets.Roles, RoleMap.Empty);
        Assert.Equal(Admin, read.AdminRole);
        Assert.Equal(Gambino, read.RoleFor("Gambino"));
    }

    /// <summary>The shipped seed must be a shape RoleMap can actually read.</summary>
    [Fact]
    public void TheSeedRoundTripsThroughTheTypeItSeeds()
    {
        var read = Store(Datasets.Seeds[Datasets.Roles]).Read(Datasets.Roles, RoleMap.Empty);

        Assert.Null(read.ModRole);
        Assert.NotNull(read.FactionRoles);

        // A fresh install's first /setroles has to stick, which is the whole point of the
        // seed being a shape this type understands rather than one it discards.
        var set = read with { ModRole = Mod };
        Assert.Equal(Mod, set.ModRole);
    }

    /// <summary>
    /// A null faction map must not arrive as null, whatever the annotation says.
    /// </summary>
    /// <remarks>
    /// System.Text.Json ignores nullable reference annotations, and `record with` bypasses
    /// property initializers - so the guard has to live in the setter or RoleFor throws on a
    /// hand-edited file and takes every permission check with it.
    /// </remarks>
    [Fact]
    public void ANullFactionMapReadsAsEmptyRatherThanThrowing()
    {
        var read = Store("""{"factionRoles":null}""").Read(Datasets.Roles, RoleMap.Empty);

        Assert.NotNull(read.FactionRoles);
        Assert.Null(read.RoleFor("Gambino"));
    }

    /// <summary>
    /// Faction lookups stay case-insensitive across a restart.
    /// </summary>
    /// <remarks>
    /// A comparer is not serialised, so a map built OrdinalIgnoreCase comes back ordinal.
    /// That fails only after a process cycle, which is the worst shape a permissions bug has.
    /// </remarks>
    [Fact]
    public async Task FactionRolesStayCaseInsensitiveAfterAReadBack()
    {
        var store = Store();

        await store.UpdateAsync(Datasets.Roles, RoleMap.Empty, _ => new RoleMap
        {
            FactionRoles = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
            {
                ["Brotherhood of Steel"] = Gambino,
            },
        });

        var read = store.Read(Datasets.Roles, RoleMap.Empty);

        Assert.Equal(Gambino, read.RoleFor("brotherhood of steel"));
        Assert.True(read.FactionRoles.ContainsKey("BROTHERHOOD OF STEEL"));
    }
}
