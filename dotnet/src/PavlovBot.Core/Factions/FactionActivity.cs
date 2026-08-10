namespace PavlovBot.Core.Factions;

/// <param name="Player">Their in-game name.</param>
/// <param name="Rank">The rank they hold.</param>
/// <param name="Minutes">Recorded playtime, all time.</param>
/// <param name="LastSeen">Null when the bot has never seen them.</param>
public sealed record MemberActivity(string Player, string Rank, long Minutes, DateTimeOffset? LastSeen)
{
    /// <summary>How long since they were last seen, or null when never.</summary>
    public TimeSpan? Idle(DateTimeOffset now) => LastSeen is { } seen ? now - seen : null;
}

/// <param name="Member">Who.</param>
/// <param name="Reason">Why they were picked out, in a sentence.</param>
/// <param name="Days">Days since they were last seen, or null when never seen.</param>
public sealed record InactiveMember(MemberActivity Member, string Reason, int? Days);

/// <param name="Faction">Which faction.</param>
/// <param name="Total">Everybody on the roster.</param>
/// <param name="Active">How many were seen inside the activity window.</param>
/// <param name="RankCounts">Members per rank, in ladder order.</param>
/// <param name="Inactive">Members worth reviewing, longest idle first.</param>
public sealed record FactionReport(
    string Faction,
    int Total,
    int Active,
    IReadOnlyList<(string Rank, int Count, int Cap)> RankCounts,
    IReadOnlyList<InactiveMember> Inactive,
    long TotalMinutes)
{
    public int Inactive30 => Inactive.Count;

    /// <summary>Share of the roster seen recently, 0-100.</summary>
    public int ActivePercent => Total == 0 ? 0 : (int)Math.Round(100.0 * Active / Total);
}

/// <summary>
/// Faction activity and inactivity, as pure arithmetic over a roster.
/// </summary>
/// <remarks>
/// PURE, so the thresholds are testable without a roster directory and so the rule that
/// decides whether somebody is "inactive" can be argued with by reading one function.
///
/// IT RECOMMENDS AND NEVER ACTS. Nothing here demotes, removes or suspends anybody, and there
/// is deliberately no code path that could - a bot that quietly demoted a Sergeant who was on
/// holiday would be a bot nobody trusts with a roster again. The brief says automatic
/// enforcement only where the configuration explicitly supports it; there is no such
/// configuration, so there is no such enforcement.
///
/// NEVER SEEN IS NOT THE SAME AS IDLE, and the two are reported differently. A member the bot
/// has no record of is usually somebody whitelisted before it started tracking, or somebody
/// who plays under a different name - treating that as "inactive for 400 days" would put
/// long-standing members at the top of a removal list on the day the feature ships.
/// </remarks>
public static class FactionActivity
{
    /// <summary>Seen inside this window counts as active.</summary>
    public static TimeSpan ActiveWindow { get; } = TimeSpan.FromDays(14);

    /// <summary>Idle beyond this is worth a look.</summary>
    public static TimeSpan InactiveAfter { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Playtime below which a member is flagged even if they logged in recently.
    /// </summary>
    /// <remarks>
    /// Catches the shape "logged in for four minutes last Tuesday to look active", which a
    /// last-seen test alone reports as a fully active member.
    /// </remarks>
    public const long MinimumMonthlyMinutes = 60;

    public static FactionReport Report(
        FactionDefinition faction,
        IReadOnlyCollection<MemberActivity> members,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(faction);
        ArgumentNullException.ThrowIfNull(members);

        var active = members.Count(m => m.Idle(now) is { } idle && idle <= ActiveWindow);

        var counts = faction.Order
            .Select(rank => (
                Rank: rank,
                Count: members.Count(m => string.Equals(m.Rank, rank, StringComparison.OrdinalIgnoreCase)),
                Cap: faction.CapFor(rank)))
            .Reverse()   // highest first: "who runs this faction" is the question being asked
            .ToList();

        var inactive = members
            .Select(m => Assess(m, now))
            .OfType<InactiveMember>()
            .OrderByDescending(i => i.Days ?? int.MaxValue)
            .ToList();

        return new FactionReport(
            faction.Name,
            members.Count,
            active,
            counts,
            inactive,
            members.Sum(m => m.Minutes));
    }

    /// <summary>Whether a member is worth reviewing, and why. Null when they are fine.</summary>
    internal static InactiveMember? Assess(MemberActivity member, DateTimeOffset now)
    {
        var idle = member.Idle(now);

        if (idle is null)
        {
            /* NOT COUNTED AS INACTIVE, reported as unknown. The bot only knows who it has
               watched connect, and a roster predates it. Ranking these as the most inactive
               members would put the founders at the top of a removal list. */
            return new InactiveMember(member, "never seen by the bot - may predate tracking, or play under another name", null);
        }

        var days = (int)idle.Value.TotalDays;

        if (idle > InactiveAfter)
            return new InactiveMember(member, $"not seen for {days} days", days);

        if (member.Minutes < MinimumMonthlyMinutes)
            return new InactiveMember(member, $"only {member.Minutes} minutes of recorded playtime", days);

        return null;
    }
}
