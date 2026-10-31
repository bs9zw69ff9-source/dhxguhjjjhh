using PavlovBot.Core.Network;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>Real output, saved from nftables 1.0.9, ulogd2 2.0.8 and CrowdSec 1.8.2 on Ubuntu 24.04.</summary>
internal static class AntiDdosFixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "antiddos", name));
}

/// <summary>The nftables policy: what it filters, in what order, and what it logs.</summary>
public class FirewallPolicyTests
{
    private static readonly FirewallSettings Settings = new() { Interface = "ens3", Enforce = true };

    private static string[] Lines(string script, string chain) =>
        [.. script.Split('\n').Where(l => l.StartsWith($"add rule inet pavlovbot_ddos {chain} ", StringComparison.Ordinal))];

    [Theory]
    [InlineData("ens3\"; flush ruleset; \"")]
    [InlineData("")]
    [InlineData("a-very-long-interface-name")]
    public void AnInterfaceThatCouldBreakOutOfItsQuotesIsRefused(string iface) =>
        Assert.Throws<ArgumentException>(() => FloodShieldRules.Script(Settings with { Interface = iface }, engaged: false, LogMode.Normal));

    [Fact]
    public void OutOfRangeThresholdsAreRefusedByName()
    {
        Assert.Contains("ANTIDDOS_SSH_NEW_PER_MINUTE", FloodShieldRules.Problem(Settings with { SshNewPerMinute = 0 }), StringComparison.Ordinal);
        Assert.Contains("ANTIDDOS_SSH_PORTS", FloodShieldRules.Problem(Settings with { SshPorts = "22; drop" }), StringComparison.Ordinal);
        Assert.Null(FloodShieldRules.Problem(Settings));
    }

    [Fact]
    public void TheGuardRunsInOrderTrustedSshPlayersBlockedThenTheAttackLimits()
    {
        var guard = Lines(FloodShieldRules.Script(Settings, engaged: true, LogMode.Normal), "guard");
        int Index(string marker) => Array.FindIndex(guard, l => l.Contains(marker, StringComparison.Ordinal));

        Assert.Equal(0, Index("other-interfaces"));
        Assert.True(Index("comment \"trusted\"") < Index("drop_ssh-rate"));
        Assert.True(Index("drop_ssh-rate") < Index("comment \"ssh\""));
        Assert.True(Index("comment \"ssh\"") < Index("comment \"players\""));
        Assert.True(Index("comment \"players\"") < Index("jump drop_blocked"));
        Assert.True(Index("jump drop_blocked") < Index("jump drop_per-source"));
        Assert.True(Index("jump drop_per-source") < Index("jump drop_unknown-cap"));
        Assert.All(guard, l => Assert.Contains("inet pavlovbot_ddos guard", l, StringComparison.Ordinal));
    }

    [Fact]
    public void BothChainsHookPreroutingSoContainersOnABridgeAreCoveredToo()
    {
        var script = FloodShieldRules.Script(Settings, engaged: false, LogMode.Normal);

        Assert.Contains("add chain inet pavlovbot_ddos guard { type filter hook prerouting priority -300; policy accept; }", script, StringComparison.Ordinal);
        Assert.Contains("add chain inet pavlovbot_ddos observe { type filter hook prerouting priority -150; policy accept; }", script, StringComparison.Ordinal);
        Assert.DoesNotContain("policy drop", script, StringComparison.Ordinal);
        Assert.Contains("iifname != \"ens3\" return", script, StringComparison.Ordinal);
    }

