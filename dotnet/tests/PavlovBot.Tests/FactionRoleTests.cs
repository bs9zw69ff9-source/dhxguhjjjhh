using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Per-faction roster delegation, for a bot whose factions came from a file.
/// </summary>
/// <remarks>
/// THE BUG THESE WERE WRITTEN FOR. ManageableFactions read two role slots named after the
/// built-in set - one for the mafias, one for the police - and added the literal strings
/// "Gambino", "Colombo" and "NYPD". A bot running a configured faction set has none of those,
/// so neither slot matched anything and the only role that could edit a roster was the
/// whitelist leader, which edits EVERY roster. The separation rule failed open, and it
/// failed open silently: /setroles accepted the role, stored it, and printed it back.
///
/// The legacy half matters just as much. A live roles.json holds mafiaRole and nypdRole, and
/// an upgrade that stopped reading them would drop the delegation on the deployment that
/// already worked - with the ids still sitting in the file.
/// </remarks>
public class FactionRoleTests
{
    private const ulong OwnerId = 2;
    private const ulong MemberId = 50;
    private const ulong NcrRoleId = 700;
    private const ulong LegionRoleId = 701;
    private const ulong LegacyMafiaRoleId = 800;
    private const ulong LegacyNypdRoleId = 801;

    private static FactionSet Fallout => FactionSet.Of(
    [
        Faction("NCR", "ncr"),
        Faction("Legion", "legion"),
    ]);

    private static FactionDefinition Faction(string name, string prefix) => new()
    {
        Name = name,
        Order = ["Recruit", "Veteran"],
        Default = "Recruit",
        SpawnFile = $"{prefix}spawn.txt",
        RankFiles = new Dictionary<string, string>
        {
            ["Recruit"] = $"{prefix}recruit.txt",
            ["Veteran"] = $"{prefix}veteran.txt",
        },
    };

    private static Access Build(FactionSet factions, RoleMap roles, out SerializedStore store)
    {
        store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        store.WriteAsync(Datasets.Roles, roles).GetAwaiter().GetResult();
        return new Access(store, [OwnerId], [], factions);
    }

    [Fact]
    public void AConfiguredFactionsRoleManagesOnlyThatFaction()
    {
        var access = Build(Fallout, new RoleMap
        {
            FactionRoles = new Dictionary<string, ulong> { ["NCR"] = NcrRoleId },
        }, out _);

        var member = new FakeMember(MemberId, NcrRoleId);

        Assert.Equal(["NCR"], access.ManageableFactions(member));
        Assert.True(access.CanManage(member, "NCR"));
        Assert.False(access.CanManage(member, "Legion"));
    }

    /// <summary>The whole point: this used to be empty for every themed bot.</summary>
    [Fact]
    public void AThemedBotHasPerFactionDelegationAtAll()
    {
        var access = Build(Fallout, new RoleMap
        {
            FactionRoles = new Dictionary<string, ulong> { ["Legion"] = LegionRoleId },
        }, out _);

        // Before the fix this member held a role /setroles had accepted and stored, and
        // ManageableFactions returned nothing at all.
        Assert.NotEmpty(access.ManageableFactions(new FakeMember(MemberId, LegionRoleId)));
    }

    [Fact]
    public void OneRoleMayManageSeveralFactions()
    {
        // What the hardcoded "one role for both mafias" rule did, now something an admin
        // chooses by naming the same role twice rather than something the code decides.
        var access = Build(Fallout, new RoleMap
        {
            FactionRoles = new Dictionary<string, ulong> { ["NCR"] = NcrRoleId, ["Legion"] = NcrRoleId },
        }, out _);

        Assert.Equal(2, access.ManageableFactions(new FakeMember(MemberId, NcrRoleId)).Count);
    }

    [Fact]
    public void AFactionNameIsMatchedCaseInsensitively()
    {
        var access = Build(Fallout, new RoleMap
        {
            FactionRoles = new Dictionary<string, ulong> { ["ncr"] = NcrRoleId },
        }, out _);

        Assert.True(access.CanManage(new FakeMember(MemberId, NcrRoleId), "NCR"));
    }

    // ---- the legacy slots a live roles.json still holds -------------------------------

    [Fact]
    public void TheOldMafiaSlotStillManagesBothMafias()
    {
        var access = Build(FactionRegistry.Default, new RoleMap { MafiaRole = LegacyMafiaRoleId }, out _);

        var member = new FakeMember(MemberId, LegacyMafiaRoleId);

        Assert.True(access.CanManage(member, "Gambino"));
        Assert.True(access.CanManage(member, "Colombo"));
        Assert.False(access.CanManage(member, "NYPD"));
    }

