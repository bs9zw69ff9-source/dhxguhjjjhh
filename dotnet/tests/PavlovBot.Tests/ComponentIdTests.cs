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
}
