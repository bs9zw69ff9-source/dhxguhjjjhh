namespace PavlovBot.Core.Intelligence;

/// <summary>
/// Turning a set of signals into a score a moderator can argue with.
/// </summary>
/// <remarks>
/// PURE. Signals are gathered elsewhere and passed in, so every rule here is testable without
/// a filesystem, RCON or Discord - and, more usefully, a disputed score can be reproduced
/// from the signals alone.
///
/// TWO PROPERTIES THIS HAS TO HAVE, and a plain sum has neither:
///
///   WEAK SIGNALS MUST NOT ADD UP TO A BAN. Six weak signals summed is 60, which reads as
///   "probably evading" while describing somebody who happens to be on a VPN with a common
///   name. Signals combine by NOISY-OR instead: each one closes a fraction of the remaining
///   gap to 100 rather than adding to a running total. Six signals worth 10 reach 47, not 60,
///   and no number of them ever reaches a strong signal's territory.
///
///   ONE STRONG SIGNAL MUST BE ENOUGH. The same rule gives this for free: a definitive signal
///   worth 100 - this exact account is banned - closes the whole gap on its own and no amount
///   of contradicting weak evidence pulls it back down.
///
/// ONE CONTRIBUTION PER KIND. Three addresses shared with the same banned player is one fact
/// observed three times, not three independent facts, and letting each score would inflate a
/// single coincidence into a certainty. Repeats stay in the signal list, because a moderator
/// should see all three, and score nothing.
///
/// NOTHING HERE BANS ANYBODY. The output is a score, its reasons and a suggestion for a
/// person. That is a deliberate limit, not an omission - an automated ban on a probabilistic
/// score is how a shared household address becomes a permanent ban on somebody's brother.
/// </remarks>
public static class RiskScorer
{
    /// <summary>Weight at or above which a single signal is treated as strong.</summary>
    /// <remarks>
    /// Used for CONFIDENCE, not for the score. Confidence asks "how much of this is solid",
    /// which is a question about the strongest evidence rather than the total.
    /// </remarks>
    public const int StrongSignal = 40;

    /// <summary>Weight at or above which one signal alone justifies high confidence.</summary>
    public const int DecisiveSignal = 60;

    public static RiskAssessment Assess(IEnumerable<RiskSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var ordered = signals
            .Where(s => s is not null)
            .OrderByDescending(s => s.Weight)
            .ToList();

        if (ordered.Count == 0) return RiskAssessment.Clean;

        /* ONE PER KIND, strongest first, so the repeats that follow are the ones dropped from
           the arithmetic. They stay in Signals for the explanation. */
        var counted = ordered
            .GroupBy(s => s.Kind)
            .Select(g => g.First())
            .ToList();

        var score = Combine(counted.Select(s => s.Weight));
        var confidence = ConfidenceOf(counted);

        return new RiskAssessment(
            score,
            confidence,
            ordered,
            Describe(counted, score),
            RecommendationFor(score, confidence));
    }