    [Fact]
    public void TheOldNypdSlotStillManagesTheNypd()
    {
        var access = Build(FactionRegistry.Default, new RoleMap { NypdRole = LegacyNypdRoleId }, out _);

        var member = new FakeMember(MemberId, LegacyNypdRoleId);

        Assert.True(access.CanManage(member, "NYPD"));
        Assert.False(access.CanManage(member, "Gambino"));
    }

    [Fact]
    public void AnExplicitFactionRoleBeatsTheLegacySlot()
    {
        /* Otherwise setting Gambino's role would appear to do nothing: the old shared mafia
           role would keep answering and the new setting would be stored and ignored. */
        var access = Build(FactionRegistry.Default, new RoleMap
        {
            MafiaRole = LegacyMafiaRoleId,
            FactionRoles = new Dictionary<string, ulong> { ["Gambino"] = NcrRoleId },
        }, out _);

        Assert.True(access.CanManage(new FakeMember(MemberId, NcrRoleId), "Gambino"));
        Assert.False(access.CanManage(new FakeMember(MemberId, LegacyMafiaRoleId), "Gambino"));

        // Colombo was not overridden, so it still answers to the old slot.
        Assert.True(access.CanManage(new FakeMember(MemberId, LegacyMafiaRoleId), "Colombo"));
    }

    [Fact]
    public void TheLegacySlotsGrantNothingOnAThemedBot()
    {
        // The names they answer for do not exist here, and a themed bot must not report
        // managing a faction it has never heard of.
        var access = Build(Fallout, new RoleMap { MafiaRole = LegacyMafiaRoleId, NypdRole = LegacyNypdRoleId }, out _);

        Assert.Empty(access.ManageableFactions(new FakeMember(MemberId, LegacyMafiaRoleId, LegacyNypdRoleId)));
    }

    [Fact]
    public async Task RoleMapSurvivesARoundTripThroughTheStore()
    {
        /* The store is the thing that would silently drop this: a dictionary that does not
           serialise reads back empty, which looks exactly like nobody having configured it. */
        var store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        await store.WriteAsync(Datasets.Roles, new RoleMap
        {
            ModRole = 1,
            MafiaRole = LegacyMafiaRoleId,
            FactionRoles = new Dictionary<string, ulong> { ["NCR"] = NcrRoleId },
        });

        var read = store.Read(Datasets.Roles, RoleMap.Empty);

        Assert.Equal(1ul, read.ModRole);
        Assert.Equal(LegacyMafiaRoleId, read.MafiaRole);
        Assert.Equal(NcrRoleId, read.RoleFor("NCR"));

        // Case-insensitive AFTER a round trip too - the comparer is not serialised, so a
        // plain Deserialize would come back ordinal and "ncr" would miss.
        Assert.Equal(NcrRoleId, read.RoleFor("ncr"));
    }
}

/// <summary>Faction names into Discord option names.</summary>
/// <remarks>
/// Discord rejects a bulk overwrite WHOLE for one bad option name, so a slug that produces
/// something illegal does not break one command - it empties the picker.
/// </remarks>
public class CommandOptionNameTests
{
    [Theory]
    [InlineData("NCR", "ncr_role")]
    [InlineData("Legion", "legion_role")]
    [InlineData("Brotherhood of Steel", "brotherhood_of_steel_role")]
    [InlineData("NYPD", "nypd_role")]
    public void AFactionNameBecomesALowercaseOptionName(string faction, string expected) =>
        Assert.Equal(expected, CommandOptionName.Slug(faction, "_role"));

    [Fact]
    public void PunctuationCollapsesRatherThanRepeating()
    {
        Assert.Equal("mojave_express_couriers_role",
            CommandOptionName.Slug("Mojave Express Couriers!", "_role"));
    }

