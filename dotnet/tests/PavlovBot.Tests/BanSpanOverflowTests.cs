using PavlovBot.Core.Time;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A number too big for a TimeSpan used to take out whatever was parsing it.
/// </summary>
/// <remarks>
/// <c>TimeSpan.FromDays(365 * n)</c> overflows twice: the multiply wraps silently in a long,
/// and FromDays THROWS rather than saturating on whatever survives. On top of that
/// <c>ParseBanSpan</c> used <c>long.Parse</c> against a <c>\d+</c> regex with no ceiling, so
/// twenty digits threw before the arithmetic was even reached.
///
/// EVERY ONE OF THESE IS REACHABLE. A moderator's typo in <c>/tempban</c> is the obvious one.
/// The one that matters more is <c>ModsaveBanlist</c>, which parses the GAME'S OWN
/// <c>banlist.txt</c> - foreign data, written by another process - inside a
/// <c>store.UpdateAsync</c>. One malformed line there threw out of the import and lost the
/// whole batch, which is the same shape as the log-tail crash: a throw partway through a loop
/// over data nobody validated.
///
/// The answer is NULL, not a clamp. The callers already handle "cannot read this" - the ban
/// importer records it as permanent and keeps the raw text in the reason, which is both
/// correct for "999999999d" and fixable by hand. A silent hundred-year ban would look
/// deliberate.
/// </remarks>
public class BanSpanOverflowTests
{
    [Theory]
    [InlineData("99999999d")]                 // fits in a long, overflows TimeSpan
    [InlineData("99999999999999999999d")]     // does not fit in a long at all
    [InlineData("999999999999w")]
    [InlineData("9999999999y")]
    [InlineData("999999999mo")]
    [InlineData("9223372036854775807d")]      // long.MaxValue exactly
    [InlineData("1d 99999999d")]              // the compound case: one good part, one absurd
    public void AnAbsurdBanSpanIsUnreadableRatherThanFatal(string input)
    {
        Assert.Null(EasternTime.ParseBanSpan(input));
    }

    [Theory]
    [InlineData("99999999d")]
    [InlineData("9223372036854775807d")]
    [InlineData("99999999999999999999")]
    [InlineData("999999999h")]
    public void AnAbsurdDurationIsUnreadableRatherThanFatal(string input)
    {
        Assert.Null(EasternTime.ParseDuration(input));
    }

    [Fact]
    public void ACompoundSpanCannotWrapBackIntoAPlausibleOne()
    {
        /* The accumulator was a TimeSpan sum with no ceiling. Two parts that each fit could
           still add past the end, and a wrapped total is far worse than a rejection: it looks
           like a real ban length. */
        Assert.Null(EasternTime.ParseBanSpan("99y 99y 99y"));
        Assert.Null(EasternTime.ParseBanSpan("50000d 50000d 50000d 50000d"));
    }

    // ---- and none of that changed what a real ban length parses to ----

    [Theory]
    [InlineData("1d", 1440)]
    [InlineData("3d 4h", 4560)]
    [InlineData("1w", 10080)]
    [InlineData("1mo", 43200)]
    [InlineData("1y", 525600)]
    [InlineData("30s", 0)]
    [InlineData("2h 30m", 150)]
    public void OrdinaryBanSpansAreUnchanged(string input, int expectedMinutes)
    {
        var span = EasternTime.ParseBanSpan(input);

        Assert.NotNull(span);
        Assert.Equal(expectedMinutes, (int)span.Value.TotalMinutes);
    }

    [Fact]
    public void ThirtySecondsIsStillThirtySeconds()
    {
        // The minutes assertion above rounds it to zero; the span itself must not be.
        Assert.Equal(TimeSpan.FromSeconds(30), EasternTime.ParseBanSpan("30s"));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("10m", 600)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    [InlineData("45", 2700)]      // a bare number is minutes
    public void OrdinaryDurationsAreUnchanged(string input, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), EasternTime.ParseDuration(input));
    }

    [Fact]
    public void TheCeilingItselfIsAcceptedAndOneStepPastItIsNot()
    {
        // 100 years, expressed the long way, is exactly the ceiling.
        Assert.Equal(EasternTime.MaxSpan, EasternTime.ParseBanSpan("36500d"));
        Assert.Null(EasternTime.ParseBanSpan("36501d"));
    }

    [Fact]
    public void NonsenseIsStillJustNull()
    {
        Assert.Null(EasternTime.ParseBanSpan("until friday"));
        Assert.Null(EasternTime.ParseBanSpan("2026-08-01"));
        Assert.Null(EasternTime.ParseBanSpan(""));
        Assert.Null(EasternTime.ParseBanSpan(null));
        Assert.Null(EasternTime.ParseDuration("soon"));
    }
}
