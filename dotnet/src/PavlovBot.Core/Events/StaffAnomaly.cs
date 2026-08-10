namespace PavlovBot.Core.Events;

/// <param name="Unusual">Whether this burst is worth a human glance.</param>
/// <param name="Explanation">What was measured, in a sentence.</param>
public sealed record StaffAnomalyVerdict(bool Unusual, string Explanation)
{
    public static StaffAnomalyVerdict Normal { get; } = new(false, "Nothing unusual.");
}

/// <summary>
/// Whether a staff member's activity looks like a burst worth asking about.
/// </summary>
/// <remarks>
/// THIS IS NOT A DISCIPLINARY TOOL and the design says so at every level. It produces a
/// sentence for a human to read, has no action attached, and is worded as a question rather
/// than a finding. The brief is explicit - "an alert for review, not an automatic
/// disciplinary action" - and the way to honour that is for the type to be incapable of
/// anything else.
///
/// WHY BURSTS AND NOT TOTALS. A moderator with a high total is a moderator who does the work,
/// and flagging them would be exactly backwards. What is worth a look is a RATE that does not
/// match their own baseline: twenty-seven bans in eighteen minutes from somebody who normally
/// issues five a day is either a raid being handled or an account somebody else is using, and
/// both are things a second person should know about promptly.
///
/// COMPARED AGAINST THEIR OWN BASELINE, not against other staff. Staff roles differ - one
/// person handles appeals and another patrols - so a shared threshold would permanently flag
/// whoever does the most of anything.
/// </remarks>
public static class StaffAnomaly
{
    /// <summary>Actions in one hour below which nothing is ever flagged.</summary>
    /// <remarks>
    /// A FLOOR, so a quiet moderator's first busy hour is not an anomaly. Without it, somebody
    /// averaging 0.2 actions an hour trips the multiplier by doing three things, and an alert
    /// that fires on normal work is an alert people learn to ignore.
    /// </remarks>
    public const int MinimumBurst = 10;

    /// <summary>How many times their own hourly average counts as a burst.</summary>
    public const double BurstMultiple = 5.0;

    /// <summary>
    /// Compare one hour against the baseline the rest of the window establishes.
    /// </summary>
    /// <param name="peakHour">Actions in their busiest hour.</param>
    /// <param name="total">Everything they did in the window.</param>
    /// <param name="window">How long the window was.</param>
    public static StaffAnomalyVerdict Check(long peakHour, long total, TimeSpan window)
    {
        if (peakHour < MinimumBurst || window <= TimeSpan.Zero) return StaffAnomalyVerdict.Normal;

        var hours = Math.Max(window.TotalHours, 1);
        var average = total / hours;

        /* A ZERO BASELINE CANNOT PRODUCE A RATIO, and treating it as infinite would flag the
           first thing anybody ever did. Compared against the floor instead: a burst with no
           history behind it is unusual only if it is large in absolute terms. */
        if (average <= 0)
        {
            return peakHour >= MinimumBurst * 2
                ? new StaffAnomalyVerdict(true,
                    $"{peakHour} actions in one hour, with no earlier activity in this window to compare against.")
                : StaffAnomalyVerdict.Normal;
        }

        var ratio = peakHour / average;
        if (ratio < BurstMultiple) return StaffAnomalyVerdict.Normal;

        return new StaffAnomalyVerdict(true,
            $"{peakHour} actions in one hour against an average of {average:F1} per hour over " +
            $"the window - about {ratio:F0}x their own usual rate.");
    }
}
