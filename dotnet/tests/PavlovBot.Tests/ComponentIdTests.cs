using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

public class ComponentIdTests
{
    [Fact]
    public void RoundTripsPrefixAndArguments()
    {
        var id = ComponentId.Parse(ComponentId.Encode("page", "next", "3"));

        Assert.Equal("page", id.Prefix);
        Assert.Equal("next", id.Argument(0));
        Assert.Equal(3, id.ArgumentAsInt(1));
    }

    [Fact]
    public void APrefixWithNoArgumentsHasNoTrailingSeparator()
    {
        Assert.Equal("verify", ComponentId.Encode("verify"));
    }

    [Fact]
    public void MissingArgumentsAreNullRatherThanAnIndexError()
    {
        /* Custom ids arrive from buttons posted by OLDER BUILDS, which may have encoded
           fewer arguments than the current handler expects. Throwing here would turn a
           stale button into a logged exception on every click. */
        var id = ComponentId.Parse("page");

        Assert.Null(id.Argument(0));
        Assert.Null(id.Argument(5));
        Assert.Equal(7, id.ArgumentAsInt(0, fallback: 7));
    }

    [Fact]
    public void AnOverlongIdIsRejectedRatherThanTruncated()
    {
        /* Discord silently accepts and truncates at 100 characters. A truncated id still
           looks valid and routes to the wrong handler - or to none - which would surface
           months later as "that button does nothing", only for long player names. */
        var tooLong = new string('x', ComponentId.MaxLength);

        var ex = Assert.Throws<ArgumentException>(() => ComponentId.Encode("page", tooLong));
        Assert.Contains("100", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyTheLimitIsAllowed()
    {
        var argument = new string('x', ComponentId.MaxLength - "page:".Length);

        var encoded = ComponentId.Encode("page", argument);

        Assert.Equal(ComponentId.MaxLength, encoded.Length);
    }

    [Fact]
    public void APrefixContainingTheSeparatorIsRejected()
    {
        // It would parse back as a different prefix plus an argument, routing elsewhere.
        Assert.Throws<ArgumentException>(() => ComponentId.Encode("page:next", "1"));
    }

    [Fact]
    public void ArgumentsContainingTheSeparatorSplitIntoMore()
    {
        /* Documenting the sharp edge rather than pretending it does not exist: callers
           encoding free text must sanitise it. Parse never throws, so the failure is a
           wrong argument index, not a crash. */
        var id = ComponentId.Parse(ComponentId.Encode("cfg", "a:b"));

        Assert.Equal("a", id.Argument(0));
        Assert.Equal("b", id.Argument(1));
    }
    [Fact]
    public void TheNodeBotsPersistentPanelButtonsStillRoute()
    {
        /* The verify and menu panels sit in a channel indefinitely and outlive the bot that
           posted them. After the cutover their buttons still carry Node's underscore ids,
           which parse as one long prefix and match no handler - so every press answered
           "that control belongs to an older version of this message" while the panel looked
           perfectly fine. */
        var verify = ComponentId.Parse("verify_start");
        Assert.Equal("verify", verify.Prefix);
        Assert.Equal("start", verify.Argument(0));

        var menu = ComponentId.Parse("menu_start");
        Assert.Equal("menu", menu.Prefix);
        Assert.Equal("start", menu.Argument(0));
    }

    [Fact]
    public void TransientNodeControlsAreNotTranslated()
    {
        /* arr_*, cfg_* and pg_* belonged to an in-message collector that died with the
           command that made it. Those really are stale, and "run the command again" is the
           honest answer - translating them would route a click into a booking that no
           longer exists. */
        Assert.Equal("arr_confirm", ComponentId.Parse("arr_confirm").Prefix);
        Assert.Equal("cfg_menu", ComponentId.Parse("cfg_menu").Prefix);
        Assert.Equal("pg_next", ComponentId.Parse("pg_next").Prefix);
    }

    [Fact]
    public void TheStaffDecisionButtonsAreDeliberatelyNotTranslated()
    {
        /* Their token refers to a pending request in the NODE bot's store, which this bot
           cannot resolve. Translating them would make Accept look like it worked and grant
           nothing at all. */
        Assert.Equal("verifyreq_ok", ComponentId.Parse("verifyreq_ok:abc123").Prefix);
    }

    [Fact]
    public void OurOwnIdsAreUnaffectedByTheTranslation()
    {
        var id = ComponentId.Parse(ComponentId.Encode("verify", "ok", "a1b2c3"));

        Assert.Equal("verify", id.Prefix);
        Assert.Equal("ok", id.Argument(0));
        Assert.Equal("a1b2c3", id.Argument(1));
    }

}
