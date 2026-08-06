using Discord;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Storage;
using Xunit;

using PavlovBot.Core.Factions;

namespace PavlovBot.Tests;

/// <summary>
/// Who may do what. The most security-relevant class in the bot, and it had no tests.
/// </summary>
/// <remarks>
/// The bug these were written for: every predicate took <c>IGuildUser</c>, so each caller
/// wrote <c>command.User as IGuildUser</c>. A cast that fails yields NULL, and null read as
/// "holds nothing" - so an OWNER was refused a Mod command while the Owner-tier commands
/// kept working, because those alone compared the user id. That is not a subtle
/// misconfiguration from the outside; it is the top of the ladder failing while the rung
/// below it works.
///
/// The fakes here are deliberately minimal. What is being tested is the RESOLUTION ORDER -
/// which is pure logic over an id set and a role list - not Discord's object model.
/// </remarks>
public class AccessTests
{
    private const ulong SuperOwnerId = 1;
    private const ulong OwnerId = 2;
    private const ulong StrangerId = 3;

    private const ulong ModRoleId = 100;
    private const ulong AdminRoleId = 200;

    private static Access Build(out SerializedStore store)
    {
        store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        return new Access(store, [OwnerId], [SuperOwnerId]);
    }

    private static Access WithRoles(out SerializedStore store)
    {
        var access = Build(out store);
        store.WriteAsync(Datasets.Roles, new RoleMap { ModRole = ModRoleId, AdminRole = AdminRoleId })
            .GetAwaiter().GetResult();
        return access;
    }

    // ---- owners pass every gate, from the id alone ----

    [Theory]
    [InlineData(RequiredAccess.Public)]
    [InlineData(RequiredAccess.Police)]
    [InlineData(RequiredAccess.FactionLeader)]
    [InlineData(RequiredAccess.Mod)]
    [InlineData(RequiredAccess.Admin)]
    [InlineData(RequiredAccess.Owner)]
    public void AnOwnerPassesEveryGate(RequiredAccess required)
    {
        var access = Build(out _);

        Assert.True(access.Passes(required, new FakeMember(OwnerId)));
        Assert.True(access.Passes(required, new FakeMember(SuperOwnerId)));
    }

    [Theory]
    [InlineData(RequiredAccess.Police)]
    [InlineData(RequiredAccess.FactionLeader)]
    [InlineData(RequiredAccess.Mod)]
    [InlineData(RequiredAccess.Admin)]
    [InlineData(RequiredAccess.Owner)]
    public void AnOwnerPassesEveryGateEvenWithNoGuildMember(RequiredAccess required)
    {
        /* THE ACTUAL BUG. A plain IUser is what the interaction carries when the member
           could not be resolved - a DM, an uncached guild, a user-installed app context.
           Every role lookup below correctly answers false; ownership must not, because it
           is a set of ids and needs nothing from the guild. */
        var access = Build(out _);

        Assert.True(access.Passes(required, new FakeUser(OwnerId)));
        Assert.True(access.Passes(required, new FakeUser(SuperOwnerId)));
    }

    [Fact]
    public void AStrangerWithNoGuildMemberPassesOnlyPublic()
    {
        // The permissive branch must be exactly the owner one, not "anything unresolved".
        var access = WithRoles(out _);
        var stranger = new FakeUser(StrangerId);

        Assert.True(access.Passes(RequiredAccess.Public, stranger));
        Assert.False(access.Passes(RequiredAccess.Mod, stranger));
        Assert.False(access.Passes(RequiredAccess.Admin, stranger));
        Assert.False(access.Passes(RequiredAccess.Owner, stranger));
        Assert.False(access.Passes(RequiredAccess.Police, stranger));
        Assert.False(access.Passes(RequiredAccess.FactionLeader, stranger));
    }

    // ---- the ladder, for everybody else ----

    [Fact]
    public void AnAdminIsAModButNotAnOwner()
    {
        var access = WithRoles(out _);
        var admin = new FakeMember(StrangerId, AdminRoleId);

        Assert.True(access.IsMod(admin));
        Assert.True(access.IsAdmin(admin));
        Assert.False(access.IsOwner(admin));
        Assert.Equal(StaffTier.Admin, access.TierOf(admin));
    }

    [Fact]
    public void AModIsNotAnAdmin()
    {
        var access = WithRoles(out _);
        var mod = new FakeMember(StrangerId, ModRoleId);

        Assert.True(access.IsMod(mod));
        Assert.False(access.IsAdmin(mod));
        Assert.Equal(StaffTier.Mod, access.TierOf(mod));
    }

