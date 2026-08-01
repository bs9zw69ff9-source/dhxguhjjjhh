using PavlovBot.Host.Logs;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Leave reporting, which had no test at all because it needed a live RCON connection.
/// </summary>
/// <remarks>
/// Joins come from the LOG and leaves come from the ROSTER, so the two fail independently:
/// joins can work perfectly while no leave is ever reported, and nothing in the output
/// distinguishes "this logic is wrong" from "the roster is empty".
/// </remarks>
public class LeaveTrackingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, DateTimeOffset> Online(params (string Player, int MinutesAgo)[] players) =>
        players.ToDictionary(p => p.Player, p => Now.AddMinutes(-p.MinutesAgo), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SomebodyMissingFromTheRosterHasLeft()
    {
        var leavers = FeedBridge.Leavers(Online(("Alice", 10), ("Bob", 5)), ["Alice"], primed: true, Now);

        var left = Assert.Single(leavers);
        Assert.Equal("Bob", left.Player);
        Assert.Equal(TimeSpan.FromMinutes(5), left.Session);
    }

    [Fact]
    public void TheFirstSweepReportsNobody()
    {
        /* Everyone already online would otherwise be announced as leaving the moment the
           second sweep ran - a wall of noise that says nothing true. */
        Assert.Empty(FeedBridge.Leavers(Online(("Alice", 10)), [], primed: false, Now));
    }

    [Fact]
    public void AnEmptyRosterMeansEverybodyLeft()
    {
        // The last player leaving is the case that matters most, and the one an
        // "ignore empty rosters" guard would swallow.
        var leavers = FeedBridge.Leavers(Online(("Alice", 10), ("Bob", 5)), [], primed: true, Now);

        Assert.Equal(2, leavers.Count);
    }

    [Fact]
    public void NobodyLeavesWhenTheRosterIsUnchanged()
    {
        Assert.Empty(FeedBridge.Leavers(Online(("Alice", 10)), ["Alice"], primed: true, Now));
    }

    [Fact]
    public void CasingDoesNotInventALeave()
    {
        /* Pavlov display names are not case-stable between replies. A case-sensitive
           comparison would report a leave and a join every single sweep. */
        Assert.Empty(FeedBridge.Leavers(Online(("Alice", 10)), ["alice"], primed: true, Now));
    }

    [Fact]
    public void ANewPlayerIsNotALeave()
    {
        Assert.Empty(FeedBridge.Leavers(Online(("Alice", 10)), ["Alice", "Carol"], primed: true, Now));
    }

    [Fact]
    public void SessionLengthSurvivesAcrossSweeps()
    {
        /* The first-seen time must be CARRIED, not reset each sweep - otherwise every
           reported session is exactly one poll interval long. */
        var previous = Online(("Alice", 30));
        var next = FeedBridge.NextRoster(previous, ["Alice"], Now);

        Assert.Equal(previous["Alice"], next["Alice"]);

        var leavers = FeedBridge.Leavers(next, [], primed: true, Now);
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(leavers).Session);
    }

    [Fact]
    public void ANewcomerStartsTheirSessionNow()
    {
        var next = FeedBridge.NextRoster(Online(), ["Carol"], Now);

        Assert.Equal(Now, next["Carol"]);
    }

    [Fact]
    public void SomebodyWhoLeavesIsDroppedFromTheRoster()
    {
        // Otherwise they would be reported as leaving again on every subsequent sweep.
        var next = FeedBridge.NextRoster(Online(("Alice", 10), ("Bob", 5)), ["Alice"], Now);

        Assert.False(next.ContainsKey("Bob"));
        Assert.Single(next);
    }

    [Fact]
    public void RejoiningAfterALeaveRestartsTheSession()
    {
        var afterLeave = FeedBridge.NextRoster(Online(("Alice", 30)), [], Now);
        var afterRejoin = FeedBridge.NextRoster(afterLeave, ["Alice"], Now.AddMinutes(5));

        Assert.Equal(Now.AddMinutes(5), afterRejoin["Alice"]);
    }
}