    [Fact]
    public void StandingByKeepsBlocksButSetsNoAttackLimits()
    {
        var guard = string.Join('\n', Lines(FloodShieldRules.Script(Settings, engaged: false, LogMode.Normal), "guard"));

        Assert.Contains("jump drop_blocked", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("drop_per-source", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("drop_unknown-cap", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutMitigationTheTableOnlyCountsAndLogs()
    {
        var script = FloodShieldRules.Script(Settings with { Enforce = false }, engaged: true, LogMode.Normal);

        Assert.DoesNotContain("jump drop_", script, StringComparison.Ordinal);
        Assert.Contains("update @acct4", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListsAndCountsSurviveAReapply()
    {
        var script = FloodShieldRules.Script(Settings, engaged: true, LogMode.Normal);

        foreach (var set in new[] { "trusted4", "trusted6", "players4", "blocked4", "blocked6", "acct4", "acct6", "dropped4" })
            Assert.DoesNotContain($"flush set inet pavlovbot_ddos {set}\n", script, StringComparison.Ordinal);
        Assert.Contains("flush set inet pavlovbot_ddos gameports", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstVersionsChainIsRemovedOnUpgrade()
    {
        var script = FloodShieldRules.Script(Settings, engaged: false, LogMode.Normal);

        Assert.Contains("add chain inet pavlovbot_ddos input\nflush chain inet pavlovbot_ddos input\ndelete chain inet pavlovbot_ddos input\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void IPv6IsFilteredLikeIPv4AndNeighbourDiscoveryIsNeverTouched()
    {
        var script = FloodShieldRules.Script(Settings, engaged: true, LogMode.Normal);

        Assert.Contains("ip6 saddr @trusted6 counter accept", script, StringComparison.Ordinal);
        Assert.Contains("ip6 saddr @blocked6 jump drop_blocked", script, StringComparison.Ordinal);
        Assert.Contains("add @blocked6 { ip6 saddr timeout 10m } jump drop_per-source", script, StringComparison.Ordinal);
        Assert.Contains("icmpv6 type echo-request limit rate over 50/second", script, StringComparison.Ordinal);
        // Only echo requests are ever limited; neighbour discovery, which IPv6 cannot work without, is not matched at all.
        Assert.DoesNotContain("nd-neighbor", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLimitsComeFromTheSettings()
    {
        var script = FloodShieldRules.Script(Settings with { Limits = new ShieldLimits("7777-7779", 800, 3000, 15), SshNewPerMinute = 12 },
            engaged: true, LogMode.Normal);

        Assert.Contains("add element inet pavlovbot_ddos gameports { 7777-7779 }", script, StringComparison.Ordinal);
        Assert.Contains("update @rate4 { ip saddr limit rate over 800/second burst 1600 packets } add @blocked4 { ip saddr timeout 15m }", script, StringComparison.Ordinal);
        Assert.Contains("limit rate over 3000/second burst 6000 packets jump drop_unknown-cap", script, StringComparison.Ordinal);
        Assert.Contains("update @ssh4 { ip saddr limit rate over 12/minute burst 12 packets }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogModeChangesOnlyTheLoggingNeverAFilter()
    {
        static IEnumerable<string> Filters(LogMode mode) =>
            Lines(FloodShieldRules.Script(Settings, engaged: true, mode), "guard");

        var normal = Filters(LogMode.Normal).ToList();
        foreach (var mode in Enum.GetValues<LogMode>()) Assert.Equal(normal, Filters(mode));
    }

    [Fact]
    public void EveryLogRuleDrawsOnItsModesSharedLimit()
    {
        var script = FloodShieldRules.Script(Settings, engaged: true, LogMode.Incident);
        var logs = script.Split('\n').Where(l => l.Contains(" log prefix ", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(logs);
        Assert.All(logs, l => Assert.Matches("limit name \"(drops|suspect|samples)_incident\"", l));
        Assert.All(logs, l => Assert.Contains("group 7 snaplen 128", l, StringComparison.Ordinal));
        Assert.Contains("add limit inet pavlovbot_ddos drops_incident { rate 50/second burst 50 packets }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggingOffWritesNoPacketsButStillFiltersAndCounts()
    {
        var script = FloodShieldRules.Script(Settings, engaged: true, LogMode.Off);

        Assert.DoesNotContain(" log prefix ", script, StringComparison.Ordinal);
        Assert.Contains("jump drop_per-source", script, StringComparison.Ordinal);
        Assert.Contains("counter drop comment \"per-source\"", script, StringComparison.Ordinal);
        Assert.Contains("update @acct4", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorstCaseLogRateIsBoundedInEveryMode()
    {
        Assert.Equal(0, LogRates.For(LogMode.Off).WorstCasePerSecond);
        Assert.True(LogRates.For(LogMode.Normal).WorstCasePerSecond <= 11);
        Assert.True(LogRates.For(LogMode.Incident).WorstCasePerSecond <= 105);
        Assert.True(Enum.GetValues<LogMode>().Select(m => LogRates.For(m).WorstCasePerSecond).SequenceEqual(
            Enum.GetValues<LogMode>().Select(m => LogRates.For(m).WorstCasePerSecond).Order()));
    }

    [Fact]
    public void TrustedNetworksAreRewrittenWholeAndCanonical()
    {
        var script = FloodShieldRules.TrustedScript(["203.0.113.5", "198.51.100.0/24", "2001:db8::/48", "nonsense", "203.0.113.5"]);

        Assert.Equal(
            "flush set inet pavlovbot_ddos trusted4\nflush set inet pavlovbot_ddos trusted6\n" +
            "add element inet pavlovbot_ddos trusted4 { 203.0.113.5, 198.51.100.0/24 }\n" +
            "add element inet pavlovbot_ddos trusted6 { 2001:db8::/48 }\n",
            script);
    }

    [Fact]
    public void BlockingAndUnblockingOneAddressTakeOnlyAnAddress()
    {
        Assert.Equal("add element inet pavlovbot_ddos blocked4 { 203.0.113.5 timeout 60m }\n", FloodShieldRules.BlockScript("203.0.113.5", 60));
        Assert.Equal("add element inet pavlovbot_ddos blocked6 { 2001:db8::1 timeout 5m }\n", FloodShieldRules.BlockScript("2001:DB8:0::1", 5));
        Assert.Null(FloodShieldRules.BlockScript("203.0.113.0/24", 60));
        Assert.Null(FloodShieldRules.BlockScript("203.0.113.5 }; flush ruleset", 60));
        Assert.Contains("delete element inet pavlovbot_ddos blocked4 { 203.0.113.5 }", FloodShieldRules.UnblockOneScript("203.0.113.5"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTableIsReadBackFromNftsOwnJson()
    {
        // Saved after loading the real policy on lo and sending UDP, TCP and pings at it.
        var table = FloodShieldRules.ParseTable(AntiDdosFixtures.Read("nft-table.json"))!;

        Assert.Equal(new Counter(263, 27_964), table.Counters["blocked"]);
        Assert.Equal(new Counter(1, 128), table.Counters["per-source"]);
        Assert.Equal(new Counter(100, 12_800), table.Counters["udp"]);
        Assert.Equal(["127.0.0.1", "198.51.100.9"], table.Blocked.Select(b => b.Address).Order(StringComparer.Ordinal));
        Assert.Equal(new Counter(100, 12_800), table.Accounting.Flows[new FlowKey("127.0.0.1", "udp", 7777)]);
        Assert.Equal(new Counter(1, 60), table.Accounting.Syn[new FlowKey("127.0.0.1", "tcp", 22)]);
        Assert.Equal(new Counter(264, 28_092), table.Accounting.Dropped["127.0.0.1"]);
        Assert.False(table.AccountingSaturated);

        var stats = FloodShieldRules.ParseStats(AntiDdosFixtures.Read("nft-table.json"))!;
        Assert.Equal(263, stats.BlockedPackets);
        Assert.Equal(1, stats.PerSourcePackets);
        Assert.Equal(2, stats.BlockedSources);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"something\": 1}")]
    [InlineData("{\"nftables\": [{\"set\": {\"name\": \"acct4\", \"elem\": 5}}]}")]
    public void AnythingElseIsNoTable(string json)
    {
        var table = FloodShieldRules.ParseTable(json);
        Assert.True(table is null || table.Accounting.Flows.Count == 0);
    }
}

/// <summary>Addresses typed by a person, before they go anywhere near nft or cscli.</summary>
public class IpNetworksTests
{
    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5", 4, true)]
    [InlineData(" 203.0.113.5 ", "203.0.113.5", 4, true)]
    [InlineData("203.0.113.0/24", "203.0.113.0/24", 4, false)]
    [InlineData("2001:DB8::1", "2001:db8::1", 6, true)]
    [InlineData("2001:db8::/48", "2001:db8::/48", 6, false)]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5", 4, true)]
    public void AddressesComeOutCanonical(string input, string text, int family, bool single)
    {
        var n = IpNetworks.Canonical(input)!;
        Assert.Equal(text, n.Text);
        Assert.Equal(family, n.Family);
        Assert.Equal(single, n.IsSingle);
    }

    [Theory]
    [InlineData("203.0.113.5/24")]      // host bits set: refused, not quietly widened
    [InlineData("203.0.113")]          // inet_aton shorthand
    [InlineData("1")]
    [InlineData("203.0.113.5; rm -rf /")]
    [InlineData("example.com")]
    [InlineData("203.0.113.0/33")]
    [InlineData("fe80::1%eth0")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? input) => Assert.Null(IpNetworks.Canonical(input));

    [Fact]
    public void ANetworkKnowsWhatIsInIt()
    {
        var n = IpNetworks.Canonical("198.51.100.0/24")!;
        Assert.True(n.Contains("198.51.100.77"));
        Assert.False(n.Contains("198.51.101.1"));
        Assert.False(n.Contains("2001:db8::1"));
        Assert.True(IpNetworks.Canonical("2001:db8::/48")!.Contains("2001:db8:0:1::5"));
    }

    [Fact]
    public void TheWidestAllowedNetworksAreOneRackNotAProvider()
    {
        Assert.True(IpNetworks.Canonical("198.51.100.0/24")!.NoWiderThan(IpNetworks.WidestTrusted4, IpNetworks.WidestTrusted6));
        Assert.False(IpNetworks.Canonical("198.51.0.0/16")!.NoWiderThan(IpNetworks.WidestTrusted4, IpNetworks.WidestTrusted6));
        Assert.False(IpNetworks.Canonical("0.0.0.0/0")!.NoWiderThan(IpNetworks.WidestTrusted4, IpNetworks.WidestTrusted6));
        Assert.False(IpNetworks.Canonical("2001:db8::/32")!.NoWiderThan(IpNetworks.WidestTrusted4, IpNetworks.WidestTrusted6));
    }
}

/// <summary>From the kernel's counters to what each source did in a minute, and what is suspicious.</summary>
public class TrafficAccountingTests
{
    private static Accounting Acct(
        (string Src, string Proto, int Port, long P, long B)[] flows,
        (string Src, int Port, long P)[]? syn = null,
        (string Src, long P)[]? dropped = null) =>
        new(
            flows.ToDictionary(f => new FlowKey(f.Src, f.Proto, f.Port), f => new Counter(f.P, f.B)),
            (syn ?? []).ToDictionary(s => new FlowKey(s.Src, "tcp", s.Port), s => new Counter(s.P, s.P * 60)),
            new Dictionary<FlowKey, Counter>(),
            (dropped ?? []).ToDictionary(d => d.Src, d => new Counter(d.P, d.P * 100)));

    [Fact]
    public void AWindowIsWhatTheCountersGrewBy()
    {
        var before = Acct([("203.0.113.5", "udp", 7777, 1000, 100_000)]);
        var after = Acct([("203.0.113.5", "udp", 7777, 4000, 400_000), ("203.0.113.5", "udp", 8177, 50, 500)]);

        var s = Assert.Single(TrafficAccounting.Between(before, after));

        Assert.Equal(3050, s.Packets);
        Assert.Equal(300_500, s.Bytes);
        Assert.Equal(3050, s.Udp);
        Assert.Equal(2, s.DistinctPorts);
        Assert.Equal(new PortCount("udp", 7777, 3000), s.TopPorts[0]);
    }

    [Fact]
    public void ACounterThatWentDownWasReCreatedAndCountsFromZero()
    {
        var before = Acct([("203.0.113.5", "udp", 7777, 5000, 500_000)]);
        var after = Acct([("203.0.113.5", "udp", 7777, 20, 2000)]);

        Assert.Equal(20, Assert.Single(TrafficAccounting.Between(before, after)).Packets);
    }

    [Fact]
    public void SourcesThatSentNothingAreLeftOutAndTheBusiestComeFirst()
    {
        var before = Acct([("10.0.0.1", "udp", 7777, 10, 10), ("10.0.0.2", "udp", 7777, 10, 10)]);
        var after = Acct([("10.0.0.1", "udp", 7777, 10, 10), ("10.0.0.2", "udp", 7777, 30, 30), ("10.0.0.3", "tcp", 22, 50, 50)],
            dropped: [("10.0.0.4", 500)]);

        Assert.Equal(["10.0.0.4", "10.0.0.3", "10.0.0.2"], TrafficAccounting.Between(before, after).Select(s => s.Source));
    }

    [Fact]
    public void TheFirstReadingCountsEverything()
    {
        var s = Assert.Single(TrafficAccounting.Between(null, Acct([("10.0.0.1", "tcp", 22, 7, 700)], syn: [("10.0.0.1", 22, 3)])));
        Assert.Equal(7, s.Tcp);
        Assert.Equal(3, s.Syn);
    }

    [Fact]
    public void TopPortsAreCapped()
    {
        var flows = Enumerable.Range(1, 40).Select(i => ("203.0.113.9", "udp", 1000 + i, (long)i, (long)i)).ToArray();

        var s = Assert.Single(TrafficAccounting.Between(null, Acct(flows)));

        Assert.Equal(TrafficAccounting.TopPortsKept, s.TopPorts.Count);
        Assert.Equal(40, s.DistinctPorts);
        Assert.Equal(1040, s.TopPorts[0].Port);
    }

    private static SourceWindow Window(string src, long udp = 0, long syn = 0, int ports = 1, long dropped = 0, bool player = false, bool trusted = false) =>
        new(src, udp, udp * 100, udp, 0, 0, [], ports, syn, dropped) { Player = player, Trusted = trusted };

    [Fact]
    public void DetectionsNameWhatWasOverWhichThreshold()
    {
        var found = TrafficDetector.Judge(
            [Window("203.0.113.5", udp: 60 * 400), Window("203.0.113.6", syn: 500), Window("203.0.113.7", ports: 80), Window("203.0.113.8", dropped: 20_000)],
            TimeSpan.FromMinutes(1), new DetectionThresholds());

        Assert.Equal(["udp-rate", "syn-burst", "port-spread", "dropped"], found.Select(d => d.Category));
        Assert.Equal(400, found[0].Observed);
        Assert.False(found[3].Escalates);
    }

    [Fact]
    public void APlayersUdpRateIsNeverSuspiciousAndTrustedAddressesAreNeverJudged()
    {
        var found = TrafficDetector.Judge(
            [Window("10.1.1.1", udp: 60 * 2000, ports: 100, player: true), Window("10.2.2.2", udp: 60 * 2000, syn: 9999, trusted: true)],
            TimeSpan.FromMinutes(1), new DetectionThresholds());

        Assert.Empty(found);
    }

    [Fact]
    public void APlayerOpeningConnectionsInABurstIsStillNoted() =>
        Assert.Equal("syn-burst", Assert.Single(TrafficDetector.Judge([Window("10.1.1.1", syn: 900, player: true)], TimeSpan.FromMinutes(1),
            new DetectionThresholds())).Category);
}

/// <summary>Graduated response: alert once, ban only on repeated evidence, and never the wrong people.</summary>
public class ResponsePlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static Detection Udp(string src) => new("udp-rate", src, 900, 300, "900/s");

    private static IReadOnlyList<PlannedResponse> Minute(ResponsePlanner p, int minute, string src, int sources = 10, bool exempt = false) =>
        p.Plan(T0.AddMinutes(minute), [Udp(src)], sources, _ => exempt);

    [Fact]
    public void OneIncidentIsOneAlertAndABanNeedsThreeMinutes()
    {
        var planner = new ResponsePlanner(new ResponsePolicy());

        var first = Assert.Single(Minute(planner, 0, "203.0.113.5"));
        var second = Assert.Single(Minute(planner, 1, "203.0.113.5"));
        var third = Assert.Single(Minute(planner, 2, "203.0.113.5"));

        Assert.True(first.Alert);
        Assert.Null(first.Ban);
        Assert.False(second.Alert);
        Assert.Null(second.Ban);
        Assert.Equal(TimeSpan.FromHours(1), third.Ban);
        Assert.Equal(first.Incident, third.Incident);
    }

    [Fact]
    public void AQuietWindowEndsTheIncident()
    {
        var planner = new ResponsePlanner(new ResponsePolicy());
        var first = Assert.Single(Minute(planner, 0, "203.0.113.5"));
        var later = Assert.Single(Minute(planner, 20, "203.0.113.5"));

        Assert.True(later.Alert);
        Assert.NotEqual(first.Incident, later.Incident);
        Assert.Null(later.Ban);   // the minute from twenty minutes ago no longer counts
    }

    [Fact]
    public void PlayersAndTrustedAreNeverBannedAutomatically()
    {
        var planner = new ResponsePlanner(new ResponsePolicy());
        PlannedResponse last = null!;
        for (var m = 0; m < 5; m++) last = Assert.Single(Minute(planner, m, "10.1.1.1", exempt: true));

        Assert.Null(last.Ban);
        Assert.Contains("never banned automatically", last.Held, StringComparison.Ordinal);
    }

    [Fact]
    public void ALikelySpoofedFloodGetsNoPerAddressBans()
    {
        var planner = new ResponsePlanner(new ResponsePolicy());
        PlannedResponse last = null!;
        for (var m = 0; m < 3; m++) last = Assert.Single(Minute(planner, m, "203.0.113.5", sources: 40_000));

        Assert.Null(last.Ban);
        Assert.Contains("likely spoofed", last.Held, StringComparison.Ordinal);
    }

    [Fact]
    public void BansAreCappedPerHour()
    {
        var planner = new ResponsePlanner(new ResponsePolicy(MaxBansPerHour: 2));
        var bans = 0;
        for (var i = 0; i < 4; i++)
        {
            var src = $"203.0.113.{i + 1}";
            for (var m = 0; m < 3; m++)
                if (Assert.Single(Minute(planner, m, src)).Ban is not null) bans++;
        }

        Assert.Equal(2, bans);
    }

    [Fact]
    public void ARepeatOffendersBanDoublesUpToTheCap()
    {
        var planner = new ResponsePlanner(new ResponsePolicy(LongestBan: TimeSpan.FromHours(3)));

        Assert.Equal(TimeSpan.FromHours(1), planner.Length(0));
        Assert.Equal(TimeSpan.FromHours(2), planner.Length(1));
        Assert.Equal(TimeSpan.FromHours(3), planner.Length(2));
        Assert.Equal(TimeSpan.FromHours(3), planner.Length(40));
    }

    [Fact]
    public void WithAutomaticBansOffItOnlyAlerts()
    {
        var planner = new ResponsePlanner(new ResponsePolicy(AutoBan: false));
        PlannedResponse last = null!;
        for (var m = 0; m < 3; m++) last = Assert.Single(Minute(planner, m, "203.0.113.5"));

        Assert.Null(last.Ban);
        Assert.Equal("automatic bans are off", last.Held);
    }

    [Fact]
    public void ADropAloneNeverEscalates()
    {
        var planner = new ResponsePlanner(new ResponsePolicy());
        PlannedResponse last = null!;
        for (var m = 0; m < 5; m++)
            last = Assert.Single(planner.Plan(T0.AddMinutes(m), [new Detection("dropped", "203.0.113.5", 1, 1, "x")], 10, _ => false));

        Assert.Null(last.Ban);
        Assert.Null(last.Held);
    }
}

/// <summary>The log line formats, written and read back.</summary>
public class AntiDdosRecordsTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 29, 17, 31, 0, TimeSpan.Zero);

    [Fact]
    public void ASourceRecordIsOneLineOfJson()
    {
        var s = new SourceWindow("203.0.113.5", 6000, 720_000, 5990, 10, 0, [new PortCount("udp", 7777, 5990)], 2, 3, 120) { Player = false };

        Assert.Equal(
            "{\"v\":1,\"type\":\"source\",\"ts\":\"2026-10-29T17:31:00Z\",\"window_s\":60,\"src\":\"203.0.113.5\",\"packets\":6000,\"bytes\":720000," +
            "\"pps\":100,\"udp\":5990,\"tcp\":10,\"other\":0,\"syn\":3,\"dropped\":120,\"distinct_ports\":2," +
            "\"top_ports\":[{\"proto\":\"udp\",\"port\":7777,\"packets\":5990}],\"player\":false,\"trusted\":false}",
            AntiDdosRecords.Source(T, TimeSpan.FromMinutes(1), s));
    }

    [Fact]
    public void AnEventRecordCarriesOnlyWhatItHas()
    {
        var line = AntiDdosRecords.Event(new AntiDdosEvent(T, "ban", "warning", "203.0.113.5", "banned", "udp-rate+syn-burst <x3>",
            T.AddHours(1), "auto", "src-202610291731-203.0.113.5", new Dictionary<string, string> { ["via"] = "crowdsec" }));

        Assert.Equal(
            "{\"v\":1,\"type\":\"event\",\"ts\":\"2026-10-29T17:31:00Z\",\"category\":\"ban\",\"severity\":\"warning\",\"src\":\"203.0.113.5\"," +
            "\"action\":\"banned\",\"reason\":\"udp-rate+syn-burst <x3>\",\"expires\":\"2026-10-29T18:31:00Z\",\"actor\":\"auto\"," +
            "\"incident\":\"src-202610291731-203.0.113.5\",\"detail\":{\"via\":\"crowdsec\"}}",
            line);
        Assert.Equal("{\"v\":1,\"type\":\"event\",\"ts\":\"2026-10-29T17:31:00Z\",\"category\":\"mode\",\"severity\":\"info\"}",
            AntiDdosRecords.Event(new AntiDdosEvent(T, "mode", "info")));
    }

    [Fact]
    public void EachKindReadsBack()
    {
        var source = AntiDdosRecords.Parse(AntiDdosRecords.Source(T, TimeSpan.FromMinutes(1),
            new SourceWindow("203.0.113.5", 10, 1000, 10, 0, 0, [new PortCount("udp", 7777, 9), new PortCount("udp", 8177, 1)], 2, 0, 0)))!;
        Assert.Equal(RecordKind.Source, source.Kind);
        Assert.Equal([7777, 8177], source.Ports);
        Assert.Equal(T, source.Time);

        var ev = AntiDdosRecords.Parse(AntiDdosRecords.Event(new AntiDdosEvent(T, "ban", "warning", "203.0.113.5", "banned")))!;
        Assert.Equal((RecordKind.Event, "ban", "banned", "203.0.113.5"), (ev.Kind, ev.Category, ev.Action, ev.Source));

        Assert.Null(AntiDdosRecords.Parse("not json"));
        Assert.Null(AntiDdosRecords.Parse("{\"type\":\"source\"}"));   // no timestamp
    }

    [Fact]
    public void UlogdPacketRecordsReadAsUtcWithTheirVerdictFromThePrefix()
    {
        var packets = AntiDdosFixtures.Read("ulogd-packets.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(AntiDdosRecords.Parse).ToList();

        Assert.All(packets, p => Assert.Equal(RecordKind.Packet, p!.Kind));
        var blocked = packets[0]!;
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793295072).AddTicks(924990 * 10), blocked.Time);
        Assert.Equal(("dropped", "blocked", "udp", 7781, "lo", (long?)48), (blocked.Action, blocked.Category, blocked.Protocol, blocked.DestinationPort, blocked.InInterface, blocked.Length));
        Assert.Equal(("passed", "sample:game"), (packets[3]!.Action, packets[3]!.Category));

        var syn = packets[4]!;
        Assert.Equal(("passed", "suspect:syn-burst", "tcp", "syn"), (syn.Action, syn.Category, syn.Protocol, syn.Flags));
    }

    [Theory]
    [InlineData("pbd drop per-source ", "dropped", "per-source")]
    [InlineData("pbd suspect udp-rate ", "passed", "suspect:udp-rate")]
    [InlineData("[UFW BLOCK] ", null, "[UFW BLOCK]")]
    public void PrefixesSayWhatHappened(string prefix, string? action, string category) =>
        Assert.Equal((action, category), AntiDdosRecords.FromPrefix(prefix));
}

/// <summary>Searching the logs after the fact.</summary>
public class LogQueryTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 29, 17, 0, 0, TimeSpan.Zero);

    private static LogRecord Source(int minute, string src, long packets, int port = 7777, long dropped = 0, bool player = false) =>
        AntiDdosRecords.Parse(AntiDdosRecords.Source(T.AddMinutes(minute), TimeSpan.FromMinutes(1),
            new SourceWindow(src, packets, packets * 100, packets, 0, 0, [new PortCount("udp", port, packets)], 1, 0, dropped) { Player = player }))!;

    private static readonly QueryFilter Everything = new(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

    [Fact]
    public void FiltersCombine()
    {
        var records = new[] { Source(0, "203.0.113.5", 10), Source(1, "203.0.113.5", 20, port: 22), Source(2, "198.51.100.9", 30), Source(30, "203.0.113.5", 40) };

        var (found, truncated) = LogQuery.Find(records,
            new QueryFilter(T, T.AddMinutes(10), IpNetworks.Canonical("203.0.113.0/24"), Port: 7777, Protocol: "udp"), 100);

        Assert.Equal([10L], found.Select(r => r.Packets));
        Assert.False(truncated);
    }

    [Fact]
    public void AResultIsCutAtItsLimitAndSaysSo()
    {
        var records = Enumerable.Range(0, 50).Select(i => Source(i, "203.0.113.5", i + 1));

        var (found, truncated) = LogQuery.Find(records, Everything, 10);

        Assert.Equal(10, found.Count);
        Assert.True(truncated);
    }

    [Fact]
    public void TheBusiestSourcesAddUpAcrossWindows()
    {
        var records = new[] { Source(0, "203.0.113.5", 100), Source(1, "203.0.113.5", 300, dropped: 50), Source(1, "10.1.1.1", 200, player: true) };

        var top = LogQuery.TopSources(records, Everything, 10);

        Assert.Equal(("203.0.113.5", 400L, 50L, 2), (top[0].Source, top[0].Packets, top[0].Dropped, top[0].Windows));
        Assert.Equal(5, top[0].PeakPps);
        Assert.True(top[1].Player);
    }

    [Fact]
    public void RatesComeFromTrafficRecords()
    {
        var line = AntiDdosRecords.Traffic(new TrafficWindow(T, TimeSpan.FromMinutes(1), "ens3", 600_000, 75_000_000, 25_000,
            new Dictionary<string, Counter> { ["udp"] = new(500_000, 1), ["blocked"] = new(90_000, 1), ["per-source"] = new(10, 1) },
            900, 100, false, LogMode.Incident, true, "ok"));

        var point = Assert.Single(LogQuery.Rates([AntiDdosRecords.Parse(line)!], Everything));

        Assert.Equal((10_000.0, 10.0, 25_000L, 500_000L, 90_010L), (point.Pps, point.Mbps, point.PeakPps, point.UdpPackets, point.Dropped));
    }
}

/// <summary>Deciding what the disk can afford.</summary>
public class StoragePolicyTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly StorageLimits Limits = new(MaxLogBytes: 2 * Gb, WarnFreePercent: 20, PressureFreePercent: 10, ReserveBytes: 2 * Gb);

    [Theory]
    [InlineData(50, 100, 0.1, StorageLevel.Ok, LogMode.Incident, true)]
    [InlineData(15, 100, 0.1, StorageLevel.Warning, LogMode.Incident, true)]
    [InlineData(50, 100, 1.7, StorageLevel.Warning, LogMode.Incident, true)]
    [InlineData(8, 100, 0.1, StorageLevel.Pressure, LogMode.Minimal, false)]
    [InlineData(50, 100, 2.0, StorageLevel.Pressure, LogMode.Minimal, false)]
    [InlineData(1.5, 10, 0.1, StorageLevel.Pressure, LogMode.Minimal, false)]   // 15% free, but under the 2 GB reserve
    [InlineData(0.5, 10, 0.1, StorageLevel.Critical, LogMode.Off, false)]
    public void TheLevelFollowsTheDisk(double freeGb, double totalGb, double logGb, StorageLevel level, LogMode most, bool capture)
    {
        var v = StoragePolicy.Judge((long)(freeGb * Gb), (long)(totalGb * Gb), (long)(logGb * Gb), Limits);

        Assert.Equal((level, most, capture), (v.Level, v.MostLogging, v.CaptureAllowed));
    }

    [Fact]
    public void EvictionTakesTheOldestRotatedFilesAndEventsLast()
    {
        var t = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var files = new[]
        {
            ("packets.jsonl", 500L, t.AddDays(9), true, false),
            ("events.jsonl-1.gz", 100L, t, false, true),
            ("packets.jsonl-1.gz", 300L, t.AddDays(1), false, false),
            ("packets.jsonl-2.gz", 300L, t.AddDays(2), false, false),
        };

        Assert.Equal(["packets.jsonl-1.gz", "packets.jsonl-2.gz"], StoragePolicy.Evict(files, 600));
        Assert.Equal(["packets.jsonl-1.gz", "packets.jsonl-2.gz", "events.jsonl-1.gz"], StoragePolicy.Evict(files, 0));
    }
}

/// <summary>What cscli prints, read back.</summary>
public class CrowdSecOutputTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public void DecisionsFromEveryOriginAreRead()
    {
        var decisions = CrowdSecOutput.Decisions(AntiDdosFixtures.Read("cscli-decisions.json"), Now);

        var scan = decisions.Single(d => d.Scenario == "crowdsecurity/iptables-scan-multi_ports");
        Assert.Equal(("198.51.100.88", "Ip", "ban", "crowdsec"), (scan.Value, scan.Scope, scan.Type, scan.Origin));
        Assert.True(scan.Until > Now.AddHours(3));
        Assert.Contains(decisions, d => d.Value == "2001:db8::5" && d.Origin == "cscli");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("level=fatal msg=\"no LAPI\"")]
    [InlineData("{}")]
    public void NothingOrAnErrorIsNoDecisions(string json) => Assert.Empty(CrowdSecOutput.Decisions(json, Now));

    [Fact]
    public void AlertsBouncersAndMetricsAreRead()
    {
        var alerts = CrowdSecOutput.Alerts(AntiDdosFixtures.Read("cscli-alerts.json"));
        Assert.Contains(alerts, a => a.Scenario == "crowdsecurity/ssh-bf" && a.Source == "198.51.100.77");

        var bouncer = Assert.Single(CrowdSecOutput.Bouncers(AntiDdosFixtures.Read("cscli-bouncers.json")));
        Assert.StartsWith("cs-firewall-bouncer", bouncer.Name, StringComparison.Ordinal);
        Assert.False(bouncer.Revoked);

        var source = Assert.Single(CrowdSecOutput.Acquisition(AntiDdosFixtures.Read("cscli-metrics.json")));
        Assert.Equal(("file:/var/log/auth.log", 8L, 8L, false), (source.Name, source.Reads, source.Parsed, source.Failing));
        Assert.True(new CrowdSecSource("file:/var/log/x", 100, 0, 100).Failing);
    }

    [Theory]
    [InlineData("3h59m48s", 14_388)]
    [InlineData("59m59s", 3_599)]
    [InlineData("87599h59m59s", 315_359_999)]
    [InlineData("1.5s", 1.5)]
    [InlineData("250ms", 0.25)]
    [InlineData("-5s", -5)]
    public void GoDurationsAreRead(string text, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), CrowdSecOutput.Duration(text));

    [Theory]
    [InlineData("forever")]
    [InlineData("")]
    [InlineData("5d")]
    public void AnythingElseIsNoDuration(string text) => Assert.Null(CrowdSecOutput.Duration(text));

    [Fact]
    public void DurationsGoToCscliInHoursOrMinutes()
    {
        Assert.Equal("4h", CrowdSecOutput.DurationArgument(TimeSpan.FromHours(4)));
        Assert.Equal("90m", CrowdSecOutput.DurationArgument(TimeSpan.FromMinutes(90)));
        Assert.Equal("87600h", CrowdSecOutput.DurationArgument(TimeSpan.FromDays(3650)));
    }
}
