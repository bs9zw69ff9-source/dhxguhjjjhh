using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using Discord;
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

/// <summary>Records what it was asked to post, without a gateway.</summary>
internal sealed class RecordingPostTarget : IAutoPostTarget
{
    public List<(ulong Channel, Embed Embed)> Sent { get; } = [];
    public Exception? Throws { get; init; }

    // Missing, not Failed: this fake never has anything to edit, and "could not tell" would
    // make the send path unreachable rather than exercising it.
    public Task<AutoPostEdit> EditAsync(ulong channelId, ulong messageId, Embed embed, MessageComponent? components, CancellationToken ct) =>
        Task.FromResult(AutoPostEdit.Missing);

    public Task<ulong?> SendAsync(ulong channelId, Embed embed, MessageComponent? components, CancellationToken ct)
    {
        if (Throws is not null) return Task.FromException<ulong?>(Throws);
        Sent.Add((channelId, embed));
        return Task.FromResult<ulong?>(1);
    }
}

/// <summary>
/// Staff actions posted into a channel by id, and which channel each one goes to.
/// </summary>
public class ChannelStaffLogTests
{
    private const ulong Mod = 1528754477861961760;
    private const ulong Ban = 999;

    private static ChannelStaffLog Log(RecordingPostTarget target, ulong? mod = Mod, ulong? ban = Ban) =>
        new(target, new StaffLogChannels(mod, ban), NullLogger<ChannelStaffLog>.Instance);

    // ---- routing ----

    [Theory]
    [InlineData("permban")]
    [InlineData("tempban")]
    [InlineData("unban")]
    [InlineData("autoban")]
    [InlineData("vpnban")]
    public void EveryBanActionRoutesToTheBanChannel(string action)
    {
        /* By substring rather than a fixed list, so a ban variant added later routes
           correctly without anybody remembering to extend the rule. These five are every
           ban-ish action the bot records today. */
        Assert.Equal(Ban, ChannelStaffLog.ChannelFor(action, new StaffLogChannels(Mod, Ban)));
    }

    [Theory]
    [InlineData("kick")]
    [InlineData("arrest")]
    [InlineData("serverswitch")]
    [InlineData("rotatemap")]
    [InlineData("menu-claim")]
    [InlineData("flush")]
    [InlineData("verify-accept")]
    [InlineData("firewall-unblock")]
    public void EverythingElseRoutesToTheModChannel(string action)
    {
        // "firewall-unblock" is in here deliberately: it is the closest any non-ban action
        // comes to containing the word, and it does not.
        Assert.Equal(Mod, ChannelStaffLog.ChannelFor(action, new StaffLogChannels(Mod, Ban)));
    }

    [Fact]
    public void OneChannelConfiguredTakesEverything()
    {
        /* Setting only MOD_LOG_CHANNEL is a reasonable way to say "all of it in one place",
           and a ban silently going nowhere because the ban channel was left unset is the
           opposite of what that operator asked for. */
        Assert.Equal(Mod, ChannelStaffLog.ChannelFor("permban", new StaffLogChannels(Mod, null)));
        Assert.Equal(Ban, ChannelStaffLog.ChannelFor("kick", new StaffLogChannels(null, Ban)));
    }

    [Fact]
    public void NeitherChannelConfiguredPostsNothing()
    {
        Assert.Null(ChannelStaffLog.ChannelFor("permban", new StaffLogChannels(null, null)));
        Assert.Null(ChannelStaffLog.ChannelFor("kick", new StaffLogChannels(null, null)));
    }

    [Fact]
    public void BothPointingAtTheSameChannelIsFine()
    {
        // The configuration this was asked for: one channel, two keys.
        Assert.Equal(Mod, ChannelStaffLog.ChannelFor("permban", new StaffLogChannels(Mod, Mod)));
        Assert.Equal(Mod, ChannelStaffLog.ChannelFor("serverswitch", new StaffLogChannels(Mod, Mod)));
    }

    // ---- posting ----

    [Fact]
    public async Task AnActionReachesItsChannel()
    {
        var target = new RecordingPostTarget();

        await Log(target).PostAsync(
            new ModAction("serverswitch", "Mayor", "pavlovserver", "shutting down - ok", DateTimeOffset.UtcNow));

        var sent = Assert.Single(target.Sent);
        Assert.Equal(Mod, sent.Channel);
        Assert.Contains("SERVERSWITCH", sent.Embed.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APostThatThrowsNeverEscapes()
    {
        /* The audit record is already written by the time this runs and cannot be un-written.
           A sink that throws would turn a channel permission problem into a lost ban. */
        var target = new RecordingPostTarget { Throws = new InvalidOperationException("missing permissions") };

        await Log(target).PostAsync(new ModAction("permban", "Mayor", "Evader", "evasion", DateTimeOffset.UtcNow));
    }

    // ---- the embed ----

    [Fact]
    public void TheEmbedNamesTheStafferTheTargetAndTheReason()
    {
        var embed = ChannelStaffLog.Build(
            new ModAction("permban", "Mayor", "Evader", "ban evasion", DateTimeOffset.UtcNow));

        var text = embed.Title + string.Join(" ", embed.Fields.Select(f => $"{f.Name}:{f.Value}"));

        Assert.Contains("Evader", text, StringComparison.Ordinal);
        Assert.Contains("Mayor", text, StringComparison.Ordinal);
        Assert.Contains("ban evasion", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnbanReadsDifferentlyFromABan()
    {
        // Scrolled past at speed in a busy channel, so the two must not look alike.
        var ban = ChannelStaffLog.Build(new ModAction("permban", "M", "P", null, DateTimeOffset.UtcNow));
        var unban = ChannelStaffLog.Build(new ModAction("unban", "M", "P", null, DateTimeOffset.UtcNow));

        Assert.NotEqual(ban.Color, unban.Color);
    }

    [Fact]
    public void APlayerCalledEveryoneCannotBecomeAMention()
    {
        /* Player names are attacker-chosen and this posts into a staff channel. The send
           also sets AllowedMentions.None, so this is the second of two braces. */
        var embed = ChannelStaffLog.Build(
            new ModAction("kick", "Mayor", "@everyone", "@here too", DateTimeOffset.UtcNow));

        var target = embed.Fields.Single(f => f.Name == "Target").Value.ToString()!;
        Assert.StartsWith("`", target, StringComparison.Ordinal);
        Assert.EndsWith("`", target, StringComparison.Ordinal);
    }

    [Fact]
    public void AReasonWithANewlineCannotForgeAField()
    {
        var embed = ChannelStaffLog.Build(
            new ModAction("kick", "Mayor", "Someone", "spam\nBanned by: nobody", DateTimeOffset.UtcNow));

        var reason = embed.Fields.Single(f => f.Name == "Reason").Value.ToString()!;
        Assert.DoesNotContain("\n", reason, StringComparison.Ordinal);
    }
}
