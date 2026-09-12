using Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Evasion;
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
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The security alerts: evasion, alt accounts and VPNs, delivered to somebody's DMs.
/// </summary>
/// <remarks>
/// TWO THINGS ARE PINNED HERE, AND THE SECOND IS THE POINT OF THE FEATURE.
///
/// That every outcome alerts, INCLUDING THE ONES WHERE NOTHING WAS DONE. "We caught an
/// evader and banned him" and "we caught an evader and let him in because of a protection"
/// are both things the owner wants to hear, and only the first is visible from the ban list
/// afterwards.
///
/// And that a failed delivery is REPORTED. A DM to somebody with closed DMs is accepted by
/// Discord and never delivered, so the sending side looks perfect while nobody is being
/// told anything - which is indistinguishable from no detections happening.
/// </remarks>
public class SecurityAlertTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pavlovbot-alerts-" + Guid.NewGuid().ToString("N"));

    private readonly SerializedStore _store;
    private readonly MetricsRegistry _metrics = new();
    private readonly RecordingSink _sink = new();
    private readonly SecurityAlerts _alerts;

    public SecurityAlertTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _alerts = new SecurityAlerts(NullLogger<SecurityAlerts>.Instance);
        _alerts.UseSink(_sink);
    }

    // ---- doubles ----

    private sealed class RecordingSink : ISecurityAlertSink
    {
        public List<SecurityAlert> Alerts { get; } = [];

        public Task<IReadOnlyList<AlertDelivery>> PostAsync(SecurityAlert alert, CancellationToken ct = default)
        {
            Alerts.Add(alert);
            return Task.FromResult<IReadOnlyList<AlertDelivery>>([new AlertDelivery(1, true)]);
        }
    }

    private sealed class ThrowingSink : ISecurityAlertSink
    {
        public Task<IReadOnlyList<AlertDelivery>> PostAsync(SecurityAlert alert, CancellationToken ct = default) =>
            throw new InvalidOperationException("the gateway is on fire");
    }

    /// <param name="delivers">What Discord does with the DM. False is a closed inbox.</param>
    private sealed class FakeDirectory(bool delivers) : IGuildDirectory
    {
        public List<ulong> Attempted { get; } = [];

        public Task<IReadOnlyList<GuildInvite>> InviteToEveryGuildAsync(TimeSpan maxAge, int maxUses, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GuildInvite>>([]);

        public Task<bool> SendDirectMessageAsync(ulong userId, Embed embed, CancellationToken ct)
        {
            Attempted.Add(userId);
            return Task.FromResult(delivers);
        }
    }

    private sealed class NoMasters : IMasterNames
    {
        public bool IsMaster(string name) => false;
        public bool IsExempt(string name) => false;
        public bool IsProtected(string name) => Protected.Contains(name);
        public HashSet<string> Protected { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task ExemptAsync(string name, TimeSpan? duration = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private RconRegistry Rcon() => new(
        new BotOptions
        {
            DiscordToken = "t",
            // Port 1: nothing can reach a server, and none of these assertions are about RCON.
            Servers = [new RconOptions { Name = "server1", Host = "127.0.0.1", Port = 1, Password = "x" }],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        }, _metrics, NullLogger<RconRegistry>.Instance);

    // ---- evasion ----

    private EvasionResponder Evasion(NoMasters masters)
    {
        var tracking = new IpTrackingService(_store, _metrics, NullLogger<IpTrackingService>.Instance);
        return new EvasionResponder(
            tracking, new BanService(Rcon(), _store, masters, NullLogger<BanService>.Instance),
            masters, _store, new AuditLog(_store),
            new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, _metrics),
            _metrics, NullLogger<EvasionResponder>.Instance, _alerts);
    }

    private static FlaggedJoin Join(string name = "Evader") =>
        new("0002913af4f445df86da1be6a2a01728", name, "203.0.113.9",
            new FlagVerdict(FlagMatch.Ip, "blacklisted ip 203.0.113.9"), DateTimeOffset.UtcNow);

    [Fact]
    public async Task AnEvasionCatchIsAlerted()
    {
        await Evasion(new NoMasters()).RespondAsync(Join(), CancellationToken.None);

        var alert = Assert.Single(_sink.Alerts);
        Assert.Equal(SecurityAlertKind.Evasion, alert.Kind);
        Assert.Equal("Evader", alert.Player);
        Assert.Contains("blacklisted ip 203.0.113.9", alert.Headline, StringComparison.Ordinal);
        Assert.Contains("Banned", alert.Action, StringComparison.Ordinal);
        Assert.Equal("203.0.113.9", alert.Ip);
    }

    [Fact]
    public async Task AnEvasionTHATWASREFUSEDIsAlertedToo()
    {
        /* THE ONE WORTH HAVING. A protected account matching an evasion flag means either a
           stale flag on somebody's own address or a player the bot will never stop - and
           nothing else in the system will tell anybody it happened. */
        var masters = new NoMasters();
        masters.Protected.Add("Evader");

        var outcome = await Evasion(masters).RespondAsync(Join(), CancellationToken.None);

        Assert.Equal(AutoBanOutcome.Protected, outcome);
        var alert = Assert.Single(_sink.Alerts);
        Assert.Contains("never-ban list", alert.Action, StringComparison.Ordinal);
        Assert.Contains("Nothing was done", alert.Action, StringComparison.Ordinal);
    }

    // ---- vpn ----

    private VpnResponder Vpn(bool autoBan = true)
    {
        var masters = new NoMasters();
        return new VpnResponder(
            new BanService(Rcon(), _store, masters, NullLogger<BanService>.Instance),
            masters, _store, new AuditLog(_store),
            new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, _metrics),
            _metrics, NullLogger<VpnResponder>.Instance, autoBan, _alerts);
    }

    private static VpnRecord Actionable() => new()
    {
        Ip = "45.10.20.30",
        Organization = "M247 Europe",
        Decision = new VpnDecision(Flagged: true, Confirmed: true, Actionable: true,
            "confirmation agreed (1/1); 2 of 3 detector(s) flagged it"),
        ScreenHits = 2,
        ScreenAnswered = 3,
    };

    private static VpnRecord BelowThreshold() => new()
    {
        Ip = "45.10.20.31",
        Decision = new VpnDecision(Flagged: true, Confirmed: null, Actionable: false,
            "1 of 3 detector(s) flagged it, 2 required"),
        ScreenHits = 1,
        ScreenAnswered = 3,
    };

    [Fact]
    public async Task AVpnBanIsAlerted()
    {
        await Vpn().RespondAsync("VpnUser", Actionable(), "0002913af4f445df86da1be6a2a01728", "join");

        var alert = Assert.Single(_sink.Alerts);
        Assert.Equal(SecurityAlertKind.Vpn, alert.Kind);
        Assert.Contains("M247 Europe", alert.Headline, StringComparison.Ordinal);
        Assert.Contains("Banned", alert.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVpnThatWasNotActedOnIsAlertedWithTheReason()
    {
        // The branch somebody asks about: "why is that obvious VPN still on the server".
        await Vpn().RespondAsync("VpnUser", BelowThreshold(), stage: "join");

        var alert = Assert.Single(_sink.Alerts);
        Assert.Contains("Not banned", alert.Action, StringComparison.Ordinal);
        Assert.Contains("under the threshold", alert.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneConnectionIsOneMessageEvenThoughItIsScreenedTwice()
    {
        /* The join line and the disconnect line both reach the responder for one connection.
           Two identical DMs per player is how an alert becomes something nobody reads. */
        var responder = Vpn();
        var record = BelowThreshold();

        await responder.RespondAsync("VpnUser", record, stage: "join");
        await responder.RespondAsync("VpnUser", record, stage: "disconnect");

        Assert.Single(_sink.Alerts);
    }

    // ---- alt accounts ----

    private const string LogFile = "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log";

    /// <summary>One whole connection for one account, on a given address.</summary>
    private static string[] Connection(string time, string account, string name, string ip) =>
    [
        $"[2026.07.31-{time}:000]LogNet: NotifyAcceptingConnection accepted from: {ip}:7777",
        $"[2026.07.31-{time}:100]LogNet: Login request: ?Name={name} userId: EOS:{account}",
        $"[2026.07.31-{time}:900]LogNet: UChannel::Close: UniqueId: EOS:{account} RemoteAddr: {ip}:7777",
    ];

    private async Task<IpTrackingService> TwoAccountsOnOneAddressAsync(params string[][] connections)
    {
        var tracking = new IpTrackingService(_store, _metrics, NullLogger<IpTrackingService>.Instance);
        var servers = new ServerLabels();
        servers.Assign([LogFile]);

        // The bridge subscribes in its constructor, which is the only way these fire.
        _ = new FeedBridge(tracking, new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, _metrics),
            servers, NullLogger<FeedBridge>.Instance, alerts: _alerts);

        foreach (var line in connections.SelectMany(c => c))
            await tracking.IngestAsync(new LogLine(LogFile, line));

        return tracking;
    }

    [Fact]
    public async Task TwoAccountsOnOneAddressAreAlerted()
    {
        await TwoAccountsOnOneAddressAsync(
            Connection("13.33.00", "0002aaa", "FirstAccount", "203.0.113.5"),
            Connection("13.40.00", "0002bbb", "SecondAccount", "203.0.113.5"));

        var alert = Assert.Single(_sink.Alerts, a => a.Kind == SecurityAlertKind.Alts);

        Assert.Equal("SecondAccount", alert.Player);
        Assert.Contains("FirstAccount", alert.Alts!);

        /* NOT A BAN, and the alert says so. Two people in a house share an address, and so
           does everybody behind one carrier NAT - the judgement is a human's. */
        Assert.Contains("Nothing", alert.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameAltPairIsNotReportedEveryTimeTheyReconnect()
    {
        /* Linked accounts do not change between connections, so the second alert says
           nothing the first did not - and a DM per reconnect is how an alert becomes noise
           somebody mutes. */
        await TwoAccountsOnOneAddressAsync(
            Connection("13.33.00", "0002aaa", "FirstAccount", "203.0.113.5"),
            Connection("13.40.00", "0002bbb", "SecondAccount", "203.0.113.5"),
            // Well past the twenty-second connection debounce, well inside the alert's day.
            Connection("18.05.00", "0002bbb", "SecondAccount", "203.0.113.5"));

        Assert.Single(_sink.Alerts, a => a.Kind == SecurityAlertKind.Alts);
    }

    [Fact]
    public async Task OneAccountOnItsOwnAddressIsNotAnAlt()
    {
        // The control. Alerting on every ordinary player would bury the ones that matter.
        await TwoAccountsOnOneAddressAsync(Connection("13.33.00", "0002aaa", "Solo", "203.0.113.5"));

        Assert.DoesNotContain(_sink.Alerts, a => a.Kind == SecurityAlertKind.Alts);
    }

    // ---- delivery ----

    [Fact]
    public async Task EveryRecipientIsTriedAndAClosedInboxIsReported()
    {
        var directory = new FakeDirectory(delivers: false);
        var sink = new DirectMessageAlerts(directory, [11, 22], _metrics,
            NullLogger<DirectMessageAlerts>.Instance);

        var results = await sink.PostAsync(Sample());

        Assert.Equal([11ul, 22ul], directory.Attempted);
        Assert.All(results, r => Assert.False(r.Delivered));
        // NOT silence: the reason is carried so /testalert can print it.
        Assert.All(results, r => Assert.Contains("DMs are probably closed", r.Problem!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeliveredAlertSaysSo()
    {
        var sink = new DirectMessageAlerts(new FakeDirectory(delivers: true), [11], _metrics,
            NullLogger<DirectMessageAlerts>.Instance);

        var delivery = Assert.Single(await sink.PostAsync(Sample()));

        Assert.True(delivery.Delivered);
        Assert.Null(delivery.Problem);
    }

    [Fact]
    public async Task WithNobodyConfiguredNothingIsSentAndNothingThrows()
    {
        var directory = new FakeDirectory(delivers: true);
        var sink = new DirectMessageAlerts(directory, [], _metrics, NullLogger<DirectMessageAlerts>.Instance);

        Assert.Empty(await sink.PostAsync(Sample()));
        Assert.Empty(directory.Attempted);
    }

    [Fact]
    public async Task ASinkThatThrowsCannotCostTheBanTheCallerIsIssuing()
    {
        /* Every caller is part-way through a moderation decision it has already committed
           to. A failed direct message must not unwind it. */
        var alerts = new SecurityAlerts(NullLogger<SecurityAlerts>.Instance);
        alerts.UseSink(new ThrowingSink());

        Assert.Empty(await alerts.PostAsync(Sample()));
    }

    [Fact]
    public void TheCardLeadsWithWhatWasDoneAboutIt()
    {
        var card = DirectMessageAlerts.Card(Sample());

        Assert.Contains("Evader", card.Title, StringComparison.Ordinal);
        var first = card.Fields[0];
        Assert.Equal("What happened", first.Name);
        Assert.Contains("Banned permanently", first.Value, StringComparison.Ordinal);

        // The alts are named, not counted - the point of the alert is which accounts.
        Assert.Contains(card.Fields, f => f.Value.Contains("SecondAccount", StringComparison.Ordinal));
    }

    private static SecurityAlert Sample() => new(
        SecurityAlertKind.Evasion,
        "Evader",
        "Their join matched blacklisted ip 203.0.113.9 (left by a previous ban).",
        "Banned permanently, on every server.",
        AccountId: "0002913af4f445df86da1be6a2a01728",
        Ip: "203.0.113.9",
        Server: "server1",
        Alts: ["SecondAccount"],
        At: DateTimeOffset.UnixEpoch.AddYears(56));

    // ---- who gets them ----

    private static FeatureOptions Bind(params (string Key, string Value)[] settings) =>
        FeatureOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [Fact]
    public void TheExplicitListWins()
    {
        var features = Bind(("SECURITY_DM_IDS", "307052224087851009"), ("OWNER_IDS", "1,2"));

        Assert.Equal([307052224087851009ul], features.SecurityAlertRecipients);
    }

    [Fact]
    public void WithNoExplicitListTheOwnersGetThem()
    {
        /* The alternative default is "nobody", which is indistinguishable from the feature
           being broken - and this is a feature whose entire job is to speak up. */
        var features = Bind(("OWNER_IDS", "1,2"), ("SUPER_OWNER_IDS", "3"));

        Assert.Equal([3ul, 1ul, 2ul], features.SecurityAlertRecipients);
    }

    [Fact]
    public void NobodyIsMessagedTwice()
    {
        // An id in both OWNER_IDS and SUPER_OWNER_IDS is an ordinary way to write "this
        // person owns the bot", not a request for two identical DMs.
        Assert.Equal([7ul], Bind(("OWNER_IDS", "7"), ("SUPER_OWNER_IDS", "7")).SecurityAlertRecipients);
    }

    [Fact]
    public void WithNothingConfiguredTheFeatureIsOff() => Assert.Empty(Bind().SecurityAlertRecipients);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
