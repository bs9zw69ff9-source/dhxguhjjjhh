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
    /* TWO SECONDS AFTER THE ACCEPT, which is what a real handshake looks like and what the
       correlator's ten-second window is sized for. A login further out than that correlates
       to nothing, which is a case of its own below. */
    private const string Login = "[2026.07.31-13.33.02:000]LogNet: Login request: ?Name=Pkdestroy userId: EOS:0002abc";

    /// <summary>Past the correlator's window, so the address for it is unknown.</summary>
    private const string LateLogin = "[2026.07.31-13.33.45:000]LogNet: Login request: ?Name=Pkdestroy userId: EOS:0002abc";
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

    /// <summary>A second accept before the login, which makes the correlation ambiguous.</summary>
    private const string OtherAccept =
        "[2026.07.31-13.33.01:000]LogNet: NotifyAcceptingConnection accepted from: 198.51.100.9:7777";

    private async Task Join()
    {
        await _tracking.IngestAsync(new LogLine(File, Accept)).ConfigureAwait(false);
        await _tracking.IngestAsync(new LogLine(File, Login)).ConfigureAwait(false);
    }

    private Task Leave() => _tracking.IngestAsync(new LogLine(File, Close));

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

    // ---- the join line ----

    [Fact]
    public async Task AFlaggedAddressIsBannedOnTheJoinLineBeforeAnyDisconnect()
    {
        /* THE POINT OF SCREENING AT JOIN. Acting only on the disconnect meant the ban landed
           after the player had already had their whole session - correct, and far too late to
           be the thing it was for. No close line is fed here at all. */
        Build(flagged: true);

        await Join();

        var ban = Assert.Single(Bans());
        Assert.Equal(Name, ban.PlayerId);
        Assert.Equal(Account, ban.UniqueId);
    }

    [Fact]
    public async Task AnAmbiguousCorrelationIsNotActedOnAtJoinButStillIsAtDisconnect()
    {
        /* THE LINE THAT MUST NOT BE CROSSED. Two addresses were accepted before this login, so
           the correlator cannot say which one joined - and banning on a guess bans whoever
           else happened to be connecting at that moment.

           The disconnect line carries both together and is certain, so the backstop is what
           catches this connection. That is exactly why the disconnect path stays. */
        Build(flagged: true);

        await _tracking.IngestAsync(new LogLine(File, Accept));
        await _tracking.IngestAsync(new LogLine(File, OtherAccept));
        await _tracking.IngestAsync(new LogLine(File, Login));

        Assert.Empty(Bans());

        await Leave();

        Assert.Single(Bans());
    }

    [Fact]
    public async Task AWholeConnectionProducesOneBanRatherThanTwo()
    {
        // Both lines now screen. The responder's per-address window is what stops the
        // disconnect re-banning what the join already did.
        Build(flagged: true);

        await Connect();

        Assert.Single(Bans());
    }

    [Fact]
    public async Task ALoginTooLongAfterTheAcceptIsLeftToTheDisconnect()
    {
        /* The correlator only matches a login to an accept within ten seconds. Past that
           there is no address to screen, so there is nothing to act on until the disconnect
           line carries one - the same fallback as an ambiguous correlation. */
        Build(flagged: true);

        await _tracking.IngestAsync(new LogLine(File, Accept));
        await _tracking.IngestAsync(new LogLine(File, LateLogin));

        Assert.Empty(Bans());

        await Leave();

        Assert.Single(Bans());
    }

    [Fact]
    public async Task ANamelessJoinIsLeftToTheDisconnect()
    {
        /* Master protection is BY NAME, so a login line with no name cannot be acted on here
           without walking past the check that stops an owner locking themselves out. The
           disconnect line names them, and handles it. */
        Build(flagged: true);

        await _tracking.IngestAsync(new LogLine(File, Accept));
        await _tracking.IngestAsync(new LogLine(File,
            "[2026.07.31-13.33.02:000]LogNet: Login request: userId: EOS:0002abc"));

        Assert.Empty(Bans());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is not a failure */ }
    }
}
