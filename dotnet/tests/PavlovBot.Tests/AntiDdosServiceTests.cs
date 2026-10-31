using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Network;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Network;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>The anti-DDoS minute loop, with the firewall, CrowdSec and Discord faked and the logs on a real disk.</summary>
public sealed class AntiDdosServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 17, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "antiddos-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(T0);
    private readonly Firewall _firewall = new();
    private readonly CrowdSec _crowdsec = new();
    private readonly Posts _posts = new();
    private readonly Players _players = new();
    private (long Free, long Total)? _disk = (50L << 30, 100L << 30);
    private long _linkPackets = 1_000_000;
    private LinkHealth? _health = new(0, 0, 0, 0, 0, 0, 100, 262_144, "up");
    private ReadingStore? _readings;
    private string? _authLogLast = "2026-10-31T21:40:03.123456+00:00 vps CRON[999]: pam_unix(cron:session): session closed for user root";
    private AntiDdosService? _service;
    private CancellationTokenSource _stop = new();

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _stop.CancelAsync();
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }
        _stop.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<AntiDdosService> Start(AntiDdosOptions? options = null)
    {
        var log = new AntiDdosLog(new AntiDdosPaths(Path.Combine(_root, "log")), NullLogger<AntiDdosLog>.Instance);
        var state = new AntiDdosStateStore(Path.Combine(_root, "state.json"), NullLogger<AntiDdosStateStore>.Instance);
        _service = new AntiDdosService(options ?? new AntiDdosOptions { AlertChannel = 42 }, new ShieldLimits(), "ens3", _firewall, _crowdsec, log,
            state, _players, null, _posts, _ => (_linkPackets, _linkPackets * 100), _ => _disk, _clock, NullLogger<AntiDdosService>.Instance,
            _ => _health, _readings, _ => _authLogLast);
        await _service.StartAsync(_stop.Token);
        await _service.Started;
        return _service;
    }

    private async Task Minute(params (string Src, string Proto, int Port, long Packets)[] flows)
    {
        _clock.Now += TimeSpan.FromMinutes(1);
        _linkPackets += 60_000;
        _firewall.Add(flows);
        await _service!.TickNowAsync();
    }

    private List<string> Lines(string file) =>
        File.Exists(Path.Combine(_root, "log", file)) ? [.. File.ReadAllLines(Path.Combine(_root, "log", file))] : [];

    [Fact]
    public async Task StartUpLoadsTheFirewallWithTheTrustedListAndRecordsIt()
    {
        await Start();

        Assert.Equal(["configure", "trusted 0"], _firewall.Calls.Take(2));
        Assert.Contains(Lines("events.jsonl"), l => l.Contains("\"category\":\"service\"", StringComparison.Ordinal) && l.Contains("\"action\":\"started\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachMinuteWritesTheTrafficAndTheBusiestSources()
    {
        await Start();
        await Minute(("203.0.113.5", "udp", 7777, 6000), ("198.51.100.9", "tcp", 22, 10));

        var traffic = Assert.Single(Lines("traffic.jsonl"));
        Assert.Contains("\"rx_packets\":60000", traffic, StringComparison.Ordinal);
        Assert.Contains("\"sources\":2", traffic, StringComparison.Ordinal);

        var sources = Lines("sources.jsonl");
        Assert.Equal(2, sources.Count);
        Assert.Contains("\"src\":\"203.0.113.5\"", sources[0], StringComparison.Ordinal);
        Assert.Contains("\"pps\":100", sources[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceThatKeepsFloodingIsAlertedOnceThenBannedThroughCrowdSec()
    {
        await Start();
        for (var m = 0; m < 3; m++) await Minute(("203.0.113.5", "udp", 7777, 60 * 900));

        var ban = Assert.Single(_crowdsec.Bans);
        Assert.Equal(("203.0.113.5", TimeSpan.FromHours(1)), (ban.Target, ban.Length));
        Assert.StartsWith("udp-rate repeated", ban.Reason, StringComparison.Ordinal);

        var events = Lines("events.jsonl");
        Assert.Contains(events, l => l.Contains("\"action\":\"banned\"", StringComparison.Ordinal) && l.Contains("\"via\":\"crowdsec\"", StringComparison.Ordinal));
        // One alert when it started, one when it was banned - not one a minute.
        Assert.Equal(2, _posts.Sent.Count);
        Assert.Contains("banned", _posts.Sent[1].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddressTheFirewallKeepsBlockingIsEscalatedToACrowdSecBan()
    {
        await Start();
        // Blocked, the block runs out, back and blocked again - three times in five minutes.
        foreach (var blocked in new[] { true, false, true, false, true })
        {
            _firewall.Blocked = blocked ? ["198.51.100.66"] : [];
            await Minute();
        }

        Assert.Equal(3, Lines("events.jsonl").Count(l => l.Contains("\"category\":\"firewall-block\"", StringComparison.Ordinal)));
        var ban = Assert.Single(_crowdsec.Bans);
        Assert.Equal("198.51.100.66", ban.Target);
        Assert.StartsWith("firewall-block repeated", ban.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlockTheBotPlacedItselfIsNotMistakenForTheFirewalls()
    {
        _crowdsec.IsInstalled = false;
        await Start();
        await _service!.BanAsync(IpNetworks.Canonical("203.0.113.5")!, TimeSpan.FromHours(1), "by hand", "owner");
        _firewall.Blocked = ["203.0.113.5"];
        await Minute();

        Assert.DoesNotContain(Lines("events.jsonl"), l => l.Contains("\"category\":\"firewall-block\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutCrowdSecTheBanGoesIntoTheKernelForTheSameTime()
    {
        _crowdsec.IsInstalled = false;
        await Start();
        for (var m = 0; m < 3; m++) await Minute(("203.0.113.5", "udp", 7777, 60 * 900));

        Assert.Contains("block 203.0.113.5 60", _firewall.Calls);
    }

    [Fact]
    public async Task AKnownPlayerIsNeverBannedAndTheHoldIsRecordedOnce()
    {
        _players.Known.Add(CapturedPacket.Number("10.1.1.1")!.Value);
        await Start();
        // A player opening connections in a burst is detected; banning them is still refused.
        for (var m = 0; m < 5; m++)
        {
            _firewall.AddSyn("10.1.1.1", 7777, 500);
            await Minute(("10.1.1.1", "tcp", 7777, 500));
        }

        Assert.Empty(_crowdsec.Bans);
        Assert.Single(Lines("events.jsonl"), l => l.Contains("\"action\":\"held\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewCrowdSecDecisionsAreRecordedAndAKnownPlayerIsFlagged()
    {
        _players.Known.Add(CapturedPacket.Number("10.1.1.1")!.Value);
        await Start();
        await Minute();
        _crowdsec.Decisions.Add(new CrowdSecDecision(7, "10.1.1.1", "Ip", "ban", "crowdsec", "crowdsecurity/ssh-bf", T0.AddHours(4)));
        await Minute();

        var decision = Assert.Single(Lines("events.jsonl"), l => l.Contains("\"category\":\"crowdsec-decision\"", StringComparison.Ordinal));
        Assert.Contains("\"known_player\":\"true\"", decision, StringComparison.Ordinal);
        Assert.Contains(_posts.Sent, e => e.Title.Contains("CrowdSec", StringComparison.Ordinal) && e.Description.Contains("known player", StringComparison.Ordinal));

        _crowdsec.Decisions.Clear();
        _clock.Now = T0.AddHours(5);
        await _service!.TickNowAsync();
        Assert.Contains(Lines("events.jsonl"), l => l.Contains("\"action\":\"expired\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFloodRaisesLoggingToIncidentAndItLapsesBackOnItsOwn()
    {
        var service = await Start();
        service.OnStarted(_clock.Now, new LinkSecond(_clock.Now, 90_000, 90_000_000), null);
        await service.DrainAsync();

        Assert.Equal(LogMode.Incident, _firewall.Mode);
        Assert.Equal("flood-" + AntiDdosLog.FileStamp(T0), service.CurrentIncident);

        _clock.Now += TimeSpan.FromMinutes(31);
        await service.TickNowAsync();

        Assert.Equal(LogMode.Normal, _firewall.Mode);
        Assert.Contains(Lines("events.jsonl"), l => l.Contains("\"action\":\"lapsed\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StoragePressureCapsLoggingRemovesOldRotatedLogsAndSaysSoOnce()
    {
        // A disk at 8% free stays under pressure after the guard has deleted what it can.
        _disk = (8L << 30, 100L << 30);
        var service = await Start(new AntiDdosOptions { AlertChannel = 42, MaxLogMegabytes = 1 });
        var root = Path.Combine(_root, "log");
        var old = Path.Combine(root, "packets", "packets.jsonl-20261001-000000.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        await File.WriteAllBytesAsync(old, new byte[2 * 1024 * 1024]);
        await service.SetModeAsync(LogMode.Incident, TimeSpan.FromHours(1), "owner");

        await Minute();
        await Minute();

        Assert.False(File.Exists(old));
        Assert.Equal(LogMode.Minimal, _firewall.Mode);
        Assert.Single(_posts.Sent, e => e.Title.Contains("storage", StringComparison.Ordinal));
        Assert.Contains(Lines("events.jsonl"), l => l.Contains("\"action\":\"evicted\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AThresholdOutsideItsRangeIsRefusedAndAGoodOneIsAppliedAndKept()
    {
        var service = await Start();

        var refused = await service.SetThresholdAsync(Thresholds.Find("per_ip_pps")!, 50, "owner");
        Assert.Contains("200-", refused, StringComparison.Ordinal);

        Assert.Null(await service.SetThresholdAsync(Thresholds.Find("per_ip_pps")!, 2500, "owner"));
        Assert.Equal(2500, _firewall.Settings.Limits.PerSourcePps);
        Assert.Contains("\"per_ip_pps\": 2500", await File.ReadAllTextAsync(Path.Combine(_root, "state.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustingAddsToTheFirewallAndCrowdSecAndBlocksOverlappingBans()
    {
        var service = await Start();
        var network = IpNetworks.Canonical("198.51.100.0/24")!;

        Assert.Null(await service.TrustAsync(network, "office", "owner"));
        Assert.Contains("trusted 1", _firewall.Calls);
        Assert.Equal(["198.51.100.0/24"], _crowdsec.Allowed);

        var refused = await service.BanAsync(IpNetworks.Canonical("198.51.100.7")!, TimeSpan.FromHours(1), "x", "owner");
        Assert.Contains("overlaps the trusted list", refused, StringComparison.Ordinal);
        Assert.Contains("loopback", await service.BanAsync(IpNetworks.Canonical("127.0.0.1")!, null, "x", "owner"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APermanentBanIsTenYearsInCrowdSecAndAnExistingOneIsNotDuplicated()
    {
        var service = await Start();
        var target = IpNetworks.Canonical("203.0.113.5")!;

        Assert.Null(await service.BanAsync(target, null, "griefing", "owner"));
        Assert.Equal(TimeSpan.FromDays(3650), Assert.Single(_crowdsec.Bans).Length);

        _crowdsec.Decisions.Add(new CrowdSecDecision(9, "203.0.113.5", "Ip", "ban", "cscli", "pavlovbot: owner: griefing", T0.AddYears(10)));
        Assert.Contains("already banned", await service.BanAsync(target, TimeSpan.FromHours(1), "again", "owner"), StringComparison.Ordinal);
    }

    // ---- connection loss ----

    private ReadingStore Readings() =>
        _readings = new ReadingStore(Path.Combine(_root, "state", "last-reading.json"), NullLogger<ReadingStore>.Instance);

    private async Task Restart(TimeSpan down)
    {
        await _stop.CancelAsync();
        await _service!.StopAsync(CancellationToken.None);
        _service.Dispose();
        _service = null;
        _clock.Now += down;
        _stop.Dispose();
        _stop = new CancellationTokenSource();
    }

    [Fact]
    public async Task KernelLossIsInTheTrafficRecordAsWhatHappenedThatMinute()
    {
        _health = new LinkHealth(10, 0, 0, 0, 500, 7, 100, 262_144, "up");
        await Start();
        _health = new LinkHealth(1_010, 0, 0, 0, 800, 7, 120, 262_144, "up");
        await Minute();

        var traffic = Assert.Single(Lines("traffic.jsonl"));
        Assert.Contains("\"lost\":1300", traffic, StringComparison.Ordinal);
        Assert.Contains("\"nic_dropped\":1000", traffic, StringComparison.Ordinal);
        Assert.Contains("\"softnet_dropped\":300", traffic, StringComparison.Ordinal);
        Assert.Contains("\"link\":\"up\"", traffic, StringComparison.Ordinal);
        Assert.Contains(Lines("events.jsonl"), l => l.Contains("\"category\":\"kernel-loss\"", StringComparison.Ordinal));
        Assert.Equal(1300, LogQuery.Rates([AntiDdosRecords.Parse(traffic)!], new QueryFilter(T0, T0.AddHours(1)))[0].Lost);
    }

    [Fact]
    public async Task TheLinkGoingDownAndUpIsRecordedAndAlertedOnceEach()
    {
        await Start();
        _health = _health! with { OperState = "down" };
        await Minute();
        await Minute();
        _health = _health with { OperState = "up" };
        await Minute();

        var link = Lines("events.jsonl").Where(l => l.Contains("\"category\":\"link\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, link.Count);
        Assert.Contains("\"action\":\"down\"", link[0], StringComparison.Ordinal);
        Assert.Contains("\"action\":\"up\"", link[1], StringComparison.Ordinal);
        Assert.Equal(["ens3 went down", "ens3 is back up"], _posts.Sent.Select(e => e.Title[(e.Title.IndexOf(' ', StringComparison.Ordinal) + 1)..]));
    }

    [Fact]
    public async Task AFillingConnectionTableIsAlertedOnceAndRecoversBelowEightyPercent()
    {
        await Start();
        foreach (var count in new[] { 240_000, 250_000, 220_000, 200_000, 245_000 })
        {
            _health = _health! with { ConntrackCount = count };
            await Minute();
        }

        var actions = Lines("events.jsonl").Where(l => l.Contains("\"category\":\"conntrack\"", StringComparison.Ordinal))
            .Select(l => l.Contains("\"action\":\"full\"", StringComparison.Ordinal) ? "full" : "recovered").ToList();
        // 91.6% full; still full; 84% is not yet recovered; 76% is; full again.
        Assert.Equal(["full", "recovered", "full"], actions);
        Assert.Equal(2, _posts.Sent.Count(e => e.Title.EndsWith("Connection table nearly full", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheTimeTheBotWasDownIsRecordedAsOneGapRecordWithEverySourceItCounted()
    {
        Readings();
        await Start();
        await Minute(("203.0.113.5", "udp", 7777, 100));
        await Restart(TimeSpan.FromMinutes(30));

        // While the bot was down: the kernel kept counting.
        _linkPackets += 5_000_000;
        _firewall.Add([("203.0.113.5", "udp", 7777, 4_000_000), ("198.51.100.9", "udp", 7777, 900_000)]);
        _health = _health! with { NicMissed = 70_000 };
        await Start();

        var gap = Assert.Single(Lines("traffic.jsonl"), l => l.Contains("\"gap\":\"", StringComparison.Ordinal));
        Assert.Contains("\"rx_packets\":5000000", gap, StringComparison.Ordinal);
        Assert.Contains("\"lost\":70000", gap, StringComparison.Ordinal);
        Assert.Contains("the bot was not running from", gap, StringComparison.Ordinal);
        var sources = Lines("sources.jsonl").Where(l => l.Contains("\"gap\":true", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, sources.Count);
        Assert.Contains("\"packets\":4000000", sources.Single(l => l.Contains("203.0.113.5", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains(Lines("events.jsonl"), l => l.Contains("\"category\":\"gap\"", StringComparison.Ordinal));
        Assert.True(LogQuery.Rates([AntiDdosRecords.Parse(gap)!], new QueryFilter(T0, T0.AddDays(1)))[0].Gap);
    }

    [Fact]
    public async Task AfterARebootTheGapRecordCoversOnlyTheTimeSinceBoot()
    {
        Readings();
        await Start();
        await Minute();
        await Restart(TimeSpan.FromMinutes(10));

        _linkPackets = 3_000;   // the counters started again from zero
        await Start();

        var gap = Assert.Single(Lines("traffic.jsonl"), l => l.Contains("\"gap\":\"", StringComparison.Ordinal));
        Assert.Contains("\"rx_packets\":3000", gap, StringComparison.Ordinal);
        Assert.Contains("rebooted", gap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartingMovesTheSavedReadingOnSoACrashBeforeTheFirstMinuteCannotRecordTheGapTwice()
    {
        Readings();
        await Start();
        await Minute();
        await Restart(TimeSpan.FromMinutes(10));
        await Start();

        // Saved by the start itself, before any tick or stop.
        Assert.Equal(_clock.Now, _readings!.Load()!.Time);
        var gap = Assert.Single(Lines("traffic.jsonl"), l => l.Contains("\"gap\":\"", StringComparison.Ordinal));
        Assert.Contains("\"window_s\":600", gap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuickRestartIsNotAGap()
    {
        Readings();
        await Start();
        await Minute();
        await Restart(TimeSpan.FromSeconds(30));
        await Start();

        Assert.DoesNotContain(Lines("traffic.jsonl"), l => l.Contains("\"gap\":\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AlertsDiscordDidNotTakeAreHeldInOrderAndPostedLateWhenItAnswers()
    {
        await Start();
        _posts.Down = true;
        _health = _health! with { OperState = "down" };
        await Minute();
        _health = _health with { OperState = "up" };
        await Minute();
        Assert.Empty(_posts.Sent);

        _posts.Down = false;
        await Minute();
        await Minute();

        Assert.Equal(["ens3 went down", "ens3 is back up"], _posts.Sent.Select(e => e.Title[(e.Title.IndexOf(' ', StringComparison.Ordinal) + 1)..]));
        // Marked late exactly once, though the first flush attempt also failed.
        Assert.All(_posts.Sent, e => Assert.Single(System.Text.RegularExpressions.Regex.Matches(e.Footer!.Value.Text, "Posted late")));
        Assert.Single(Lines("events.jsonl"), l => l.Contains("\"action\":\"delivered-late\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyTheNewestAlertsAreHeldAndTheRestAreCounted()
    {
        await Start();
        _posts.Down = true;
        for (var m = 0; m < AntiDdosService.OutboxSize + 4; m++)
        {
            _health = _health! with { OperState = m % 2 == 0 ? "down" : "up" };
            await Minute();
        }
        _posts.Down = false;
        await Minute();

        Assert.Equal(AntiDdosService.OutboxSize, _posts.Sent.Count);
        Assert.Contains(Lines("events.jsonl"), l => l.Contains("4 older one(s) did not fit", StringComparison.Ordinal));
    }

    // ---- CrowdSec parser health ----

    private bool AttentionAlert() => _posts.Sent.Any(e => e.Title.EndsWith("CrowdSec needs attention", StringComparison.Ordinal));

    [Fact]
    public async Task AQuietAuthLogWithNothingParsedIsNotAFaultWhenAFailedLoginInItsFormatParses()
    {
        // The real case: 13 lines of cron and sudo, which no parser takes.
        _crowdsec.Sources.Add(new CrowdSecSource("file:/var/log/auth.log", 13, 0, 13));
        await Start();
        await Minute();

        var probe = Assert.Single(_crowdsec.Probed);
        Assert.StartsWith("2026-10-31T21:40:03.123456+00:00 vps sshd[1]: Invalid user pavlovbot-probe from 192.0.2.1", probe, StringComparison.Ordinal);
        Assert.False(AttentionAlert());
    }

    [Fact]
    public async Task AFailedLoginThatDoesNotParseIsAFaultAndSaysWhatIsMissed()
    {
        _crowdsec.Sources.Add(new CrowdSecSource("file:/var/log/auth.log", 13, 0, 13));
        _crowdsec.Parses = _ => false;
        await Start();
        await Minute();

        var alert = Assert.Single(_posts.Sent, e => e.Title.EndsWith("CrowdSec needs attention", StringComparison.Ordinal));
        Assert.Contains("SSH brute force is never banned", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProbeVerdictStandsForHoursInsteadOfRunningEveryHealthCheck()
    {
        _crowdsec.Sources.Add(new CrowdSecSource("file:/var/log/auth.log", 13, 0, 13));
        await Start();
        for (var m = 0; m < 60; m++) await Minute();

        Assert.Single(_crowdsec.Probed);
    }

    [Fact]
    public async Task CscliNotAnsweringTheProbeIsNotCalledAParserFault()
    {
        _crowdsec.Sources.Add(new CrowdSecSource("file:/var/log/auth.log", 13, 0, 13));
        _crowdsec.Parses = _ => null;
        await Start();
        await Minute();

        Assert.False(AttentionAlert());
    }

    [Fact]
    public async Task ASourceWithNoKnownLineToTestIsNotProbedOrReported()
    {
        _crowdsec.Sources.Add(new CrowdSecSource("file:/var/log/nginx/access.log", 500, 0, 500));
        _crowdsec.Sources.Add(new CrowdSecSource("file:/var/log/auth.log", 900, 12, 888));
        await Start();
        await Minute();

        Assert.Empty(_crowdsec.Probed);
        Assert.False(AttentionAlert());
    }

    // ---- fakes ----

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Players : IPlayerTraffic
    {
        public HashSet<uint> Known { get; } = [];

        public bool RosterFresh => true;

        public IReadOnlyCollection<string> OnlineNames() => [];

        public IReadOnlySet<uint> OnlineAddresses() => Known;

        public IReadOnlySet<uint> KnownAddresses() => Known;
    }

    private sealed class Posts : IAutoPostTarget
    {
        public List<Embed> Sent { get; } = [];

        /// <summary>Discord unreachable: every post fails, half by throwing and half by returning nothing.</summary>
        public bool Down { get; set; }

        private int _attempts;

        public Task<AutoPostEdit> EditAsync(ulong channelId, ulong messageId, Embed embed, MessageComponent? components, CancellationToken ct) =>
            Task.FromResult(AutoPostEdit.Edited);

        public Task<ulong?> SendAsync(ulong channelId, Embed embed, MessageComponent? components, CancellationToken ct)
        {
            if (Down)
            {
                if (_attempts++ % 2 == 0) throw new HttpRequestException("unreachable");
                return Task.FromResult<ulong?>(null);
            }
            lock (Sent) Sent.Add(embed);
            return Task.FromResult<ulong?>(1);
        }
    }

    private sealed class CrowdSec : ICrowdSec
    {
        public bool IsInstalled { get; set; } = true;
        public List<CrowdSecDecision> Decisions { get; } = [];
        public List<(string Target, TimeSpan Length, string Reason)> Bans { get; } = [];
        public List<string> Allowed { get; } = [];

        public bool Installed => IsInstalled;

        public Task<(IReadOnlyList<CrowdSecDecision>? Decisions, string? Problem)> DecisionsAsync(CancellationToken ct) =>
            Task.FromResult<(IReadOnlyList<CrowdSecDecision>?, string?)>(([.. Decisions], null));

        public Task<IReadOnlyList<CrowdSecAlert>?> AlertsAsync(TimeSpan since, string? address, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CrowdSecAlert>?>([]);

        public Task<IReadOnlyList<CrowdSecBouncer>?> BouncersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CrowdSecBouncer>?>([new CrowdSecBouncer("cs-firewall-bouncer", DateTimeOffset.MaxValue, false, "0.0.36")]);

        public List<CrowdSecSource> Sources { get; } = [];

        /// <summary>What <c>cscli explain</c> says of a line; every line it was asked about.</summary>
        public Func<string, bool?> Parses { get; set; } = _ => true;

        public List<string> Probed { get; } = [];

        public Task<IReadOnlyList<CrowdSecSource>?> AcquisitionAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CrowdSecSource>?>([.. Sources]);

        public Task<bool?> ParsesAsync(string line, CancellationToken ct)
        {
            Probed.Add(line);
            return Task.FromResult(Parses(line));
        }

        public Task<string?> BanAsync(IpNetworks.Network target, TimeSpan duration, string reason, CancellationToken ct)
        {
            Bans.Add((target.Text, duration, reason));
            return Task.FromResult<string?>(null);
        }

        public Task<string?> UnbanAsync(IpNetworks.Network target, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> AllowAsync(IpNetworks.Network target, string comment, CancellationToken ct)
        {
            Allowed.Add(target.Text);
            return Task.FromResult<string?>(null);
        }

        public Task<string?> DisallowAsync(IpNetworks.Network target, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>A firewall whose counters grow by what each test minute adds.</summary>
    private sealed class Firewall : IAntiDdosFirewall
    {
        private readonly Dictionary<FlowKey, Counter> _flows = [];
        private readonly Dictionary<FlowKey, Counter> _syn = [];

        public List<string> Calls { get; } = [];

        public bool Managed => true;

        public FirewallSettings Settings { get; private set; } = new() { Interface = "ens3", Enforce = true };

        public LogMode Mode { get; private set; } = LogMode.Normal;

        public bool Engaged { get; private set; }

        public string? LastProblem => null;

        /// <summary>What the kernel's block list holds now.</summary>
        public List<string> Blocked { get; set; } = [];

        public bool Enabled => true;

        public ShieldLimits Limits => Settings.Limits;

        public void Add((string Src, string Proto, int Port, long Packets)[] flows)
        {
            foreach (var f in flows)
            {
                var key = new FlowKey(f.Src, f.Proto, f.Port);
                _flows[key] = (_flows.TryGetValue(key, out var c) ? c : default) + new Counter(f.Packets, f.Packets * 100);
            }
        }

        public void AddSyn(string src, int port, long packets)
        {
            var key = new FlowKey(src, "tcp", port);
            _syn[key] = (_syn.TryGetValue(key, out var c) ? c : default) + new Counter(packets, packets * 60);
        }

        public Task<string?> ConfigureAsync(FirewallSettings settings, CancellationToken ct)
        {
            Calls.Add("configure");
            Settings = settings;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> SetModeAsync(LogMode mode, CancellationToken ct)
        {
            Calls.Add($"mode {mode}");
            Mode = mode;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> SetTrustedAsync(IReadOnlyCollection<string> networks, CancellationToken ct)
        {
            Calls.Add($"trusted {networks.Count}");
            return Task.FromResult<string?>(null);
        }

        public Task<string?> BlockAsync(string address, int minutes, CancellationToken ct)
        {
            Calls.Add($"block {address} {minutes}");
            return Task.FromResult<string?>(null);
        }

        public Task<string?> UnblockAsync(string address, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<FirewallSnapshot?> SnapshotAsync(CancellationToken ct) => Task.FromResult<FirewallSnapshot?>(new FirewallSnapshot(
            new Dictionary<string, Counter>(), [.. Blocked.Select(b => new BlockedAddress(b, 600))], [],
            new Accounting(new Dictionary<FlowKey, Counter>(_flows), new Dictionary<FlowKey, Counter>(_syn), new Dictionary<FlowKey, Counter>(),
                new Dictionary<string, Counter>()),
            new Dictionary<string, int>()));

        public Task<string?> ApplyAsync(bool engaged, CancellationToken ct)
        {
            Engaged = engaged;
            return Task.FromResult<string?>(null);
        }

        public Task AllowAsync(IReadOnlySet<uint> addresses, CancellationToken ct) => Task.CompletedTask;

        public Task<ShieldStats?> StatsAsync(CancellationToken ct) => Task.FromResult<ShieldStats?>(new ShieldStats(0, 0, 0, 0));

        public Task<string?> UnblockAllAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }
}

/// <summary>The command's own parsing and its shape as Discord registers it.</summary>
public class AntiDdosCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ItsShapeIsWithinDiscordsLimits()
    {
        var built = (SlashCommandProperties)new AntiDdosCommand(null!, null!, null!, null!).Build();
        Assert.Equal("antiddos", built.Name.Value);

        void Check(IEnumerable<ApplicationCommandOptionProperties> options)
        {
            var list = options.ToList();
            Assert.True(list.Count <= 25);
            foreach (var o in list)
            {
                Assert.InRange(o.Description.Length, 1, 100);
                Assert.Matches("^[a-z0-9_-]{1,32}$", o.Name);
                Assert.True((o.Choices?.Count ?? 0) <= 25);
                Assert.All(o.Choices ?? [], c => Assert.InRange(c.Name.Length, 1, 100));
                if (o.Options is { } inner) Check(inner);
            }
        }

        Check(built.Options.Value);
        Assert.Equal(
            ["status", "traffic", "history", "top", "detections", "bans", "crowdsec", "storage", "export", "block", "unban", "logging", "allow", "threshold"],
            built.Options.Value.Select(o => o.Name));
    }

    [Theory]
    [InlineData(null, null, 60L, "2026-10-29T17:00", "2026-10-29T18:00")]
    [InlineData("2026-10-29 12:00", "2026-10-29 13:30", null, "2026-10-29T12:00", "2026-10-29T13:30")]
    [InlineData("2026-10-29T12:00:00Z", null, null, "2026-10-29T12:00", "2026-10-29T18:00")]
    public void ARangeIsMinutesBackOrFromTo(string? from, string? to, long? minutes, string start, string end)
    {
        var range = AntiDdosCommand.ParseWindow(from, to, minutes, Now, TimeSpan.FromDays(7))!.Value;

        Assert.Equal(DateTimeOffset.Parse(start + "Z", System.Globalization.CultureInfo.InvariantCulture), range.From);
        Assert.Equal(DateTimeOffset.Parse(end + "Z", System.Globalization.CultureInfo.InvariantCulture), range.To);
    }

    [Theory]
    [InlineData("yesterday", null)]
    [InlineData("2026-10-29 13:00", "2026-10-29 12:00")]   // backwards
    [InlineData("2026-09-01 00:00", null)]                // longer than allowed
    public void AnythingElseIsNoRange(string? from, string? to) =>
        Assert.Null(AntiDdosCommand.ParseWindow(from, to, null, Now, TimeSpan.FromDays(7)));

    [Theory]
    [InlineData("30m", 30.0)]
    [InlineData("4h", 240.0)]
    [InlineData("7d", 10080.0)]
    [InlineData("permanent", null)]
    public void DurationsAreMinutesHoursDaysOrPermanent(string text, double? minutes)
    {
        Assert.Null(AntiDdosCommand.Duration(text, out var duration));
        Assert.Equal(minutes, duration?.TotalMinutes);
    }

    [Theory]
    [InlineData("2m")]       // under five minutes
    [InlineData("400d")]
    [InlineData("10x")]
    [InlineData("-5h")]
    [InlineData("")]
    public void BadDurationsAreRefused(string text) => Assert.NotNull(AntiDdosCommand.Duration(text, out _));

    [Fact]
    public void APacketRowSaysWhatHappenedToIt()
    {
        var line = AntiDdosFixtures.Read("ulogd-packets.jsonl").Split('\n')[0];

        Assert.Equal("10-29 17:31:12 pkt dropped blocked            127.0.0.1:43443 > :7781 udp len 48", AntiDdosCommand.Row(AntiDdosRecords.Parse(line)!));
    }
}

/// <summary>The logs on disk: written 0600, rotated files read back, gzip included.</summary>
public sealed class AntiDdosLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "antiddoslog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task RecordsAreReadBackAcrossRotatedAndCompressedFilesInOrder()
    {
        var log = new AntiDdosLog(new AntiDdosPaths(_root), NullLogger<AntiDdosLog>.Instance);
        var t = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        string Event(int minute) => AntiDdosRecords.Event(new AntiDdosEvent(t.AddMinutes(minute), "ban", "info", "203.0.113.5", "banned"));

        Directory.CreateDirectory(_root);
        await using (var gz = new System.IO.Compression.GZipStream(File.Create(Path.Combine(_root, "events.jsonl-20261029-120500.gz")),
                         System.IO.Compression.CompressionLevel.Fastest))
        {
            await gz.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Event(0) + "\n" + Event(1) + "\n"));
        }
        await File.WriteAllTextAsync(Path.Combine(_root, "events.jsonl-20261029-121000"), Event(6) + "\n");
        await log.WriteAsync(RecordKind.Event, [Event(11), "not json", Event(12)], CancellationToken.None);

        var all = new List<LogRecord>();
        await foreach (var r in log.ReadAsync([RecordKind.Event], DateTimeOffset.MinValue.AddMinutes(2), DateTimeOffset.MaxValue.AddMinutes(-2), CancellationToken.None))
            all.Add(r);
        Assert.Equal([0, 1, 6, 11, 12], all.Select(r => (int)(r.Time - t).TotalMinutes));

        var window = new List<LogRecord>();
        await foreach (var r in log.ReadAsync([RecordKind.Event], t.AddMinutes(5), t.AddMinutes(11), CancellationToken.None)) window.Add(r);
        Assert.Equal([6, 11], window.Select(r => (int)(r.Time - t).TotalMinutes));

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_root, "events.jsonl")));

        var (oldest, _, files) = await log.SummaryAsync(RecordKind.Event, CancellationToken.None);
        Assert.Equal((t, 3), (oldest, files));
    }

    [Fact]
    public void ThresholdOverridesApplyOnlyInsideTheirRange()
    {
        var options = Thresholds.Apply(new AntiDdosOptions(), new ShieldLimits(),
            new Dictionary<string, int> { ["per_ip_pps"] = 50, ["block_minutes"] = 30, ["autoban"] = 0, ["nonsense"] = 5 }, out var limits);

        Assert.Equal(1000, limits.PerSourcePps);   // 50 is under the floor: ignored
        Assert.Equal(30, limits.BlockMinutes);
        Assert.False(options.AutoBan);
    }

    [Fact]
    public void ADamagedStateFileStartsFromTheSettingsInsteadOfFailing()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "state.json");
        File.WriteAllText(path, "{ not json");
        var store = new AntiDdosStateStore(path, NullLogger<AntiDdosStateStore>.Instance);

        Assert.Empty(store.Read().Trusted);
        Assert.True(store.Update(s => s.Overrides["ban_minutes"] = 90));
        Assert.Equal(90, new AntiDdosStateStore(path, NullLogger<AntiDdosStateStore>.Instance).Read().Overrides["ban_minutes"]);
    }
}
