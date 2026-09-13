using PavlovBot.Core.Data;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Storage;
using PavlovBot.Core.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Who gets past the front door of the roster commands.
/// </summary>
/// <remarks>
/// THE HOLE THIS CLOSES. /subclass, /promotion, /demotion and /whitelist remove all checked
/// CanManage - the right check - but only AFTER resolving the member, and resolving them
/// discloses their in-game name. Worse, a member whose roster entry had gone stale was
/// FORGOTTEN on the way past: a write, performed for anybody who could type the command,
/// before any permission check ran at all.
///
/// The gate is RequiredAccess.FactionLeader, which means "manages at least one faction" -
/// so a faction's own role still passes and is then refused BY NAME if the member is not
/// theirs. Somebody who manages nothing is refused before anything is read or written,
/// because no outcome was ever possible for them.
///
/// These pin the access semantics the gate rests on. The handlers cannot be invoked in a
/// test - SocketSlashCommand belongs to Discord.Net's gateway and cannot be constructed -
/// so the ordering itself is enforced by reading the code, which is why the gate sits at the
/// very top of each handler rather than somewhere defensible-looking further down.
/// </remarks>
public class RosterCommandGateTests
{
    private const ulong NcrRole = 900;
    private const ulong LeaderRole = 901;
    private const ulong Stranger = 5;

    private static Access WithFactionRoles(out SerializedStore store)
    {
        store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        var access = new Access(store, owners: [], factions: FactionRegistry.Police);
        store.WriteAsync(Datasets.Roles, new RoleMap
        {
            FactionLeaderRole = LeaderRole,
            FactionRoles = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { ["NYPD"] = NcrRole },
        }).GetAwaiter().GetResult();
        return access;
    }

    [Fact]
    public void SomebodyWhoManagesNothingIsRefusedAtTheGate()
    {
        /* THE ONE THAT MATTERS. Before this gate they reached the member lookup, saw an
           in-game name, and could clear a stale index entry. */
        var access = WithFactionRoles(out _);

        Assert.False(access.Allows(RequiredAccess.FactionLeader, new FakeMember(Stranger)));
        Assert.False(access.Allows(RequiredAccess.FactionLeader, new FakeUser(Stranger)));
    }

    [Fact]
    public void AFactionsOwnRoleGetsPastTheGate()
    {
        /* It must, or the gate would lock out exactly the people it is meant to admit - the
           per-faction check further down is what tells them the member is not theirs. */
        var access = WithFactionRoles(out _);

        Assert.True(access.Allows(RequiredAccess.FactionLeader, new FakeMember(Stranger, NcrRole)));
    }

    [Fact]
    public void TheGateIsNotTheWholeCheck()
    {
        /* Passing the gate is not permission to touch any roster. A role that manages one
           faction manages only that one, which is what CanManage answers. */
        var access = WithFactionRoles(out _);
        var holder = new FakeMember(Stranger, NcrRole);

        Assert.True(access.CanManage(holder, "NYPD"));
        Assert.False(access.CanManage(holder, "Gambino"));
    }

    [Fact]
    public void TheOverallLeaderRoleManagesEveryFaction()
    {
        var access = WithFactionRoles(out _);
        var leader = new FakeMember(Stranger, LeaderRole);

        Assert.True(access.Allows(RequiredAccess.FactionLeader, leader));
        Assert.True(access.CanManage(leader, "NYPD"));
        Assert.True(access.CanManage(leader, "Gambino"));
    }

    [Fact]
    public void AnOwnerPassesWithoutAnyRoleAtAll()
    {
        /* Owners are matched by user id, so the gate must not lock out the one account that
           can fix a broken role configuration. */
        var store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        var access = new Access(store, owners: [Stranger], factions: FactionRegistry.Police);

        Assert.True(access.Allows(RequiredAccess.FactionLeader, new FakeUser(Stranger)));
    }
}