    /// <summary>
    /// Noisy-OR: each signal closes part of the gap that is left, rather than adding.
    /// </summary>
    /// <remarks>
    /// <c>1 - Π(1 - w/100)</c>. Read as "the chance that at least one of these is really
    /// telling the truth", if the weights are read as rough independent likelihoods. The
    /// weights are not calibrated probabilities and this is not a claim that they are - what
    /// is being borrowed is the SHAPE: monotonic, saturating at 100, and never letting a pile
    /// of weak evidence overtake one strong piece.
    /// </remarks>
    internal static int Combine(IEnumerable<int> weights)
    {
        var remaining = 1.0;
        foreach (var weight in weights)
        {
            var clamped = Math.Clamp(weight, 0, 100) / 100.0;
            remaining *= 1.0 - clamped;
        }

        // Round rather than truncate: two signals worth 50 are 75, not 74.
        return (int)Math.Round((1.0 - remaining) * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// How much the evidence can be relied on, which is about its STRENGTH, not its total.
    /// </summary>
    /// <remarks>
    /// A moderator needs to tell "one decisive fact" apart from "a lot of circumstantial
    /// noise that happens to add up", and the score alone cannot. Kept deliberately crude:
    /// three bands somebody can hold in their head beat a second opaque number.
    /// </remarks>
    internal static RiskConfidence ConfidenceOf(IReadOnlyList<RiskSignal> counted)
    {
        var strong = counted.Count(s => s.Weight >= StrongSignal);

        if (counted.Any(s => s.Weight >= DecisiveSignal) || strong >= 2) return RiskConfidence.High;
        if (strong == 1 || counted.Count >= 3) return RiskConfidence.Medium;
        return RiskConfidence.Low;
    }

    public static RiskBand BandOf(int score) => score switch
    {
        < 10 => RiskBand.None,
        < 30 => RiskBand.Low,
        < 55 => RiskBand.Medium,
        < 80 => RiskBand.High,
        _ => RiskBand.Critical,
    };

    /// <summary>
    /// One sentence naming what this pattern looks like.
    /// </summary>
    /// <remarks>
    /// NAMED FROM THE STRONGEST SIGNAL rather than from the score, because the score does not
    /// know what it is made of. "82/100" describes ban evasion and a heavily-warned regular
    /// identically; the signal that drove it does not.
    ///
    /// Every phrasing here is hedged except the one case that is not a guess - a banned
    /// account is a fact about the ban list, not an inference.
    /// </remarks>
    internal static string Describe(IReadOnlyList<RiskSignal> counted, int score)
    {
        if (counted.Count == 0) return "Nothing on record.";

        var top = counted[0];

        return top.Kind switch
        {
            RiskSignalKind.BannedAccount =>
                "This account is banned. Not a prediction - it is on the ban list now.",

            RiskSignalKind.SharedAddressWithBanned or RiskSignalKind.EvasionMatch =>
                "Consistent with ban evasion. Addresses are shared by households, phone " +
                "tethering and student halls, so this is a reason to look rather than a finding.",

            RiskSignalKind.NameMatchesBanned =>
                "A name here matches a banned player. Names are not identifiers and can be " +
                "reused by anybody, so on its own this proves nothing.",

            RiskSignalKind.Flagged =>
                "Carrying a standing ban-evasion flag. Check whether the flag should still exist.",

            RiskSignalKind.PriorBan =>
                "Has prior moderation history. That is a record, not a prediction about now.",

            RiskSignalKind.Anonymising =>
                "Connecting through a VPN, proxy or hosting network. Common enough among " +
                "ordinary players that it means little by itself.",

            _ => score >= 30
                ? "Several weak indicators and nothing decisive. Worth an eye, not an action."
                : "Nothing that stands out.",
        };
    }

    /// <summary>
    /// What a person should do. Never what the bot has already done.
    /// </summary>
    /// <remarks>
    /// TOPS OUT AT "INVESTIGATE". There is deliberately no value here that means ban: the
    /// recommendation is read by humans and the ceiling is the clearest way to say that a
    /// score is not evidence. Low confidence caps the recommendation regardless of score,
    /// because a high score built on weak evidence is exactly the false positive that costs
    /// somebody their account.
    /// </remarks>
    internal static RiskRecommendation RecommendationFor(int score, RiskConfidence confidence)
    {
        if (score < 10) return RiskRecommendation.None;
        if (confidence == RiskConfidence.Low) return RiskRecommendation.Monitor;

        return BandOf(score) switch
        {
            RiskBand.Critical when confidence == RiskConfidence.High => RiskRecommendation.Investigate,
            RiskBand.Critical or RiskBand.High => RiskRecommendation.ReviewManually,
            RiskBand.Medium => RiskRecommendation.Monitor,
            _ => RiskRecommendation.Monitor,
        };
    }
}
