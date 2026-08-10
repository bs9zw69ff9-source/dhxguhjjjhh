using PavlovBot.Core.Economy;
using PavlovBot.Core.Factions;
using PavlovBot.Core.Events;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The pure rules behind faction, economy and staff intelligence.
/// </summary>
/// <remarks>
/// ALL THREE FLAG AND NONE OF THEM ACT, and most of these tests are about the refusal to be
/// confident. Each one drives a screen that a human reads before deciding something about a
/// real person's rank, balance or record, and the expensive failure in every case is a
/// confident wrong answer rather than a missed one.
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

    // ---- economy fraud ----

    private static FraudVerdict Assess(IReadOnlyList<long> credits, long own = 0, long server = 0, int minutes = 15) =>
        FraudSignals.Assess("Alice", credits, own, server, TimeSpan.FromMinutes(minutes));

    /// <summary>
    /// Small totals are never flagged, whatever the ratios say.
    /// </summary>
    /// <remarks>
    /// The floor is what stops the multipliers firing on noise. Somebody whose baseline is 5
    /// earning 60 is twelve times their usual and entirely unremarkable - and an alert that
    /// fires on the quietest players is one staff learn to ignore.
    /// </remarks>
    [Fact]
    public void SmallEarningsAreNeverFlaggedHoweverLargeTheRatio()
    {
        var verdict = Assess([20, 20, 20], own: 5, server: 5);

        Assert.False(verdict.Suspicious);
        Assert.Empty(verdict.Signals);
    }

    [Fact]
    public void EarningsFarAboveTheirOwnBaselineAreFlagged()
    {
        var verdict = Assess([9000, 9000], own: 1000, server: 1500);

        Assert.True(verdict.Suspicious);
        Assert.Contains(verdict.Signals, s => s.Kind == FraudSignalKind.FarAboveOwnBaseline);
    }

    /// <summary>Identical repeated amounts are a shape play does not produce.</summary>
    [Fact]
    public void ARunOfIdenticalCreditsIsFlagged()
    {
        var verdict = Assess([500, 500, 500, 500, 500, 500], own: 400, server: 400);

        Assert.Contains(verdict.Signals, s => s.Kind == FraudSignalKind.RepeatedIdenticalAmounts);
    }

    [Fact]
    public void CreditsArrivingFasterThanPlayAllowsAreFlagged()
    {
        // 80 credits in 10 minutes is 8 per minute.
        var verdict = Assess([.. Enumerable.Range(1, 80).Select(i => (long)(50 + i))], server: 500, minutes: 10);

        Assert.Contains(verdict.Signals, s => s.Kind == FraudSignalKind.ImpossibleFrequency);
    }

    /// <summary>
    /// A big but ordinary night is not flagged.
    /// </summary>
    /// <remarks>
    /// The control, and the one that matters most. Every test above proves the detector fires;
    /// this proves it can stay quiet, which is the property that decides whether anybody keeps
    /// the feature switched on.
    /// </remarks>
    [Fact]
    public void AGoodNightWithinNormalBoundsIsNotFlagged()
    {
        var verdict = Assess([600, 450, 700, 520, 380, 610], own: 2500, server: 2800);

        Assert.False(verdict.Suspicious);
    }

    /// <summary>
    /// A run shorter than the threshold is not a run.
    /// </summary>
    /// <remarks>
    /// THROUGH THE PUBLIC SURFACE, not by reaching for the internal helper. Core has no
    /// InternalsVisibleTo - unlike Host and Rcon - and that is a convention worth keeping:
    /// the pure domain is the part most worth being able to change freely behind its API.
    /// The boundary is exercised here by feeding Assess a run one short of the threshold.
    /// </remarks>
    [Fact]
    public void ARunShorterThanTheThresholdIsNotFlagged()
    {
        var justUnder = Assess([700, 700, 700, 700, 1200, 900], own: 400, server: 400);

        Assert.DoesNotContain(justUnder.Signals, s => s.Kind == FraudSignalKind.RepeatedIdenticalAmounts);
    }

    // ---- staff anomaly ----

    /// <summary>A steady worker is never flagged, however much they do.</summary>
    /// <remarks>
    /// A high total is somebody doing the job. Flagging it would be exactly backwards, and
    /// would teach staff that working hard gets them looked at.
    /// </remarks>
    [Fact]
    public void SteadyHighVolumeIsNotAnAnomaly()
    {
        // 240 actions over 24 hours, peaking at 15 in the busiest hour: average 10/h.
        var verdict = StaffAnomaly.Check(peakHour: 15, total: 240, TimeSpan.FromHours(24));

        Assert.False(verdict.Unusual);
    }

    [Fact]
    public void ASharpBurstAgainstTheirOwnBaselineIsFlagged()
    {
        // 27 in one hour from somebody averaging about 2 an hour.
        var verdict = StaffAnomaly.Check(peakHour: 27, total: 48, TimeSpan.FromHours(24));

        Assert.True(verdict.Unusual);
        Assert.Contains("27", verdict.Explanation, StringComparison.Ordinal);
    }

    /// <summary>A quiet moderator's first busy hour is not an anomaly.</summary>
    [Fact]
    public void ASmallBurstIsBelowTheFloor()
    {
        Assert.False(StaffAnomaly.Check(peakHour: 4, total: 5, TimeSpan.FromHours(24)).Unusual);
    }

    /// <summary>
    /// A window with no history to compare against never flags.
    /// </summary>
    /// <remarks>
    /// WRITING THIS FOUND DEAD CODE. The first version of Check had a zero-baseline branch
    /// with its own absolute threshold, and this test asserted it fired. It never could:
    /// peakHour cannot exceed total, so a positive peak always implies a positive average, and
    /// a zero peak returns at the floor first. The branch was removed.
    ///
    /// What remains is the honest behaviour - over a one-hour window the peak IS the average,
    /// so nothing is unusual. A burst only means something against a baseline, and a window
    /// this short has none.
    /// </remarks>
    [Fact]
    public void AWindowWithNoBaselineNeverFlags()
    {
        Assert.False(StaffAnomaly.Check(peakHour: 12, total: 12, TimeSpan.Zero).Unusual);
        Assert.False(StaffAnomaly.Check(peakHour: 40, total: 40, TimeSpan.FromHours(1)).Unusual);

        // The same 40 actions over a day, where a baseline exists, IS a burst.
        Assert.True(StaffAnomaly.Check(peakHour: 40, total: 60, TimeSpan.FromHours(24)).Unusual);
    }
}