    [Fact]
    public void ALongNameIsTruncatedAndKeepsItsSuffix()
    {
        var slug = CommandOptionName.Slug("The Brotherhood of Steel Mojave Chapter", "_role");

        Assert.NotNull(slug);
        Assert.True(slug!.Length <= 32, $"\"{slug}\" is {slug.Length} characters; Discord's limit is 32");
        Assert.EndsWith("_role", slug, StringComparison.Ordinal);

        // Never a double underscore where the cut landed on one.
        Assert.DoesNotContain("__", slug, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameWithNothingUsableInItYieldsNull()
    {
        // Null rather than a made-up "faction_1": a renumbered option has no visible
        // relationship to the faction it sets, and the caller can name what it dropped.
        Assert.Null(CommandOptionName.Slug("!!!", "_role"));
        Assert.Null(CommandOptionName.Slug("   ", "_role"));
        Assert.Null(CommandOptionName.Slug(null, "_role"));
    }

    [Fact]
    public void EveryProducedNameMatchesDiscordsRule()
    {
        string[] names =
        [
            "NCR", "Legion", "Brotherhood of Steel", "Enclave",
            "Mojave Express Couriers!", "Crimson Caravan Co.", "Vault 21", "Caesar's Legion",
        ];

        foreach (var slug in names.Select(n => CommandOptionName.Slug(n, "_role")))
        {
            Assert.NotNull(slug);
            Assert.Matches("^[a-z0-9_-]{1,32}$", slug!);
        }
    }
}

/// <summary>
/// The options <c>/setroles</c> actually registers.
/// </summary>
/// <remarks>
/// THE COMPLAINT THIS ANSWERS, in the operator's words: "the set roles still has police and
/// mafias not the fallout factions". The option list was three fixed tiers plus
/// <c>police_role</c>, <c>mafia_role</c> and <c>nypd_role</c>, compiled in - so a Fallout
/// bot's admin was offered three slots naming factions that bot has never heard of, and none
/// naming the four it runs.
///
/// Worth testing where DiscordGateway is not: this class builds a command shape out of
/// configuration and holds no live Discord client, so the shape can simply be asserted.
/// </remarks>
public class SetRolesShapeTests
{
    private static FactionDefinition Faction(string name, string prefix) => new()
    {
        Name = name,
        Order = ["Recruit", "Veteran"],
        Default = "Recruit",
        SpawnFile = $"{prefix}spawn.txt",
        RankFiles = new Dictionary<string, string>
        {
            ["Recruit"] = $"{prefix}recruit.txt",
            ["Veteran"] = $"{prefix}veteran.txt",
        },
    };

    private static IReadOnlyList<string> Options(FactionSet factions, params string[] disabled)
    {
        var store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        var command = new SetRolesCommand(
            store,
            new Access(store, [1ul], [2ul], factions),
            factions,
            new BotOptions
            {
                DiscordToken = "t",
                Servers = [],
                Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
                DataDirectory = ".",
                DisabledCommands = disabled,
            },
            NullLogger<SetRolesCommand>.Instance);

        var built = (SlashCommandProperties)command.Build();
        return [.. built.Options.GetValueOrDefault([]).Select(o => o.Name)];
    }

    [Fact]
    public void AThemedBotIsOfferedItsOwnFactions()
    {
        var options = Options(FactionSet.Of(
        [
            Faction("NCR", "ncr"),
            Faction("Legion", "legion"),
            Faction("Brotherhood of Steel", "bos"),
            Faction("Enclave", "enclave"),
        ]), "arrest", "warrant", "backgroundcheck", "bail");

        Assert.Equal(
            ["mod_role", "admin_role", "whitelist_leader_role",
             "brotherhood_of_steel_role", "enclave_role", "legion_role", "ncr_role"],
            options);

        // The three that prompted this, gone.
        Assert.DoesNotContain("mafia_role", options);
        Assert.DoesNotContain("nypd_role", options);
        Assert.DoesNotContain("police_role", options);
    }

    [Fact]
    public void TheBuiltInBotKeepsPoliceAndGainsAnOptionPerFaction()
    {
        var options = Options(FactionRegistry.Default);

        Assert.Contains("police_role", options);       // its police commands are enabled
        foreach (var faction in FactionRegistry.Default.Names)
        {
            var expected = CommandOptionName.Slug(faction, "_role");
            Assert.Contains(expected!, options);
        }
    }

    /// <summary>Police is a tier, not a faction, and it survives one command being off.</summary>
    [Fact]
    public void ThePoliceSlotSurvivesUntilEveryPoliceCommandIsGone()
    {
        Assert.Contains("police_role", Options(FactionRegistry.Default, "arrest"));
        Assert.DoesNotContain("police_role",
            Options(FactionRegistry.Default, "arrest", "warrant", "backgroundcheck", "bail"));
    }

    [Fact]
    public void EveryOptionNameIsUniqueAndWithinDiscordsLimits()
    {
        /* Discord rejects a bulk overwrite WHOLE for a duplicate or malformed option name,
           which would not shorten /setroles - it would empty the entire command picker. */
        var options = Options(FactionSet.Of(
        [
            Faction("NCR", "ncr"),
            Faction("New California Republic Rangers Mojave", "rangers"),
            Faction("Caesar's Legion", "legion"),
        ]));

        Assert.Equal(options.Count, options.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(options.Count <= 25, $"{options.Count} options; Discord's cap is 25");
        foreach (var name in options) Assert.Matches("^[a-z0-9_-]{1,32}$", name);
    }

    [Fact]
    public void AFactionThatCannotBeNamedIsDroppedRatherThanBreakingRegistration()
    {
        // "!!!" slugs to nothing. Registering an empty option name would take every command
        // down with it, so it is left out and reported instead.
        var options = Options(FactionSet.Of([Faction("NCR", "ncr"), Faction("!!!", "x")]));

        Assert.Contains("ncr_role", options);
        Assert.Equal(options.Count, options.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var name in options) Assert.Matches("^[a-z0-9_-]{1,32}$", name);
    }
}
