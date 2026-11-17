using PavlovBot.Core.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The pure rules behind faction intelligence (<c>/factionstats</c>).
/// </summary>
/// <remarks>
/// IT FLAGS AND DOES NOT ACT, and most of these tests are about the refusal to be confident.
/// The report drives a screen a human reads before deciding something about a real person's
/// rank, and the expensive failure is a confident wrong answer rather than a missed one.
/// </remarks>
public class IntelligenceRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly FactionDefinition Nypd = FactionRegistry.Get("NYPD")!;

    private static MemberActivity Member(string name, string rank, long minutes, int? daysAgo) =>
        new(name, rank, minutes, daysAgo is { } d ? Now.AddDays(-d) : null);

    // ---- faction activity ----

    [Fact]
    public void RecentlySeenMembersCountAsActive()
    {
        var report = FactionActivity.Report(Nypd,
        [
            Member("Alice", "Cadet", 600, daysAgo: 1),
            Member("Bob", "Cadet", 600, daysAgo: 40),
        ], Now);

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Active);
        Assert.Equal(50, report.ActivePercent);
    }

    [Fact]
    public void LongIdleMembersAreFlaggedWithTheirDays()
    {
        var report = FactionActivity.Report(Nypd, [Member("Bob", "Sergeant", 600, daysAgo: 45)], Now);

        var flagged = Assert.Single(report.Inactive);
        Assert.Equal(45, flagged.Days);
        Assert.Contains("45 days", flagged.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Logging in briefly does not make somebody active.
    /// </summary>
    /// <remarks>
    /// The shape this catches is "log in for four minutes to look active", which a last-seen
    /// test on its own reports as a fully engaged member.
    /// </remarks>
    [Fact]
    public void ARecentButBarelyPlayingMemberIsStillFlagged()
    {
        var report = FactionActivity.Report(Nypd, [Member("Ghost", "Cadet", 4, daysAgo: 1)], Now);

        Assert.Contains("4 minutes", Assert.Single(report.Inactive).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// NEVER SEEN IS NOT THE SAME AS INACTIVE, and it must not sort as the worst case.
    /// </summary>
    /// <remarks>
    /// A member the bot has no record of usually predates its tracking or plays under another
    /// name. Ranked as "inactive for 400 days" they would top a removal list on the day this
    /// feature shipped, and the founders would be first.
    /// </remarks>
    [Fact]
    public void AMemberWithNoRecordIsReportedSeparatelyRatherThanAsMaximallyIdle()
    {
        var report = FactionActivity.Report(Nypd,
        [
            Member("Unknown", "Chief of Police", 0, daysAgo: null),
            Member("Idle", "Cadet", 600, daysAgo: 60),
        ], Now);

        var unknown = report.Inactive.Single(i => i.Member.Player == "Unknown");

        Assert.Null(unknown.Days);
        Assert.Contains("never seen", unknown.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("predate", unknown.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnActiveMemberIsNotFlaggedAtAll()
    {
        var report = FactionActivity.Report(Nypd, [Member("Alice", "Cadet", 5000, daysAgo: 2)], Now);

        Assert.Empty(report.Inactive);
    }

    [Fact]
    public void RanksAreCountedHighestFirst()
    {
        var report = FactionActivity.Report(Nypd,
        [
            Member("A", Nypd.Lowest, 600, 1),
            Member("B", Nypd.Lowest, 600, 1),
            Member("C", Nypd.Highest, 600, 1),
        ], Now);

        Assert.Equal(Nypd.Highest, report.RankCounts[0].Rank);
        Assert.Equal(2, report.RankCounts.Single(r => r.Rank == Nypd.Lowest).Count);
    }
}
