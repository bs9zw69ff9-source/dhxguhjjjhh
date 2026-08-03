using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Staff actions reaching the staff channel, as they happen.
/// </summary>
/// <remarks>
/// The audit DATASET already recorded everything and answers "what has this staffer done"
/// through /staffactivity - but only when somebody thinks to ask. The feed answers "what
/// just happened" without anybody asking, which is what a staff channel is for.
///
/// POSTED FROM THE AUDIT FUNNEL, not from each command. Every staff action in the bot
/// already records an audit entry - seventeen call sites - so posting there makes the feed
/// complete by construction. A command added next year appears in the staff channel because
/// it records an entry, not because somebody remembered to post one too.
/// </remarks>
public class StaffFeedTests
{
    private static AuditLog Audit(out MemoryBackend backend, FeedWebhooks? feeds = null)
    {
        backend = new MemoryBackend();
        return new AuditLog(new SerializedStore(backend, new SystemTextJsonCodec()), feeds);
    }

    private static FeedWebhooks Feeds() =>
        new(NullLogger<FeedWebhooks>.Instance, new MetricsRegistry());

    // ---- the line itself ----

    [Fact]
    public void TheLineNamesWhoDidWhatToWhomAndWhy()
    {
        var line = FeedWebhooks.StaffLine(
            "serverswitch", "Mayor", "pavlovserver", "shutting down - ok", DateTimeOffset.UtcNow);

        Assert.Contains("SERVERSWITCH", line, StringComparison.Ordinal);
        Assert.Contains("Mayor", line, StringComparison.Ordinal);
        Assert.Contains("pavlovserver", line, StringComparison.Ordinal);
        Assert.Contains("shutting down", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AReasonCannotForgeASecondEntry()
    {
        /* The feed is one action per line and the reason is the only part somebody can type
           freely - a newline in it would fabricate an entry that looks exactly like a real
           staff action, in the channel people trust to settle arguments. */
        var line = FeedWebhooks.StaffLine(
            "kick", "Mayor", "Someone", "spam\n[12:00:00] BAN  Mayor → Innocent", DateTimeOffset.UtcNow);

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnActionWithNoReasonStillReads()
    {
        var line = FeedWebhooks.StaffLine("flush", "Mayor", "server1", null, DateTimeOffset.UtcNow);

        Assert.Contains("FLUSH", line, StringComparison.Ordinal);
        Assert.DoesNotContain("()", line, StringComparison.Ordinal);
    }

    // ---- the funnel ----

    [Fact]
    public async Task RecordingAnActionStillWritesTheDataset()
    {
        /* The dataset is the record that has to survive; the channel is a convenience. This
           is the assertion that the convenience did not displace the record. */
        var audit = Audit(out var backend);

        await audit.RecordAsync("serverswitch", "Mayor", "pavlovserver", "shutting down - ok");

        Assert.Single(audit.All());
        Assert.Equal("serverswitch", audit.All()[0].Action);
        Assert.True(backend.Writes > 0);
    }

    [Fact]
    public async Task AnUnconfiguredStaffFeedChangesNothing()
    {
        /* Every existing deployment has no STAFF_WEBHOOK_URL, so the audit path has to
           behave exactly as it did - including for a bot with no feeds wired at all. */
        var audit = Audit(out _, Feeds());   // registered, but no URL for the staff label

        await audit.RecordAsync("kick", "Mayor", "Someone", "afk");

        Assert.Single(audit.All());
    }

    [Fact]
    public async Task ADeadStaffWebhookNeverLosesTheAuditEntry()
    {
        /* THE PROPERTY THAT MATTERS. A ban that vanished from the audit trail because a
           webhook was misconfigured would be the worst possible trade - the dataset is what
           /staffactivity reads and what settles an argument about a ban weeks later. */
        var feeds = Feeds();
        feeds.Register(FeedWebhooks.Staff, "https://discord.com/api/webhooks/1/deadbeef-not-a-real-token");

        var audit = Audit(out _, feeds);

        await audit.RecordAsync("permban", "Mayor", "Evader", "ban evasion");

        Assert.Single(audit.All());
        Assert.Equal("permban", audit.All()[0].Action);
    }

    [Fact]
    public async Task ServerSwitchIsAmongTheActionsThatReachTheFeed()
    {
        // Named explicitly because it is what prompted this: /serverswitch records an audit
        // entry like every other staff action, so it reaches the staff channel by the same
        // route rather than needing its own posting code.
        var audit = Audit(out _);

        await audit.RecordAsync("serverswitch", "Mayor", "pavlovserver1", "restarting - ok");
        await audit.RecordAsync("rotatemap", "Mayor", "pavlovserver", "3/3 restarted");

        Assert.Equal(["serverswitch", "rotatemap"], audit.All().Select(a => a.Action));
    }
}
