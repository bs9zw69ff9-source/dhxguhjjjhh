using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;
using Xunit;
using BanRecord = PavlovBot.Core.Moderation.BanRecord;

namespace PavlovBot.Tests;

/// <summary>
/// <c>/configure</c> blacklisting, from the owner's entry to the commands the game server receives.
/// </summary>
/// <remarks>
/// EACH LINK WAS ALREADY TESTED ON ITS OWN - the entry is written, a log line raises a flagged
/// join, a flagged join writes a ban record - and none of that says whether anybody is actually
/// removed. These run the real pieces together, feed them the log lines Pavlov writes, and assert
/// against <see cref="FakeRconServer"/>, which records the exact commands on the wire.
/// </remarks>
public sealed class BlacklistEndToEndTests : IAsyncDisposable
{
    private const string Log = "server1.log";
    private const string Address = "203.0.113.5";

    private const string Accept = "[2026.09.29-12.00.00:000]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777";
    private const string OtherAccept = "[2026.09.29-12.00.00:200]LogNet: NotifyAcceptingConnection accepted from: 198.51.100.8:7777";
    private const string Login = "[2026.09.29-12.00.01:000]LogNet: Login request: ?Name=Evader userId: EOS:0002abc";
    private const string Close = "[2026.09.29-12.40.00:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-bl-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRconServer _server = new();
    private readonly SerializedStore _store;
    private readonly RconRegistry _rcon;
    private readonly IpTrackingService _tracking;
    private readonly OwnerActions _actions;

    public BlacklistEndToEndTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        var metrics = new MetricsRegistry();

        _rcon = new RconRegistry(new BotOptions
        {
            DiscordToken = "t",
            Servers =
            [
                new RconOptions
                {
                    Name = "server1",
                    Host = "127.0.0.1",
                    Port = _server.Port,
                    Password = _server.Password,
                    CommandSpacing = TimeSpan.Zero,
                    ReadCacheDuration = TimeSpan.Zero,
                    MaxAttempts = 1,
                },
            ],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        }, metrics, NullLogger<RconRegistry>.Instance);

        var masters = new MasterNames(["OwnerAlt"], _store);
        _tracking = new IpTrackingService(_store, metrics, NullLogger<IpTrackingService>.Instance);
        var bans = new BanService(_rcon, _store, masters, NullLogger<BanService>.Instance);

        // Constructing it is what subscribes it - the same as the host does at startup.
        _ = new EvasionResponder(
            _tracking, bans, masters, _store, new AuditLog(_store),
            new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, metrics),
            metrics, NullLogger<EvasionResponder>.Instance);

        _actions = new OwnerActions(_store, _tracking);
    }

    private async Task FeedAsync(params string[] lines)
    {
        foreach (var line in lines) await _tracking.IngestAsync(new LogLine(Log, line));
    }

    private BanRecord TheBan() =>
        Assert.Single(_store.Read<List<BanRecord>>(Datasets.TempBans, []));

    private void AssertRemovedOnTheWire()
    {
        Assert.Contains("Ban Evader", _server.Commands);
        Assert.Contains("Kick Evader", _server.Commands);
    }

    [Fact]
    public async Task ABlacklistedAddressIsBannedWhenItJoins()
    {
        Assert.True((await _actions.BlacklistAsync(Address)).Ok);

        // A quiet server: one connection pending, so the join's address is certain enough to act on.
        await FeedAsync(Accept, Login);

        AssertRemovedOnTheWire();
        var ban = TheBan();
        Assert.Equal("Evader", ban.PlayerId);
        Assert.True(ban.Permanent);
        Assert.Contains($"blacklisted ip {Address}", ban.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnABusyServerABlacklistedAddressIsBannedOnTheLineThatConfirmsIt()
    {
        /* Two connections at once: the join cannot say which address is whose, so nothing is
           done at login. The close line carries the account and the address together - that is
           where the block lands. (With FIREWALL_BLACKLIST on, ufw has refused them before any of
           this; this is the bot-level half.) */
        await _actions.BlacklistAsync(Address);

        await FeedAsync(Accept, OtherAccept, Login);
        Assert.DoesNotContain("Ban Evader", _server.Commands);

        await FeedAsync(Close);

        AssertRemovedOnTheWire();
        Assert.Equal("Evader", TheBan().PlayerId);
    }

    [Fact]
    public async Task ABlacklistedUsernameIsBannedWhenItJoins()
    {
        Assert.True((await _actions.BlacklistAsync("Evader")).Ok);

        await FeedAsync(Login);

        AssertRemovedOnTheWire();
        Assert.Contains("blacklisted username Evader", TheBan().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomebodyNotOnTheBlacklistIsLeftAlone()
    {
        await _actions.BlacklistAsync("198.51.100.99");
        await _actions.BlacklistAsync("SomebodyElse");

        await FeedAsync(Accept, Login, Close);

        Assert.DoesNotContain(_server.Commands, c => c.StartsWith("Ban ", StringComparison.Ordinal));
        Assert.Empty(_store.Read<List<BanRecord>>(Datasets.TempBans, []));
    }

    public async ValueTask DisposeAsync()
    {
        await _rcon.DisposeAsync();
        await _server.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
