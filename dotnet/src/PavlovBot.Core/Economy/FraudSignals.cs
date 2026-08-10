namespace PavlovBot.Core.Economy;

/// <summary>What about somebody's earnings looks wrong.</summary>
/// <remarks>
/// THE WEIGHT IS THE ENUM VALUE, as in the risk engine, so a signal and its weight cannot
/// drift into two fields that have to agree by convention.
/// </remarks>
public enum FraudSignalKind
{
    /// <summary>Earnings far above what this player normally makes.</summary>
    FarAboveOwnBaseline = 60,

    /// <summary>Earnings far above what anybody on the server normally makes.</summary>
    FarAboveServerNormal = 45,

    /// <summary>Credits arriving faster than play could produce them.</summary>
    ImpossibleFrequency = 40,

    /// <summary>The same amount over and over, which play does not produce.</summary>
    RepeatedIdenticalAmounts = 30,

    /// <summary>A single credit larger than anything legitimate pays out.</summary>
    OversizedSingleCredit = 25,
}

/// <param name="Kind">What was seen. Carries the weight.</param>
/// <param name="Summary">One sentence naming the numbers behind it.</param>
public sealed record FraudSignal(FraudSignalKind Kind, string Summary)
{
    public int Weight => (int)Kind;
}

/// <param name="Player">Who.</param>
/// <param name="Earned">What they took in over the window.</param>
/// <param name="Baseline">Their own usual rate over the same length of time.</param>
/// <param name="Confidence">How much the signals can be relied on.</param>
public sealed record FraudVerdict(
    string Player,
    long Earned,
    long Baseline,
    IReadOnlyList<FraudSignal> Signals,
    string Assessment,
    Intelligence.RiskConfidence Confidence)
{
    public bool Suspicious => Signals.Count > 0;

    /// <summary>How many times their own baseline this is. Null when there is no baseline.</summary>
    public double? Multiple => Baseline > 0 ? (double)Earned / Baseline : null;
}

/// <summary>
/// Deciding whether somebody's earnings look like an exploit.
/// </summary>
/// <remarks>
/// PURE. Every input is passed in, so a disputed flag reproduces from the numbers alone, and
/// the thresholds can be argued with by reading one file rather than by watching the bot.
///
/// IT FLAGS AND NEVER PUNISHES. The brief is explicit - "do not automatically punish players
/// solely based on statistical anomalies" - and there is no code path here that could. A big
/// number is not evidence of an exploit: a good night, a payout that had been owed for a week,
/// and an actual duplication bug all look identical in a total. The output is a case worth
/// opening, not a ban.
///
/// AGAINST THEIR OWN BASELINE FIRST. Players earn at wildly different rates depending on what
/// they do, so a server-wide threshold flags the productive and misses a modest exploiter. The
/// server-wide signal is kept as a second, weaker check for the case where somebody has no
/// history to be compared against - which is exactly what a fresh alt used to launder money
/// looks like.
/// </remarks>
public static class FraudSignals
{
    /// <summary>Multiple of a player's own baseline that counts as far above it.</summary>
    public const double OwnBaselineMultiple = 8.0;

    /// <summary>Multiple of the server's typical earnings that counts as far above it.</summary>
    public const double ServerNormalMultiple = 6.0;

    /// <summary>Credits per minute above which play cannot be the explanation.</summary>
    /// <remarks>
    /// Pavlov pays for kills and objectives; a credit every few seconds sustained over a
    /// window is a script or a bug, not a firefight.
    /// </remarks>
    public const double ImpossibleCreditsPerMinute = 4.0;

    /// <summary>How many identical amounts in a row stop looking like coincidence.</summary>
    public const int IdenticalRunLength = 5;

    /// <summary>Earnings below which nothing is ever flagged, whatever the ratios say.</summary>
    /// <remarks>
    /// A FLOOR, and it is what stops the multipliers firing on noise. Somebody whose baseline
    /// is 5 earning 60 is eight times their usual and completely unremarkable; without this
    /// the quietest players would be flagged constantly and staff would learn to ignore the
    /// alerts - which costs more than missing one exploit.
    /// </remarks>
    public const long MinimumToFlag = 2500;