    [Fact]
    public void DiscordsOwnAdministratorPermissionCounts()
    {
        // Somebody who can delete the guild is not meaningfully restricted by a bot role.
        var access = WithRoles(out _);

        Assert.True(access.IsAdmin(new FakeMember(StrangerId) { Administrator = true }));
    }

    [Fact]
    public void ASuperOwnerOutranksAnOwner()
    {
        var access = Build(out _);

        Assert.Equal(StaffTier.SuperOwner, access.TierOf(new FakeMember(SuperOwnerId)));
        Assert.Equal(StaffTier.Owner, access.TierOf(new FakeMember(OwnerId)));
        Assert.True(access.IsOwner(new FakeMember(SuperOwnerId)));       // a super owner IS an owner
        Assert.False(access.IsSuperOwner(new FakeMember(OwnerId)));
    }

    [Fact]
    public void AnOwnerManagesEveryFactionRoster()
    {
        var access = Build(out _);

        // One faction now: the Gambino and Colombo ladders were removed. Asserted against
        // the registry rather than a literal, so adding a faction does not fail this.
        Assert.Equal(FactionRegistry.All.Count, access.ManageableFactions(new FakeMember(OwnerId)).Count);
        Assert.True(access.CanManage(new FakeUser(SuperOwnerId), "NYPD"));
        Assert.Empty(access.ManageableFactions(new FakeMember(StrangerId)));
    }

    [Fact]
    public void AnOwnerIsDescribedAsOne()
    {
        var access = Build(out _);

        Assert.Equal("SUPER OWNER", access.DescribeAccess(new FakeUser(SuperOwnerId)));
        Assert.Equal("OWNER", access.DescribeAccess(new FakeUser(OwnerId)));
        Assert.Equal("PUBLIC", access.DescribeAccess(new FakeUser(StrangerId)));
    }

    // ---- the refusal has to be actionable ----

    [Fact]
    public void ARefusalSaysWhatYouHold_NotJustWhatYouNeed()
    {
        /* "You need Mod access" cannot distinguish an unconfigured MOD_ROLE from a role the
           person simply lacks, and those are different fixes. Days went into that gap. */
        var access = Build(out _);

        var refusal = access.ExplainRefusal(new FakeMember(StrangerId), RequiredAccess.Mod);

        Assert.Contains("Mod", refusal, StringComparison.Ordinal);
        Assert.Contains("PUBLIC", refusal, StringComparison.Ordinal);
        Assert.Contains("/setroles", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalNamesTheUnresolvedMemberCase()
    {
        // Otherwise it is indistinguishable from lacking the role, and it is not the same.
        var access = WithRoles(out _);

        var refusal = access.ExplainRefusal(new FakeUser(StrangerId), RequiredAccess.Mod);

        Assert.Contains("could not read your server roles", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalSaysWhenNoOwnersExistAtAll()
    {
        var access = new Access(new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec()), []);

        var refusal = access.ExplainRefusal(new FakeMember(StrangerId), RequiredAccess.Owner);

        Assert.Contains("OWNER_IDS", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalNeverNamesWhoDoesHoldTheTier()
    {
        // A refusal that lists the holders is a map of who to social-engineer.
        var access = Build(out _);

        var refusal = access.ExplainRefusal(new FakeMember(StrangerId), RequiredAccess.Owner);

        Assert.DoesNotContain(OwnerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            refusal, StringComparison.Ordinal);
        Assert.DoesNotContain(SuperOwnerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            refusal, StringComparison.Ordinal);
    }
}

/// <summary>
/// The tier resolution behind <c>AccessChecks.Allows</c>, without a SocketInteraction.
/// </summary>
/// <remarks>
/// <c>Allows</c> takes a <c>SocketInteraction</c>, which is sealed to Discord.Net's gateway
/// and cannot be constructed in a test. This mirrors its body exactly - including the owner
/// short-circuit that is the whole point - so the ORDER of the checks is covered even though
/// the extension method itself is not reachable from here.
/// </remarks>
internal static class AccessProbe
{
    public static bool Passes(this Access access, RequiredAccess required, IUser user)
    {
        if (access.IsOwner(user)) return true;

        return required switch
        {
            RequiredAccess.Public => true,
            RequiredAccess.Police => access.IsPolice(user),
            RequiredAccess.FactionLeader => access.IsFactionLeader(user) || access.ManageableFactions(user).Count > 0,
            RequiredAccess.Mod => access.IsMod(user),
            RequiredAccess.Admin => access.IsAdmin(user),
            RequiredAccess.Owner => access.IsOwner(user),
            _ => false,
        };
    }
}
