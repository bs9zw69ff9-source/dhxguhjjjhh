using PavlovBot.Core.Time;
using PavlovBot.Host.Moderation;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A temp ban must not come back from the ban file as a permanent one.
/// </summary>
/// <remarks>
/// REPORTED FROM PRODUCTION: /banlist showing four bans, every one of them
/// "Permanent | by in-game" with "[unreadable unban value: 0m]" in the reason.
///
/// A ROUND TRIP THE BOT DID TO ITSELF, in two steps that were each defensible alone:
///
///   WRITING. TimeLeft formats sub-hour spans as "{remaining.Minutes}m", and Minutes
///   TRUNCATES. A ban with fifty seconds left is still active, so it is written to the game's
///   ban file - as "0m".
///
///   READING. ParseBanSpan rejects a zero quantity, so "0m" is unparseable, and the importer
///   treated unparseable as PERMANENT on the reasoning that guessing a duration is worse than
///   holding someone. Sound for corrupt input; wrong here, because "no time left" is not
///   unreadable, it is the answer.
///
/// Every temp ban therefore became permanent in its final minute, silently, and the note in
/// the reason made it look like the game had written something malformed.
/// </remarks>
public class TempBanExpiryTests
{
    // ---- the writing half ----

    [Theory]
    [InlineData(50)]     // under a minute: the case that produced "0m"
    [InlineData(1)]
    public void ABanWithSecondsLeftIsNeverWrittenAsZero(int secondsLeft)
    {
        var now = DateTimeOffset.UtcNow;
        var time = new TestClock(now);

        var written = EasternTime.TimeLeft(now.AddSeconds(secondsLeft), time);

        Assert.NotEqual("0m", written);
        Assert.Equal("1m", written);
    }

    [Fact]
    public void LongerSpansAreUnchanged()
    {
        // The rounding must not disturb the spans that were always fine.
        var now = DateTimeOffset.UtcNow;
        var time = new TestClock(now);

        Assert.Equal("5m", EasternTime.TimeLeft(now.AddMinutes(5), time));
        Assert.Equal("2h 30m", EasternTime.TimeLeft(now.AddHours(2).AddMinutes(30), time));
        Assert.Equal("expired", EasternTime.TimeLeft(now.AddMinutes(-1), time));
    }

    // ---- the reading half ----

    [Theory]
    [InlineData("0m")]
    [InlineData("0")]
    [InlineData("00h")]
    [InlineData("expired")]
    [InlineData(" 0m ")]
    public void AnUnbanValueWithNoTimeLeftIsRecognisedAsElapsed(string unban) =>
        Assert.True(ModsaveBanlist.DenotesElapsed(unban),
            $"\"{unban}\" means the ban has served its time, not that it is permanent");

    [Theory]
    [InlineData("Permanent")]
    [InlineData("1m")]
    [InlineData("3d 4h")]
    [InlineData("30s")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsLeftToTheNormalParser(string? unban) =>
        Assert.False(ModsaveBanlist.DenotesElapsed(unban),
            $"\"{unban}\" is not an elapsed span and must not be treated as one");

    [Fact]
    public void PermanentIsStillPermanent()
    {
        /* The regression that would matter most in the other direction. Making elapsed values
           expire must not make a genuine permanent ban expire too - that would quietly free
           everybody the staff meant to keep out. */
        Assert.False(ModsaveBanlist.DenotesElapsed("Permanent"));
        Assert.Null(EasternTime.ParseBanSpan("Permanent"));
    }
}