    /// <param name="credits">Individual credits in the window, in order.</param>
    /// <param name="ownBaseline">What this player usually earns over the same span.</param>
    /// <param name="serverNormal">What a typical player earns over the same span.</param>
    /// <param name="window">The span these credits arrived in.</param>
    public static FraudVerdict Assess(
        string player,
        IReadOnlyList<long> credits,
        long ownBaseline,
        long serverNormal,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(credits);

        var earned = credits.Sum();
        var signals = new List<FraudSignal>();

        if (earned < MinimumToFlag)
            return new FraudVerdict(player, earned, ownBaseline, [], "Nothing unusual.", Intelligence.RiskConfidence.Low);

        if (ownBaseline > 0 && earned >= ownBaseline * OwnBaselineMultiple)
        {
            signals.Add(new FraudSignal(FraudSignalKind.FarAboveOwnBaseline,
                $"Earned {earned:N0} against their usual {ownBaseline:N0} for this length of time - " +
                $"about {(double)earned / ownBaseline:F0}x."));
        }

        if (serverNormal > 0 && earned >= serverNormal * ServerNormalMultiple)
        {
            signals.Add(new FraudSignal(FraudSignalKind.FarAboveServerNormal,
                $"Earned {earned:N0} against a server-typical {serverNormal:N0} over the same span."));
        }

        if (window > TimeSpan.Zero && credits.Count / window.TotalMinutes >= ImpossibleCreditsPerMinute)
        {
            signals.Add(new FraudSignal(FraudSignalKind.ImpossibleFrequency,
                $"{credits.Count} separate credits in {window.TotalMinutes:F0} minutes - " +
                $"{credits.Count / window.TotalMinutes:F1} per minute."));
        }

        if (LongestIdenticalRun(credits) is var run && run >= IdenticalRunLength)
        {
            signals.Add(new FraudSignal(FraudSignalKind.RepeatedIdenticalAmounts,
                $"{run} identical credits in a row, which ordinary play does not produce."));
        }

        /* AGAINST THE REST OF THEIR OWN WINDOW, not a fixed number. What counts as an
           oversized payout depends entirely on the server's economy, and a constant here
           would be wrong for every server but the one it was tuned on. */
        if (credits.Count > 1)
        {
            var largest = credits.Max();
            var rest = earned - largest;
            if (rest > 0 && largest > rest)
            {
                signals.Add(new FraudSignal(FraudSignalKind.OversizedSingleCredit,
                    $"One credit of {largest:N0} outweighs everything else in the window combined ({rest:N0})."));
            }
        }

        var ordered = signals.OrderByDescending(s => s.Weight).ToList();

        return new FraudVerdict(
            player,
            earned,
            ownBaseline,
            ordered,
            Describe(ordered),
            /* THE SAME SCORER THE RISK ENGINE USES, over the fraud signals. One notion of
               confidence across the bot means a moderator learns to read it once. */
            Intelligence.RiskScorer.ConfidenceOf([.. ordered.Select(s =>
                new Intelligence.RiskSignal((Intelligence.RiskSignalKind)s.Weight, s.Summary))]));
    }

    /// <summary>The longest run of the same amount repeated consecutively.</summary>
    internal static int LongestIdenticalRun(IReadOnlyList<long> credits)
    {
        if (credits.Count == 0) return 0;

        var longest = 1;
        var current = 1;

        for (var i = 1; i < credits.Count; i++)
        {
            current = credits[i] == credits[i - 1] ? current + 1 : 1;
            if (current > longest) longest = current;
        }

        return longest;
    }

    private static string Describe(IReadOnlyList<FraudSignal> signals) => signals.Count == 0
        ? "Nothing unusual."
        : signals[0].Kind switch
        {
            FraudSignalKind.ImpossibleFrequency or FraudSignalKind.RepeatedIdenticalAmounts =>
                "The SHAPE of these credits does not look like play. Worth reading the transactions " +
                "before concluding anything - a payout that had been owed for a while arrives in one burst too.",

            FraudSignalKind.FarAboveOwnBaseline or FraudSignalKind.FarAboveServerNormal =>
                "Well outside what this player normally earns. A good night looks like this as often " +
                "as an exploit does, so this is a reason to look rather than a finding.",

            _ => "One credit dominates the window. Check where it came from.",
        };
}
