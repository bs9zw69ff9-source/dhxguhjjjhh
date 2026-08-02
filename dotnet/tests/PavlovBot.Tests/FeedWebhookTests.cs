using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Observability;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Registering a feed webhook, and being able to tell what it is doing.
/// </summary>
/// <remarks>
/// THE BUG: <c>new DiscordWebhookClient(url)</c> makes a BLOCKING HTTP call and throws when
/// the webhook cannot be fetched. Registration happened once, at startup, so a webhook that
/// had been rotated or deleted - or merely a moment where Discord was unreachable as the bot
/// booted - permanently disabled that feed for the lifetime of the process. It logged once
/// and every later post returned silently, which is indistinguishable from a quiet server.
///
/// The status string has existed since the class was written and nothing ever displayed it,
/// so "the webhook doesn't work" was unanswerable from inside Discord. That is what
/// <c>/feeds</c> is for.
/// </remarks>
public class FeedWebhookTests
{
    private static FeedWebhooks Build() =>
        new(NullLogger<FeedWebhooks>.Instance, new MetricsRegistry());

    /// <summary>Well-formed and correctly shaped, but not a real webhook.</summary>
    private const string PlausibleUrl = "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnop";

    [Fact]
    public void RegisteringDoesNotTouchTheNetwork()
    {
        /* The whole fix. If this call opened the webhook it would take most of a second and
           fail, and the feed would then be dead until the next restart. */
        var feeds = Build();

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        feeds.Register(FeedWebhooks.Connect, PlausibleUrl);
        elapsed.Stop();

        Assert.True(elapsed.ElapsedMilliseconds < 200, $"registration took {elapsed.ElapsedMilliseconds}ms - it made a request");
        Assert.True(feeds.IsConfigured(FeedWebhooks.Connect));
    }

    [Fact]
    public void AnUnsetUrlIsAChoice_NotAFault()
    {
        var feeds = Build();
        feeds.Register(FeedWebhooks.Connect, null);

        Assert.False(feeds.IsConfigured(FeedWebhooks.Connect));
        Assert.Equal("not configured", feeds.Status[FeedWebhooks.Connect]);
    }

    [Fact]
    public void WhitespaceCountsAsUnset()
    {
        // An empty value in .env is how somebody disables a feed without deleting the line.
        var feeds = Build();
        feeds.Register(FeedWebhooks.Kill, "   ");

        Assert.Equal("not configured", feeds.Status[FeedWebhooks.Kill]);
    }

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("https://example.com/not-a-webhook")]
    [InlineData("discord.com/api/webhooks/1/2")]          // no scheme
    public void SomethingThatIsNotAWebhookUrlIsRejectedImmediately(string url)
    {
        /* A typo in .env is a configuration error the owner can fix right now, and saying so
           at startup costs nothing. Only the SYNTAX is checked here - whether the webhook
           actually exists cannot be known without asking Discord. */
        var feeds = Build();
        feeds.Register(FeedWebhooks.Connect, url);

        Assert.False(feeds.IsConfigured(FeedWebhooks.Connect));
        Assert.Contains("invalid URL", feeds.Status[FeedWebhooks.Connect], StringComparison.Ordinal);
    }

    [Fact]
    public void AUrlWithSurroundingWhitespaceStillWorks()
    {
        // Copying a webhook URL out of Discord picks up a trailing newline more often than not.
        var feeds = Build();
        feeds.Register(FeedWebhooks.Connect, $"  {PlausibleUrl}\n");

        Assert.True(feeds.IsConfigured(FeedWebhooks.Connect));
    }

    [Fact]
    public void EveryFeedReportsAStatus()
    {
        /* The property existed from the first day and nothing read it, so a feed that was
           not delivering looked exactly like a quiet server. */
        var feeds = Build();
        feeds.Register(FeedWebhooks.Join, PlausibleUrl);
        feeds.Register(FeedWebhooks.Connect, null);
        feeds.Register(FeedWebhooks.Kill, "nonsense");

        Assert.Equal(3, feeds.Status.Count);
        Assert.All(feeds.Status.Values, s => Assert.False(string.IsNullOrWhiteSpace(s)));

        // Three states that need three different fixes, and they must not read alike.
        Assert.NotEqual(feeds.Status[FeedWebhooks.Join], feeds.Status[FeedWebhooks.Connect]);
        Assert.NotEqual(feeds.Status[FeedWebhooks.Connect], feeds.Status[FeedWebhooks.Kill]);
    }

    [Fact]
    public async Task PostingToAnUnconfiguredFeedIsSilentAndHarmless()
    {
        // Every caller posts inside a try/catch that logs, so a throw here would turn an
        // unset webhook into a warning on every single join.
        var feeds = Build();
        feeds.Register(FeedWebhooks.Connect, null);

        Assert.Null(await Record.ExceptionAsync(() => feeds.PostAsync(FeedWebhooks.Connect, "hello")));
    }

    [Fact]
    public async Task PostingToADeadWebhookNeverThrows()
    {
        /* It cannot open, so the post is dropped - but the caller is the log ingest path and
           must not be interrupted by it. This DOES make a real request to a webhook that
           does not exist, which is the point. */
        var feeds = Build();
        feeds.Register(FeedWebhooks.Connect, PlausibleUrl);

        Assert.Null(await Record.ExceptionAsync(() => feeds.PostAsync(FeedWebhooks.Connect, "hello")));

        // ...and the failure is REPORTED rather than swallowed.
        Assert.Contains("not delivering", feeds.Status[FeedWebhooks.Connect], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeadWebhookIsNotRetriedOnEveryPost()
    {
        /* Opening one is a blocking round trip of most of a second. Retrying per join would
           add that to every connection on a busy server. */
        var feeds = Build();
        feeds.Register(FeedWebhooks.Connect, PlausibleUrl);

        await feeds.PostAsync(FeedWebhooks.Connect, "first");        // pays for the attempt

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await feeds.PostAsync(FeedWebhooks.Connect, "second");
        elapsed.Stop();

        Assert.True(elapsed.ElapsedMilliseconds < 200,
            $"the second post took {elapsed.ElapsedMilliseconds}ms - it retried instead of backing off");
    }

    [Fact]
    public void TheJoinAndConnectFeedsStayDistinct()
    {
        /* They carry different things to different channels: connect has player addresses
           on it. Collapsing them either publishes addresses or silences the public log. */
        var feeds = Build();
        feeds.Register(FeedWebhooks.Join, PlausibleUrl);
        feeds.Register(FeedWebhooks.Connect, null);

        Assert.True(feeds.IsConfigured(FeedWebhooks.Join));
        Assert.False(feeds.IsConfigured(FeedWebhooks.Connect));
    }
}
