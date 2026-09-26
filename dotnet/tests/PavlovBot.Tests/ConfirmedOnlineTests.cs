using PavlovBot.Host.Rcon;
using PavlovBot.Rcon.Protocol;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Who may be PAID as online: only players on a server whose roster is fresh.
/// </summary>
public class ConfirmedOnlineTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static RosterSnapshot Snapshot(string server, TimeSpan age, params string[] names) =>
        new(server, [.. names.Select(n => new PavlovPlayer(n, $"id-{n}"))], Now - age);

    [Fact]
    public void AFrozenRosterFromACrashedServerIsExcluded()
    {
        var rosters = new[]
        {
            Snapshot("server1", TimeSpan.FromSeconds(20), "Alice"),
            Snapshot("server2", TimeSpan.FromMinutes(30), "Bob"),   // stopped answering half an hour ago
        };

        Assert.Equal(["Alice"], RconRegistry.ConfirmedOnline(rosters, Now));
    }

    [Fact]
    public void ANeverRefreshedRosterIsExcluded()
    {
        Assert.Empty(RconRegistry.ConfirmedOnline([RosterSnapshot.Empty("server1")], Now));
    }

    [Fact]
    public void NamesAreDeduplicatedAcrossServersAndBlanksDropped()
    {
        var rosters = new[]
        {
            Snapshot("server1", TimeSpan.Zero, "Alice", ""),
            Snapshot("server2", TimeSpan.Zero, "alice"),
        };

        Assert.Single(RconRegistry.ConfirmedOnline(rosters, Now));
    }
}
