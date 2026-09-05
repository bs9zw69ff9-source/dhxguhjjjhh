using PavlovBot.Host.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Quoting what the server actually said when its reply was not JSON.
/// </summary>
/// <remarks>
/// THE DIAGNOSIS WAS BEING THROWN AWAY. The report said only "the server's reply to
/// RefreshList could not be parsed", which names the one thing nobody needed telling. What
/// the reply WAS is the whole answer - an auth error, a command this build does not have, a
/// plain-text refusal - and the bot read it, decided it could not use it, and discarded it.
/// Three servers failing identically then looked like a bot fault rather than something the
/// servers were saying out loud.
///
/// This is server-controlled text on its way into a Discord embed, so it is bounded and
/// escaped here as well as sanitised at the point it is rendered.
/// </remarks>
public class RosterProblemExcerptTests
{
    [Fact]
    public void TheReplyIsQuotedSoTheCauseIsInTheMessage()
    {
        Assert.Equal("\"Authentication required\"", RconRegistry.Excerpt("Authentication required"));
    }

    [Fact]
    public void AnEmptyReplyIsSaidRatherThanQuotedAsNothing()
    {
        /* "the reply was \"\"" reads as a formatting bug. An empty reply is a distinct and
           very different cause from a reply with words in it. */
        Assert.Equal("it was empty", RconRegistry.Excerpt(""));
        Assert.Equal("it was empty", RconRegistry.Excerpt("   \r\n "));
        Assert.Equal("it was empty", RconRegistry.Excerpt(null));
    }

    [Fact]
    public void BackticksCannotBreakOutOfTheCodeSpanItLandsIn()
    {
        // The reply is whatever the server sent, and it ends up inside a Discord message.
        Assert.DoesNotContain('`', RconRegistry.Excerpt("nope ``` **not really**"));
    }

    [Fact]
    public void AVeryLongReplyIsTruncatedRatherThanPastedWhole()
    {
        var excerpt = RconRegistry.Excerpt(new string('x', 5000));

        Assert.True(excerpt.Length < 250, $"excerpt was {excerpt.Length}");
        Assert.EndsWith("...\"", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeadIsKeptBecauseThatIsWhereTheCauseIs()
    {
        // A non-JSON reply says what it is in its first few words.
        Assert.StartsWith("\"Error: unknown command", RconRegistry.Excerpt(
            "Error: unknown command RefreshList" + new string('.', 500)), StringComparison.Ordinal);
    }
}
