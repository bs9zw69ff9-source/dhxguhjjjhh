using PavlovBot.Core.Vpn;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// One verdict, worded one way, for every surface that shows a screening.
/// </summary>
/// <remarks>
/// The same screening used to be rendered three ways: the connection card built its own
/// breakdown, <c>/iplookup</c> printed "VPN + datacenter (flagged (inconclusive))", and the
/// ban feed wrote a third phrasing. All three led with detector arithmetic, so the reader was
/// handed "2/3 flagged, confirmer gave no verdict" and left to work out what that meant about
/// the person in front of them.
///
/// What is pinned here is the DISTINCTION, not the prose. Likely, disputed, confirmed and
/// clean have to stay four different answers however the wording is edited later, because
/// collapsing any pair makes the bot sound certain about something it is not - and that
/// certainty is what a ban appeal turns on.
/// </remarks>
public class VpnVerdictTests
{
    private static VpnRecord Record(
        bool flagged, bool? confirmed, bool actionable,
        int screenHits = 2, int screenAnswered = 3, int confirmHits = 0, int confirmAnswered = 1,
        bool local = false) =>
        new()
        {
            Ip = "203.0.113.9",
            Local = local,
            Decision = new VpnDecision(flagged, confirmed, actionable, "test"),
            ScreenHits = screenHits,
            ScreenAnswered = screenAnswered,
            ConfirmHits = confirmHits,
            ConfirmAnswered = confirmAnswered,
        };

    // ---- the four answers stay four answers ----

    [Fact]
    public void NoScreeningIsNotChecked_NeverClean()
    {
        // Silence is not innocence. Reading one as the other clears somebody nothing looked at.
        var summary = VpnVerdict.Of(null);

        Assert.Equal(VpnStance.NotChecked, summary.Stance);
        Assert.Equal("NOT CHECKED", summary.Headline);
        Assert.False(summary.Concerning);
    }

    [Fact]
    public void APrivateAddressSaysThereWasNothingToCheck()
    {
        var summary = VpnVerdict.Of(Record(flagged: false, confirmed: null, actionable: false, local: true));

        Assert.Equal(VpnStance.Local, summary.Stance);
        Assert.False(summary.Concerning);
    }

    [Fact]
    public void NothingFlaggedIsClean()
    {
        var summary = VpnVerdict.Of(Record(flagged: false, confirmed: null, actionable: false, screenHits: 0));

        Assert.Equal(VpnStance.Clean, summary.Stance);
        Assert.Equal("CLEAN", summary.Headline);
        Assert.False(summary.Concerning);
    }

    [Fact]
    public void FlaggedWithNoConfirmationIsLikely_NotConfirmed()
    {
        /* THE ONE THAT MATTERS MOST. "FLAGGED (inconclusive)" used to be the headline, and
           it reads as a verdict when it is the absence of one. */
        var summary = VpnVerdict.Of(Record(flagged: true, confirmed: null, actionable: true));

        Assert.Equal(VpnStance.Likely, summary.Stance);
        Assert.Contains("LIKELY", summary.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIRMED", summary.Headline, StringComparison.Ordinal);
        Assert.True(summary.Concerning);
    }

    [Fact]
    public void FlaggedAndAgreedIsConfirmed()
    {
        var summary = VpnVerdict.Of(Record(flagged: true, confirmed: true, actionable: true, confirmHits: 1));

        Assert.Equal(VpnStance.Confirmed, summary.Stance);
        Assert.Contains("CONFIRMED", summary.Headline, StringComparison.Ordinal);
        Assert.Contains("the confirming check agreed", summary.Plain, StringComparison.Ordinal);
    }

    [Fact]
    public void FlaggedAndContradictedIsDisputed()
    {
        var summary = VpnVerdict.Of(Record(flagged: true, confirmed: false, actionable: false));

        Assert.Equal(VpnStance.Disputed, summary.Stance);
        Assert.Contains("cleared it", summary.Plain, StringComparison.Ordinal);
    }

    // ---- the action is separate from the verdict ----

    [Fact]
    public void ADisputedVerdictThatStillBannedSaysBanned()
    {
        /* The confirmer used to hold a veto, so disputed and not-banned were the same thing.
           Two screeners agreeing now outvotes it, and a verdict still reading "not banned"
           beside a player who was just banned gets read as a bug in the ban. */
        var summary = VpnVerdict.Of(Record(flagged: true, confirmed: false, actionable: true));

        Assert.Equal(VpnStance.Disputed, summary.Stance);
        Assert.Contains("AUTO-BANNED", summary.Action, StringComparison.Ordinal);

        // And it must STILL not claim the confirmation supported the ban.
        Assert.Contains("cleared it", summary.Plain, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlagBelowTheThresholdSaysNotBanned()
    {
        var summary = VpnVerdict.Of(Record(flagged: true, confirmed: null, actionable: false));

        Assert.Contains("Not banned", summary.Action, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTO-BANNED", summary.Action, StringComparison.Ordinal);
    }

    // ---- the wording itself ----

    [Fact]
    public void CountsAreWordsRatherThanFractions()
    {
        /* "2/3" beside "1/1" beside a threat score reads as arithmetic to evaluate. The
           point of the headline is that nobody should have to. */
        var summary = VpnVerdict.Of(Record(flagged: true, confirmed: true, actionable: true, confirmHits: 1));

        Assert.Contains("2 of 3 checks", summary.Plain, StringComparison.Ordinal);
        Assert.DoesNotContain("2/3", summary.Plain, StringComparison.Ordinal);
    }

    [Fact]
    public void OneCheckIsSingular()
    {
        var summary = VpnVerdict.Of(Record(flagged: true, confirmed: null, actionable: false,
            screenHits: 1, screenAnswered: 1));

        Assert.Contains("1 of 1 check", summary.Plain, StringComparison.Ordinal);
        Assert.DoesNotContain("1 of 1 checks", summary.Plain, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryHeadlineIsUpperCaseAndShort()
    {
        /* It is the only thing that has to be read, and it is read at a glance in a channel
           beside a dozen other cards. */
        foreach (var record in new[]
        {
            null,
            Record(flagged: false, confirmed: null, actionable: false, screenHits: 0),
            Record(flagged: true, confirmed: null, actionable: true),
            Record(flagged: true, confirmed: true, actionable: true, confirmHits: 1),
            Record(flagged: true, confirmed: false, actionable: false),
            Record(flagged: false, confirmed: null, actionable: false, local: true),
        })
        {
            var headline = VpnVerdict.Of(record).Headline;

            Assert.Equal(headline.ToUpperInvariant(), headline);
            Assert.True(headline.Length <= 26, headline);
        }
    }

    // ---- signals ----

    [Fact]
    public void OnlyAssertedSignalsAreNamed()
    {
        // A null is "the detector did not answer", never "no". That is the distinction the
        // whole subsystem is built on, and it must not leak away in the presentation.
        var record = Record(flagged: true, confirmed: null, actionable: false) with
        {
            Vpn = true,
            Proxy = null,
            Tor = false,
            Hosting = true,
        };

        Assert.Equal(["VPN", "datacenter"], VpnVerdict.Signals(record));
    }

    [Fact]
    public void NoRecordHasNoSignals()
    {
        Assert.Empty(VpnVerdict.Signals(null));
    }
}
