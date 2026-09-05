using PavlovBot.Core.Data;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Staff roles following a person out of the staff server.
/// </summary>
/// <remarks>
/// THE BUG: Access reads roles off the interaction's user, which is only an IGuildUser inside
/// a guild the bot serves. In a DM - and in any server the bot was carried into as a
/// user-installed app - it is a plain user with no roles, so every check below owner answered
/// false and a moderator messaging the bot privately was, to the bot, a member of the public.
/// Owners kept working because they are matched by user id, which made it look like a quirk
/// of one tier rather than the permission model being absent.
///
/// <see cref="FakeUser"/> is exactly that state, which is why these use it rather than
/// modelling a DM: the shape Access sees is the same either way.
/// </remarks>
public class HomeGuildAccessTests
{
    private const ulong ModRoleId = 100;
    private const ulong AdminRoleId = 200;
    private const ulong StrangerId = 3;

    private static Access WithRoles()
    {
        var store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        var access = new Access(store, owners: []);
        store.WriteAsync(Datasets.Roles, new RoleMap { ModRole = ModRoleId, AdminRole = AdminRoleId })
            .GetAwaiter().GetResult();
        return access;
    }

    [Fact]
    public void WithNoHomeGuildAUserWithNoMembershipHoldsNothing()
    {
        /* THE BASELINE, kept so the fix below is visibly a change rather than a test that
           would have passed either way. This is what every DM used to get. */
        Assert.False(WithRoles().IsMod(new FakeUser(StrangerId)));
    }

    [Fact]
    public void TheHomeGuildsRolesApplyWhenTheInteractionCarriesNoMember()
    {
        // THE FIX. Same person, same role, messaging the bot privately.
        var access = WithRoles();
        access.UseHomeGuild(id => new FakeMember(id, ModRoleId));

        Assert.True(access.IsMod(new FakeUser(StrangerId)));
        Assert.True(access.Allows(RequiredAccess.Mod, new FakeUser(StrangerId)));
    }

    [Fact]
    public void SomebodyWhoIsNotInTheHomeGuildStillHoldsNothing()
    {
        // The lookup answering null is a stranger, and a permission check must not fail open.
        var access = WithRoles();
        access.UseHomeGuild(_ => null);

        Assert.False(access.IsMod(new FakeUser(StrangerId)));
        Assert.False(access.Allows(RequiredAccess.Mod, new FakeUser(StrangerId)));
    }

    [Fact]
    public void OnlyTheRolesTheGuildGrantedCarryOver()
    {
        /* The widening is exactly "the access they already had", and no more: a member with
           the mod role is a mod and is still not an admin. */
        var access = WithRoles();
        access.UseHomeGuild(id => new FakeMember(id, ModRoleId));

        Assert.True(access.IsMod(new FakeUser(StrangerId)));
        Assert.False(access.IsAdmin(new FakeUser(StrangerId)));
    }

    [Fact]
    public void TheInteractionsOwnMemberIsUsedWithoutConsultingTheHomeGuild()
    {
        /* Inside a guild there is already a member, and it is the guild the person is
           actually standing in. Reaching for the home guild there would be a wasted lookup
           and, for anyone whose roles differ between the two, the wrong answer. */
        var access = WithRoles();
        access.UseHomeGuild(_ => throw new InvalidOperationException(
            "the interaction carried a member; the home guild should not have been asked"));

        Assert.True(access.IsMod(new FakeMember(StrangerId, ModRoleId)));
        Assert.False(access.IsMod(new FakeMember(StrangerId, 9999)));
    }

    [Fact]
    public void DiscordsOwnAdministratorPermissionCarriesOverToo()
    {
        /* An administrator who loses admin by messaging the bot is the same bug in a
           different clause, and that clause read the cast separately from the role check. */
        var access = WithRoles();
        access.UseHomeGuild(id => new FakeMember(id) { Administrator = true });

        Assert.True(access.IsAdmin(new FakeUser(StrangerId)));
    }

    [Fact]
    public async Task PerFactionDelegationCarriesOverAsWell()
    {
        /* Faction roles are read through the same member lookup, so a whitelist leader has
           to work in a DM too - otherwise the commands they exist for are the ones that
           still refuse them. */
        var store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        var access = new Access(store, owners: []);
        await store.WriteAsync(Datasets.Roles, new RoleMap
        {
            FactionRoles = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { ["NYPD"] = 777 },
        });

        access.UseHomeGuild(id => new FakeMember(id, 777));

        Assert.True(access.CanManage(new FakeUser(StrangerId), "NYPD"));
        Assert.True(access.Allows(RequiredAccess.FactionLeader, new FakeUser(StrangerId)));
    }

    [Fact]
    public void TheRefusalExplainsWhereRolesAreReadFromWhenThereIsNoMemberAnywhere()
    {
        // The refusal is the only place this gets explained to the person it happened to.
        var refusal = WithRoles().ExplainRefusal(new FakeUser(StrangerId), RequiredAccess.Mod);

        Assert.Contains("staff guild", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThatExplanationIsNotShownToSomebodyWhoseRolesWereRead()
    {
        /* It is a diagnosis of a specific state, and printing it under every refusal would
           tell a member who simply lacks the role to go looking at guild configuration. */
        var access = WithRoles();
        access.UseHomeGuild(id => new FakeMember(id));

        var refusal = access.ExplainRefusal(new FakeUser(StrangerId), RequiredAccess.Mod);

        Assert.DoesNotContain("staff guild", refusal, StringComparison.OrdinalIgnoreCase);
    }
}
