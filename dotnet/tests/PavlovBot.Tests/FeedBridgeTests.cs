using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Logs;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The wire between the log reader and the webhooks.
/// </summary>
/// <remarks>
/// It did not exist. IpTrackingService raised Joined and Kill for every parsed line and
/// nothing subscribed, so the connect, join and kill feeds were complete, tested and never
/// reached by any code path.
/// </remarks>
public class FeedBridgeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-bridge-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly IpTrackingService _tracking;
    private readonly FeedWebhooks _feeds;
    private readonly RconRegistry _rcon;

    public FeedBridgeTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _tracking = new IpTrackingService(_store, new MetricsRegistry(), NullLogger<IpTrackingService>.Instance);
        _feeds = new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, new MetricsRegistry());
        _rcon = new RconRegistry(
            new BotOptions
            {
                DiscordToken = "t",
                Servers = [new RconOptions { Name = "server1", Host = "127.0.0.1", Port = 9100, Password = "x" }],
                Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
                DataDirectory = _directory,
            },
            new MetricsRegistry(), NullLogger<RconRegistry>.Instance);
    }

    private FeedBridge Build() =>
        new(_tracking, _feeds, _rcon, NullLogger<FeedBridge>.Instance);

    [Fact]
    public void ConstructingTheBridgeSubscribesToTheTracker()
    {
        /* The whole defect in one assertion. The events had no subscribers, and a service
           registered but never RESOLVED is never constructed - which is why simply adding
           the class would not have been enough either. */
        Assert.Null(Record.Exception(() => Build()));

        var joined = typeof(IpTrackingService).GetEvent(nameof(IpTrackingService.Joined));
        Assert.NotNull(joined);
    }

    [Fact]
    public async Task AJoinRaisesTheFeedsWithoutThrowing()
    {
        /* No webhook is configured here, so PostAsync short-circuits. The property under
           test is that the handler RUNS - an exception in an event handler would otherwise
           surface as a failed log ingest, taking the address tracking down with the feed. */
        Build();

        await _tracking.IngestAsync(new LogLine("Pavlov.log",
            "[2026.07.31-20.00.00:000][  0]LogNet: Join succeeded: Alice"));
    }

    [Fact]
    public async Task NobodyLeavesOnTheFirstSweep()
    {
        /* On startup everyone already online would otherwise be announced as leaving the
           moment the second sweep ran - a wall of noise that says nothing true. */
        var bridge = Build();

        await bridge.TickRosterAsync();
        await bridge.TickRosterAsync();
    }

    [Fact]
    public async Task AnEmptyRosterFromAnUnreachableServerIsNotEverybodyLeaving()
    {
        /* An empty server and an unreachable one look identical from here. The roster has
           never been fetched in this test, so the sweep must decline to conclude anything -
           announcing that the whole server left because RCON blipped is worse than silence. */
        var bridge = Build();

        await bridge.TickRosterAsync();

        Assert.Equal(DateTimeOffset.MinValue, _rcon.Roster("server1").TakenAt);
    }

    [Fact]
    public void TheConnectAndJoinFeedsAreDistinctLabels()
    {
        /* They point at different channels with different privacy rules: connect carries
           addresses and belongs somewhere private, join is the public log. The port read
           CONNECT_WEBHOOK_URL into the join feed and never read JOIN_WEBHOOK_URL at all. */
        Assert.NotEqual(FeedWebhooks.Join, FeedWebhooks.Connect);
    }

    [Fact]
    public void ThePublicJoinLineCarriesNoAddress()
    {
        // The point of the split. If an address can reach the public feed, the split is
        // decorative.
        var line = FeedWebhooks.JoinLine("Alice", DateTimeOffset.UtcNow);

        Assert.Contains("Alice", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ip=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConnectLineCarriesTheAddress()
    {
        var line = FeedWebhooks.ConnectLine("Alice", "203.0.113.9", "Toronto, Canada", "CLEAN", DateTimeOffset.UtcNow);

        Assert.Contains("ip=203.0.113.9", line, StringComparison.Ordinal);
        Assert.Contains("Toronto", line, StringComparison.Ordinal);
        Assert.Contains("vpn=CLEAN", line, StringComparison.Ordinal);
    }

    [Fact]
    public void APlayerNameCannotInjectALineBreakIntoAFeed()
    {
        /* Names are attacker-controlled and these feeds are plain text, so a newline would
           let a player forge an entire extra log line - "[..] LEAVE somebodyelse". */
        var line = FeedWebhooks.JoinLine("Alice\n[12:00:00] LEAVE Bob", DateTimeOffset.UtcNow);

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
