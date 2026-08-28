using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Logs;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Vpn;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using PavlovBot.Host.Vpn;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// That a VPN verdict reaches the ban, from a real log line, with nothing else configured.
/// </summary>
/// <remarks>
/// THE BUG: the screening and the auto-ban both sat BELOW an early return for an unset
/// CONNECT_WEBHOOK_URL. A Discord webhook - a display setting, whose own warning said only
/// the leave lines were unaffected - silently switched VPN auto-ban off entirely. Every
/// existing test for this subsystem called the responder directly, so the whole feature could
/// be unreachable in the deployment that needed it and the suite stayed green.
///
/// So these drive the actual handler through IpTrackingService with NO webhook configured,
/// and assert on the ban record that comes out the far end.
/// </remarks>
public class VpnAutoBanWiringTests : IDisposable
{
    private const string File = "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log";
    private const string Accept = "[2026.07.31-13.33.00:000]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777";
    private const string Login = "[2026.07.31-13.33.22:000]LogNet: Login request: ?Name=Pkdestroy userId: EOS:0002abc";
    private const string Close = "[2026.07.31-13.33.50:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777";

    private const string Name = "Pkdestroy";
    // The tracker strips the "EOS:" prefix; this is the id the ban has to carry.
    private const string Account = "0002abc";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-vpnban-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly IpTrackingService _tracking;
    private readonly FeedWebhooks _feeds;
    private readonly ServerLabels _servers = new();

    public VpnAutoBanWiringTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _tracking = new IpTrackingService(_store, new MetricsRegistry(), NullLogger<IpTrackingService>.Instance);

        // NOTHING CONFIGURED. This is the deployment the bug hid in.
        _feeds = new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, new MetricsRegistry());
        _servers.Assign([File]);
    }

    /// <summary>A detector that answers immediately, either way, with no network.</summary>
    private sealed class FakeDetector(string name, bool flagged) : IVpnDetector
    {
        public string Name => name;
        public int Tier => 1;
        public bool Enabled => true;

        public Task<DetectorOutcome> LookupAsync(string ip, CancellationToken ct) =>
            Task.FromResult(DetectorOutcome.Answered(new DetectorReading
            {
                Name = name,
                Tier = 1,
                Flagged = flagged,
                Vpn = flagged ? true : null,
                Detail = flagged ? "vpn" : "clean",
            }));
    }

    private sealed class NoMasters : IMasterNames
    {
        public bool IsMaster(string name) => false;
        public bool IsExempt(string name) => false;
        public Task ExemptAsync(string name, TimeSpan? duration = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <param name="flagged">Whether both detectors call the address a VPN.</param>
    private FeedBridge Build(bool flagged)
    {
        var metrics = new MetricsRegistry();

        /* TWO detectors, because VpnThresholds.BanMin defaults to 2 - one provider is
           deliberately never enough to ban. */
        var vpn = new VpnScreeningService(
            [new FakeDetector("alpha", flagged), new FakeDetector("beta", flagged)],
            _store, new StubGeoLocator(null), metrics, NullLogger<VpnScreeningService>.Instance);

        // No RCON servers: the ban record is what is under test, not the wire.
        var rcon = new RconRegistry(
            new BotOptions
            {
                DiscordToken = "t",
                Servers = [],
                Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
                DataDirectory = _directory,
            },
            metrics, NullLogger<RconRegistry>.Instance);

        var masters = new NoMasters();
        var bans = new BanService(rcon, _store, masters, NullLogger<BanService>.Instance);
        var responder = new VpnResponder(
            bans, masters, _store, new AuditLog(_store), _feeds, metrics, NullLogger<VpnResponder>.Instance);

        return new FeedBridge(_tracking, _feeds, _servers, NullLogger<FeedBridge>.Instance, vpn, masters, responder);
    }

    private async Task Connect()
    {
        await _tracking.IngestAsync(new LogLine(File, Accept)).ConfigureAwait(false);
        await _tracking.IngestAsync(new LogLine(File, Login)).ConfigureAwait(false);
        await _tracking.IngestAsync(new LogLine(File, Close)).ConfigureAwait(false);
    }

    private List<BanRecord> Bans() => _store.Read<List<BanRecord>>(Datasets.TempBans, []);

    [Fact]
    public async Task AFlaggedAddressIsBannedWithNoConnectWebhookConfigured()
    {
        /* THE REGRESSION, in one assertion. CONNECT_WEBHOOK_URL is a place to POST a card.
           It is not, and must never again be, the switch that decides whether a moderation
           action happens at all. */
        Build(flagged: true);

        await Connect();

        var ban = Assert.Single(Bans());
        Assert.Equal(Name, ban.PlayerId);
        Assert.True(ban.Permanent);
        Assert.Equal("auto", ban.Moderator);
    }

    [Fact]
    public async Task TheBanCarriesTheAccountIdFromTheDisconnectLine()
    {
        /* The id is on the same line as the address, so recording null and re-deriving it
           from the display name - at enforce time and again at lift time - was guessing with
           the answer in hand. Two accounts that have used one name resolve to whichever the
           tracker saw last, and a ban and its lift then name different things. */
        Build(flagged: true);

        await Connect();

        Assert.Equal(Account, Assert.Single(Bans()).UniqueId);
    }

    [Fact]
    public async Task ACleanAddressIsNotBanned()
    {
        // The control. Without it, a responder that banned unconditionally would pass above.
        Build(flagged: false);

        await Connect();

        Assert.Empty(Bans());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is not a failure */ }
    }
}
