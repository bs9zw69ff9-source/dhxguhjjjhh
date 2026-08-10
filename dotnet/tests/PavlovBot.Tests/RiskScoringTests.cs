using PavlovBot.Core.Intelligence;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The risk score, which decides what staff think about a player before they meet them.
/// </summary>
/// <remarks>
/// A SCORE THAT BANS THE WRONG PERSON IS WORSE THAN NO SCORE, so most of these are about the
/// score REFUSING to be confident. The failure mode that matters is not missing an evader; it
/// is a household sharing an address with somebody who was banned last year, scoring 70, and
/// a moderator treating that as a finding.
/// </remarks>
public class RiskScoringTests
{
    private static RiskSignal Signal(RiskSignalKind kind, bool sensitive = false) =>
        new(kind, $"test signal: {kind}", "evidence", sensitive);

    [Fact]
    public void NoSignalsIsNotRisky()
    {
        var assessment = RiskScorer.Assess([]);

        Assert.Equal(0, assessment.Score);
        Assert.Equal(RiskBand.None, assessment.Band);
        Assert.Equal(RiskRecommendation.None, assessment.Recommendation);
    }

    /// <summary>
    /// THE CENTRAL PROPERTY. Weak signals must never add up to a strong verdict.
    /// </summary>
    /// <remarks>
    /// Under a plain sum these five are 45 and climbing - six would be 55, past the midpoint,
    /// on a player whose entire record is "new, on a VPN, has some warnings". Noisy-OR means
    /// each one closes a fraction of what is left, so the pile saturates well below anything
    /// a single strong signal reaches.
    /// </remarks>
    [Fact]
    public void WeakSignalsDoNotAddUpToAStrongVerdict()
    {
        var weak = new[]
        {
            Signal(RiskSignalKind.Anonymising),
            Signal(RiskSignalKind.ActiveWarnings),
            Signal(RiskSignalKind.BrandNewAccount),
            Signal(RiskSignalKind.PriorBan),
        };

        var assessment = RiskScorer.Assess(weak);

        Assert.True(assessment.Score < 55,
            $"four weak signals reached {assessment.Score}, which reads as a finding");
        Assert.NotEqual(RiskRecommendation.Investigate, assessment.Recommendation);
    }

    /// <summary>Even a long pile of the weakest signal stays below one strong one.</summary>
    /// <remarks>
    /// The generalisation of the test above, and the one that would catch somebody swapping
    /// the combination back to a sum to "make the numbers look right".
    /// </remarks>
    [Fact]
    public void NoPileOfWeakSignalsOvertakesOneStrongSignal()
    {
        var manyWeak = Enumerable.Range(0, 3)
            .Select(_ => Signal(RiskSignalKind.Anonymising))
            .Concat([Signal(RiskSignalKind.ActiveWarnings), Signal(RiskSignalKind.BrandNewAccount)])
            .ToList();

        var pile = RiskScorer.Assess(manyWeak).Score;
        var oneStrong = RiskScorer.Assess([Signal(RiskSignalKind.SharedAddressWithBanned)]).Score;

        Assert.True(pile < oneStrong, $"weak pile scored {pile}, one strong signal scored {oneStrong}");
    }

    /// <summary>One definitive signal is decisive on its own.</summary>
    [Fact]
    public void ABannedAccountScoresOutright()
    {
        var assessment = RiskScorer.Assess([Signal(RiskSignalKind.BannedAccount)]);

        Assert.Equal(100, assessment.Score);
        Assert.Equal(RiskConfidence.High, assessment.Confidence);
        Assert.Equal(RiskBand.Critical, assessment.Band);
    }

    /// <summary>
    /// One fact observed three times is one fact.
    /// </summary>
    /// <remarks>
    /// Three addresses shared with the same banned player is one coincidence, not three
    /// independent ones. Without the per-kind cap this scores 94 and reads as certainty.
    /// </remarks>
    [Fact]
    public void RepeatsOfOneKindDoNotInflateTheScore()
    {
        var once = RiskScorer.Assess([Signal(RiskSignalKind.SharedAddressWithBanned)]);

        var thrice = RiskScorer.Assess(
        [
            Signal(RiskSignalKind.SharedAddressWithBanned),
            Signal(RiskSignalKind.SharedAddressWithBanned),
            Signal(RiskSignalKind.SharedAddressWithBanned),
        ]);

        Assert.Equal(once.Score, thrice.Score);

        // But all three are still SHOWN. The moderator needs to see there were three.
        Assert.Equal(3, thrice.Signals.Count);
    }

