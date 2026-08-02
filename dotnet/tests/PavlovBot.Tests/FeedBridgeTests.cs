using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Logs;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The wire between the log reader and the webhooks.
/// </summary>
/// <remarks>
/// It did not exist. IpTrackingService raised Joined, Confirmed and Kill for every parsed
/// line and nothing subscribed, so the connect, join and kill feeds were complete, tested
/// and never reached by any code path. <c>Confirmed</c> stayed unsubscribed a second time
/// after the others were wired up, which is what took leaves and the connection card down.
/// </remarks>
public class FeedBridgeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-bridge-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly IpTrackingService _tracking;
    private readonly FeedWebhooks _feeds;
    private readonly ServerLabels _servers = new();
    private readonly Captured _log = new();

    private const string File = "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log";
    private const string Accept = "[2026.07.31-13.33.00:000]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777";
    private const string Login = "[2026.07.31-13.33.22:000]LogNet: Login request: ?Name=Pkdestroy userId: EOS:0002abc";
    private const string Close = "[2026.07.31-13.33.50:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777";

    public FeedBridgeTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _tracking = new IpTrackingService(_store, new MetricsRegistry(), NullLogger<IpTrackingService>.Instance);
        _feeds = new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, new MetricsRegistry());
        _servers.Assign([File]);
    }

    private FeedBridge Build() => new(_tracking, _feeds, _servers, _log);

    private Task Feed(string text) => _tracking.IngestAsync(new LogLine(File, text));

    [Fact]
    public void ConstructingTheBridgeSubscribesToTheTracker()
    {
        /* The whole defect in one assertion. The events had no subscribers, and a service
           registered but never RESOLVED is never constructed - which is why simply adding
           the class would not have been enough either. */
        Assert.Null(Record.Exception(() => Build()));

        Assert.NotNull(typeof(IpTrackingService).GetEvent(nameof(IpTrackingService.Joined)));
        Assert.NotNull(typeof(IpTrackingService).GetEvent(nameof(IpTrackingService.Confirmed)));
    }

    [Fact]
    public async Task AJoinRaisesTheFeedsWithoutThrowing()
    {
        /* No webhook is configured here, so PostAsync short-circuits. The property under
           test is that the handler RUNS - an exception in an event handler would otherwise
           surface as a failed log ingest, taking the address tracking down with the feed. */
        Build();

        await Feed(Accept);
        await Feed(Login);

        Assert.DoesNotContain("Feed post failed", _log.Lines);
    }

    [Fact]
    public async Task ADisconnectLineReachesTheLeavePath()
    {
        /* END TO END, and the one that would have caught this: a real log line goes in, and
           the leave path comes out. The previous version diffed the RCON roster instead, so
           no log line could ever produce a leave and no test noticed. */
        Build();

        await Feed(Accept);
        await Feed(Login);
        await Feed(Close);

        Assert.Contains(_log.Lines, l => l.Contains("First disconnect line seen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneConnectionProducesOneJoinLine()
    {
        /* Pavlov logs a player's login SEVERAL TIMES per connection. Without a debounce the
           join log shows the same player arriving two and three times in a row with no leave
           between - which is exactly what it was doing, and reads as the leave tracking being
           broken rather than the joins being duplicated. */
        Build();

        await Feed(Accept);
        await Feed(Login);
        await Feed("[2026.07.31-13.33.23:000]LogNet: Login request: ?Name=Pkdestroy userId: EOS:0002abc");
        await Feed("[2026.07.31-13.33.25:000]LogNet: Join request: ?Name=Pkdestroy userId: EOS:0002abc");

        Assert.DoesNotContain(_log.Lines, l => l.Contains("Feed post failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALeaveIsReportedForSomebodyOnlyEverSeenOnADisconnect()
    {
        /* Everybody already connected when the bot started is first seen on their CLOSE
           line. Their account had no name, so the leave was dropped - silently, and for
           every player online across a restart. The close line carries ?Name= when Pavlov
           writes one, and that is now taken. */
        Build();

        await Feed("[2026.07.31-13.40.00:000]LogNet: UChannel::Close: ?Name=Latecomer " +
                   "UniqueId: EOS:0009late RemoteAddr: 203.0.113.7:7777");

        Assert.Contains(_log.Lines, l => l.Contains("First disconnect line seen", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("no name recorded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANamelessDisconnectSaysWhyItWasSkipped()
    {
        // "leaves are broken" and "this account is genuinely anonymous" look identical from
        // the channel, and they are fixed completely differently.
        Build();

        await Feed(Close);

        Assert.Contains(_log.Lines, l => l.Contains("no name recorded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADisconnectWithNoKnownNameIsNotAnnounced()
    {
        /* A close line for somebody who was dropped before any login line named them. The
           feed would read "left Server 1" with nobody in front of it, and the connection
           card would have no player on it. */
        Build();

        await Feed(Close);

        Assert.DoesNotContain(_log.Lines, l => l.Contains("Feed post failed", StringComparison.Ordinal));
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
        var line = FeedWebhooks.JoinLine("Alice", "Server 1", DateTimeOffset.UtcNow);

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
           let a player forge an entire extra log line - "[..] Bob left Server 1". */
        var line = FeedWebhooks.JoinLine("Alice\n[12:00:00] Bob left Server 1", "Server 1", DateTimeOffset.UtcNow);

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Keeps what was logged, so "the handler ran" is observable without a webhook.</summary>
    private sealed class Captured : ILogger<FeedBridge>, IDisposable
    {
        public List<string> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => this;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));

        public void Dispose() { }
    }
}