    /// <summary>Two independent strong signals are more than one.</summary>
    [Fact]
    public void IndependentStrongSignalsCompound()
    {
        var one = RiskScorer.Assess([Signal(RiskSignalKind.SharedAddressWithBanned)]).Score;

        var two = RiskScorer.Assess(
        [
            Signal(RiskSignalKind.SharedAddressWithBanned),
            Signal(RiskSignalKind.EvasionMatch),
        ]).Score;

        Assert.True(two > one);
        Assert.True(two < 100, "two strong signals should not be certainty");
    }

    /// <summary>The score never leaves 0-100 however many signals arrive.</summary>
    [Fact]
    public void TheScoreStaysInRange()
    {
        var everything = Enum.GetValues<RiskSignalKind>().Select(k => Signal(k)).ToList();

        var assessment = RiskScorer.Assess(everything);

        Assert.InRange(assessment.Score, 0, 100);
    }

    // ---- confidence ----

    [Fact]
    public void OneWeakSignalIsLowConfidence()
    {
        Assert.Equal(RiskConfidence.Low, RiskScorer.Assess([Signal(RiskSignalKind.Anonymising)]).Confidence);
    }

    [Fact]
    public void TwoStrongSignalsAreHighConfidence()
    {
        var assessment = RiskScorer.Assess(
        [
            Signal(RiskSignalKind.NameMatchesBanned),
            Signal(RiskSignalKind.EvasionMatch),
        ]);

        Assert.Equal(RiskConfidence.High, assessment.Confidence);
    }

    /// <summary>
    /// Low confidence caps the recommendation however high the score gets.
    /// </summary>
    /// <remarks>
    /// The false-positive guard. A score built on weak evidence must not be able to tell a
    /// moderator to investigate, or the weakest data in the system drives the strongest action.
    /// </remarks>
    [Fact]
    public void LowConfidenceNeverRecommendsInvestigating()
    {
        // The only way to reach Low confidence is with fewer than three weak signals, so this
        // constructs the strongest such case and asserts it still holds back.
        foreach (var kind in Enum.GetValues<RiskSignalKind>())
        {
            var assessment = RiskScorer.Assess([Signal(kind), Signal(kind)]);
            if (assessment.Confidence != RiskConfidence.Low) continue;

            Assert.NotEqual(RiskRecommendation.Investigate, assessment.Recommendation);
        }
    }

    /// <summary>
    /// Nothing the scorer can produce ever recommends a ban.
    /// </summary>
    /// <remarks>
    /// THE HARD LIMIT, asserted over every reachable combination rather than trusted. The
    /// brief is explicit that a player must never be banned on a score alone, and the clearest
    /// way to guarantee that is for the type to have no value meaning "ban" - so this also
    /// fails if somebody adds one.
    /// </remarks>
    [Fact]
    public void NoCombinationOfSignalsEverRecommendsABan()
    {
        var kinds = Enum.GetValues<RiskSignalKind>();

        foreach (var a in kinds)
        {
            foreach (var b in kinds)
            {
                var recommendation = RiskScorer.Assess([Signal(a), Signal(b)]).Recommendation;

                Assert.True(
                    recommendation <= RiskRecommendation.Investigate,
                    $"{a} + {b} recommended {recommendation}");
            }
        }

        Assert.Equal(RiskRecommendation.Investigate, Enum.GetValues<RiskRecommendation>().Max());
    }

    /// <summary>The assessment names what it saw, not just how much.</summary>
    /// <remarks>
    /// A score with no sentence attached is a number a moderator can only trust or ignore.
    /// The wording comes from the strongest SIGNAL rather than the score, because two players
    /// on 82 can be there for completely different reasons.
    /// </remarks>
    [Fact]
    public void TheAssessmentIsDrivenByTheStrongestSignalNotTheScore()
    {
        var evasion = RiskScorer.Assess(
        [
            Signal(RiskSignalKind.SharedAddressWithBanned),
            Signal(RiskSignalKind.EvasionMatch),
        ]);

        var warnings = RiskScorer.Assess(
        [
            Signal(RiskSignalKind.ActiveWarnings),
            Signal(RiskSignalKind.PriorBan),
            Signal(RiskSignalKind.Anonymising),
            Signal(RiskSignalKind.BrandNewAccount),
        ]);

        Assert.Contains("evasion", evasion.Assessment, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(evasion.Assessment, warnings.Assessment);
    }

    /// <summary>Strongest first, so the decisive reason is read before the noise.</summary>
    [Fact]
    public void SignalsAreOrderedStrongestFirst()
    {
        var assessment = RiskScorer.Assess(
        [
            Signal(RiskSignalKind.BrandNewAccount),
            Signal(RiskSignalKind.BannedAccount),
            Signal(RiskSignalKind.Anonymising),
        ]);

        Assert.Equal(RiskSignalKind.BannedAccount, assessment.Signals[0].Kind);
    }
}
