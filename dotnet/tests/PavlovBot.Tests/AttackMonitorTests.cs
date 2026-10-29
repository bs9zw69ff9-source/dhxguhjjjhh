using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Network;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Network;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>Reading tshark, deciding what is an attack, and the numbers in the report.</summary>
/// <remarks>The sample lines and statistics are copied from tshark 4.2.2 captures.</remarks>
public class FloodAnalysisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 13, 50, 34, TimeSpan.Zero);

    // ---- tshark fields ----

    [Fact]
    public void AUdpLine()
    {
        var p = CapturedPacket.ParseFields("203.0.113.5,17,7777,,1208,,,,1228,0")!.Value;

        Assert.Equal("203.0.113.5", CapturedPacket.Address(p.Source));
        Assert.Equal(7777, p.DestinationPort);
        Assert.Equal(PacketProtocol.Udp, p.Protocol);
        Assert.Equal(1200, p.Payload);   // udp.length includes UDP's 8-byte header
        Assert.False(p.Syn);
    }

    [Theory]
    [InlineData("203.0.113.5,6,,22,,0,True,False,60,0", true)]       // tshark 4.x
    [InlineData("203.0.113.5,6,,22,,0,1,0,60,0", true)]              // older releases print 1/0
    [InlineData("34.149.66.165,6,,50860,,0,False,True,52,0", false)]  // a bare ACK, from a real capture
    [InlineData("10.0.0.9,6,,443,,0,True,True,60,0", false)]          // SYN-ACK: a reply, not a new connection
    public void TcpOnlyABareSynIsASyn(string line, bool syn)
    {
        var p = CapturedPacket.ParseFields(line)!.Value;

        Assert.Equal(PacketProtocol.Tcp, p.Protocol);
        Assert.Equal(syn, p.Syn);
    }

    [Fact]
    public void AnIcmpErrorIsNotTheUdpPacketItQuotes()
    {
        /* From a real capture: an ICMP port-unreachable carries the UDP header it is about, and
           tshark fills in that quoted port and length. It is ICMP, not UDP traffic to 7779. */
        var p = CapturedPacket.ParseFields("127.0.0.1,1,7779,,1224,,,,576,0")!.Value;

        Assert.Equal(PacketProtocol.Icmp, p.Protocol);
        Assert.Equal(0, p.DestinationPort);
        Assert.Equal(556, p.Payload);
    }

    [Fact]
    public void ANonFirstFragmentHasNoPorts()
    {
        var p = CapturedPacket.ParseFields("198.51.100.7,17,,,,,,,1500,185")!.Value;

        Assert.Equal(PacketProtocol.Other, p.Protocol);
        Assert.Equal(0, p.DestinationPort);
    }

    [Theory]
    [InlineData("Running as user \"root\" and group \"root\". This could be dangerous.")]
    [InlineData("Capturing on 'eth0'")]
    [InlineData("999.1.1.1,17,7777,,40,,,,60,0")]
    [InlineData("203.0.113.5,17,notaport,,40,,,,60,0")]
    [InlineData("203.0.113.5,17,7777")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotAPacket(string? line) => Assert.Null(CapturedPacket.ParseFields(line));

    [Fact]
    public void TsharksClosingStatisticsAreRead()
    {
        var health = TsharkCapture.Health(
            "Running as user \"root\" and group \"root\". This could be dangerous.\n" +
            "Capturing on 'eth0'\n2465096 packets captured\n4904 packets dropped from eth0\n");

        Assert.Equal(2_465_096, health.Captured);
        Assert.Equal(4_904, health.DroppedByKernel);
        Assert.InRange(health.Completeness, 0.997, 0.999);
    }

    [Fact]
    public void TheStreamCopyDissectsOnlyWhatTheBreakdownReads()
    {
        var argv = TsharkCapture.StreamArgv("eth0");

        Assert.Equal("inbound and ip", argv[argv.ToList().IndexOf("-f") + 1]);
        Assert.Equal("128", argv[argv.ToList().IndexOf("-s") + 1]);
        Assert.Contains("--disable-all-protocols", argv);
        Assert.Equal("frame,eth,sll,ip,udp,tcp,icmp", argv[argv.ToList().IndexOf("--enable-protocol") + 1]);
        Assert.Contains("occurrence=f", argv);   // outer values only, never a quoted packet's
        Assert.Equal(CapturedPacket.TsharkFields, argv.Where((a, i) => i > 0 && argv[i - 1] == "-e"));
    }

    [Fact]
    public void ThePcapCopyIsCappedAndWritesWhereItIsTold()
    {
        var argv = TsharkCapture.PcapArgv("eth0", "/root/pavlov-bot/data/attacks/attack-1.pcap");

        Assert.Equal(TsharkCapture.PcapPacketLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), argv[argv.ToList().IndexOf("-c") + 1]);
        Assert.Equal("/root/pavlov-bot/data/attacks/attack-1.pcap", argv[argv.ToList().IndexOf("-w") + 1]);
    }

    [Fact]
    public void TheStatisticsAreWiresharksMenuInOnePass()
    {
        var argv = TsharkCapture.StatisticsArgv("/x/attack.pcap");

        Assert.Equal(["io,phs", "endpoints,ip", "conv,ip", "conv,udp"], argv.Where((a, i) => i > 0 && argv[i - 1] == "-z"));
        Assert.Contains("--disable-all-protocols", argv);
    }

    [Fact]
    public void BigTablesAreCutToTheirBusiestRowsAndTheHierarchyIsKept()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"203.0.113.{i}            {1000 - i}     {(1000 - i) * 1200}"));
        var statistics =
            "================================================================================\n" +
            "IPv4 Endpoints\nFilter:<No Filter>\n" +
            "                       |  Packets  | |  Bytes  |\n" + rows + "\n" +
            "================================================================================\n" +
            "===================================================================\n" +
            "Protocol Hierarchy Statistics\nFilter: \n\n" +
            "eth                                      frames:200000 bytes:178153300\n" +
            "  ip                                     frames:200000 bytes:178153300\n" +
            "    udp                                  frames:100000 bytes:119153300\n" +
            "===================================================================\n";

        var trimmed = TsharkCapture.Trim(statistics, rows: 5);

        Assert.Contains("203.0.113.5 ", trimmed, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.6 ", trimmed, StringComparison.Ordinal);
        Assert.Contains("… 35 more row(s)", trimmed, StringComparison.Ordinal);
        Assert.Contains("    udp", trimmed, StringComparison.Ordinal);
        Assert.Contains("IPv4 Endpoints", trimmed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eth0", true)]
    [InlineData("enp3s0", true)]
    [InlineData("bond0.100", true)]
    [InlineData("-i", false)]
    [InlineData("../etc", false)]
    [InlineData("eth0 and", false)]
    [InlineData("averyveryverylongname", false)]
    public void AnInterfaceIsOneSafeName(string name, bool ok) => Assert.Equal(ok, LinkCounters.IsPlausibleInterface(name));

    // ---- the detector ----

    private static LinkSecond S(int t, long pps, long bytes = 0) => new(T0.AddSeconds(t), pps, bytes);

    [Fact]
    public void ThreeSecondsOverStartsAnAttack()
    {
        var detector = new FloodDetector(new FloodThresholds(Pps: 10_000));

        Assert.Equal(FloodSignal.Watch, detector.Observe(S(0, 50_000)));
        Assert.Equal(FloodSignal.Watch, detector.Observe(S(1, 50_000)));
        Assert.Equal(FloodSignal.Started, detector.Observe(S(2, 50_000)));
        Assert.True(detector.InAttack);
        Assert.Equal(T0, detector.RunStartedAt);
    }

    [Fact]
    public void OneSpikeIsNotAnAttack()
    {
        var detector = new FloodDetector(new FloodThresholds(Pps: 10_000));

        detector.Observe(S(0, 50_000));
        Assert.Equal(FloodSignal.Quiet, detector.Observe(S(1, 100)));
        Assert.False(detector.InAttack);
        Assert.Null(detector.RunStartedAt);
    }

    [Fact]
    public void ItEndsAfterFifteenQuietSecondsAndAPulseDoesNotSplitIt()
    {
        var detector = new FloodDetector(new FloodThresholds(Pps: 10_000));
        for (var t = 0; t < 3; t++) detector.Observe(S(t, 50_000));

        for (var t = 3; t < 13; t++) Assert.Equal(FloodSignal.Ongoing, detector.Observe(S(t, 100)));
        Assert.Equal(FloodSignal.Ongoing, detector.Observe(S(13, 50_000)));   // the pulse comes back

        for (var t = 14; t < 28; t++) Assert.Equal(FloodSignal.Ongoing, detector.Observe(S(t, 100)));
        Assert.Equal(FloodSignal.Ended, detector.Observe(S(28, 100)));
        Assert.False(detector.InAttack);
    }

    [Fact]
    public void ABusyBoxNeedsMoreThanTheFloor()
    {
        var detector = new FloodDetector(new FloodThresholds(Pps: 10_000, BaselineFactor: 4));
        for (var t = 0; t < 60; t++) detector.Observe(S(t, 8_000));   // three full servers

        Assert.Equal(32_000, detector.TriggerPps);
        Assert.False(detector.IsOver(S(60, 15_000)));
    }

    [Fact]
    public void ALongAttackDoesNotBecomeTheNewNormal()
    {
        var detector = new FloodDetector(new FloodThresholds(Pps: 10_000));
        for (var t = 0; t < 600; t++) detector.Observe(S(t, 50_000));

        Assert.Equal(10_000, detector.TriggerPps);
    }

    [Fact]
    public void BandwidthAloneCountsToo()
    {
        var detector = new FloodDetector(new FloodThresholds(Pps: 1_000_000, Mbps: 200));

        Assert.True(detector.IsOver(S(0, 20_000, bytes: 60_000_000)));   // 480 Mbit/s of big packets
    }

    // ---- the breakdown ----

    private static CapturedPacket Udp(string from, int size, ushort port = 7777) =>
        new(CapturedPacket.Number(from)!.Value, port, PacketProtocol.Udp, size);

    [Fact]
    public void PlayersAreKeptOutOfTheAttackFigures()
    {
        var recorder = new FloodRecorder();
        recorder.Add(Udp("203.0.113.5", 1300), fromPlayer: false);
        recorder.Add(Udp("10.1.1.1", 80), fromPlayer: true);

        var d = recorder.Snapshot();
        Assert.Equal(1, d.AttackPackets);
        Assert.Equal(1, d.PlayerPackets);
        Assert.Equal(1, d.DistinctSources);
    }

    [Fact]
    public void AFloodFromMillionsOfAddressesCannotGrowTheTableForever()
    {
        var recorder = new FloodRecorder(maxSources: 10);
        for (uint i = 0; i < 1000; i++) recorder.Add(new CapturedPacket(i + 1, 7777, PacketProtocol.Udp, 500), false);

        var d = recorder.Snapshot();
        Assert.Equal(10, d.DistinctSources);
        Assert.True(d.SourcesCapped);
        Assert.Equal(1000, d.AttackPackets);
    }

    [Fact]
    public void SizesPortsAndTopSourcesAreCounted()
    {
        var recorder = new FloodRecorder();
        foreach (var size in new[] { 228, 228, 700, 1400, 1400, 1400 }) recorder.Add(Udp("203.0.113.5", size), false);
        recorder.Add(Udp("198.51.100.1", 1300, port: 7778), false);

        var d = recorder.Snapshot();
        Assert.Equal(228, d.MinSize);
        Assert.Equal(1400, d.MaxSize);
        Assert.Equal(4, d.DistinctSizes);
        Assert.Equal((ushort)7777, d.TopPorts[0].Port);
        Assert.Equal("203.0.113.5", d.TopSources[0].Address);
        Assert.Equal(4, d.Bands.Single(b => b.Label == ">1024 B").Packets);
    }

    [Theory]
    [InlineData(PacketProtocol.Udp, 1300, false, "Big-packet UDP flood")]
    [InlineData(PacketProtocol.Udp, 60, false, "Small-packet UDP flood")]
    [InlineData(PacketProtocol.Udp, 300, false, "UDP flood")]
    [InlineData(PacketProtocol.Tcp, 0, true, "TCP SYN flood")]
    [InlineData(PacketProtocol.Tcp, 100, false, "TCP flood")]
    [InlineData(PacketProtocol.Icmp, 64, false, "ICMP flood")]
    [InlineData(PacketProtocol.Other, 0, false, "Fragment flood")]
    public void ThePatternIsNamed(PacketProtocol protocol, int size, bool syn, string pattern)
    {
        var recorder = new FloodRecorder();
        for (uint i = 0; i < 100; i++) recorder.Add(new CapturedPacket(i + 1, 7777, protocol, size, syn), false);

        Assert.Equal(pattern, recorder.Snapshot().Pattern);
    }

    [Fact]
    public void NoProtocolOverEightyPercentIsMixed()
    {
        var recorder = new FloodRecorder();
        for (uint i = 0; i < 50; i++) recorder.Add(new CapturedPacket(i + 1, 7777, PacketProtocol.Udp, 900), false);
        for (uint i = 0; i < 50; i++) recorder.Add(new CapturedPacket(i + 1, 80, PacketProtocol.Tcp, 0, true), false);

        Assert.Equal("Mixed flood", recorder.Snapshot().Pattern);
    }

    // ---- the report ----

    internal static AttackReport Report(bool captured = true, int seconds = 30)
    {
        var recorder = new FloodRecorder();
        for (uint i = 0; i < 4650; i++) recorder.Add(new CapturedPacket(0xCB007100 + i, 7777, PacketProtocol.Udp, 228 + (int)(i % 1172)), false);
        recorder.Add(Udp("10.1.1.1", 80), fromPlayer: true);

        var timeline = Enumerable.Range(0, seconds)
            .Select(t => S(t, t is > 5 and < 25 ? 46_973 : 900, t is > 5 and < 25 ? 56_750_000 : 400_000))
            .ToList();

        return new AttackReport("shack-04", "eth0", T0, T0.AddSeconds(seconds), timeline, ActiveSeconds: 19,
            captured ? recorder.Snapshot() : FloodBreakdown.Empty,
            new PlayerImpact(28, 26, 28, 27),
            captured ? new CaptureHealth(null, 2_465_096, 0) : new CaptureHealth("the bot is not running as root, so tcpdump cannot capture"),
            captured ? "/root/pavlov-bot/data/attacks/attack-20261018-135034.pcap" : null);
    }

    [Fact]
    public void TheVerdictSaysWhatWhereHowLongAndHowHard()
    {
        var verdict = Report().Verdict;

        Assert.Contains("Big-packet UDP flood on shack-04", verdict, StringComparison.Ordinal);
        Assert.Contains("19s", verdict, StringComparison.Ordinal);
        Assert.Contains("46,973 pkt/s", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAverageIsOverTheFloodingNotTheQuietTail()
    {
        var report = Report();

        Assert.InRange(report.AverageActiveMbps, 453, 455);   // 56.75 MB/s of the active seconds, not the idle ones
    }

    [Fact]
    public void TheTextReportHoldsTheAddressesAndTheTimeline()
    {
        var text = Report().ToText();

        Assert.Contains("top 50 sources", text, StringComparison.Ordinal);
        Assert.Contains("203.0.113.0", text, StringComparison.Ordinal);
        Assert.Contains("timeline (packets/s, Mbit/s)", text, StringComparison.Ordinal);
        Assert.Contains("/packettools analyze reads it again", text, StringComparison.Ordinal);
        Assert.Contains("online 28 before · 26 lowest · 28 after", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutACaptureTheReportSaysWhyInsteadOfInventingABreakdown()
    {
        var report = Report(captured: false);

        Assert.Equal("Flood", report.Pattern);
        Assert.Contains("breakdown       none - the bot is not running as root", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSparklineFitsItsWidthAndPeaksAtTheTop()
    {
        var line = AttackReport.Sparkline([.. Enumerable.Range(0, 400).Select(i => (long)i)], width: 40);

        Assert.Equal(40, line.Length);
        Assert.Equal('█', line[^1]);
        Assert.Equal('▁', line[0]);
    }

    // ---- who got knocked off ----

    [Fact]
    public void DropsAndRejoinsAreRecordedInOrder()
    {
        var tracker = new PlayerDropTracker();
        tracker.Baseline(["Alice", "Bob", "Cara"]);

        tracker.Observe(["Alice", "Cara"], T0.AddSeconds(5), fresh: true);
        tracker.Observe(["Alice"], T0.AddSeconds(9), fresh: true);
        tracker.Observe(["alice", "BOB"], T0.AddSeconds(30), fresh: true);   // names compare without case

        var drops = tracker.Drops;
        Assert.Equal(["Bob", "Cara"], drops.Select(d => d.Name));
        Assert.Equal(T0.AddSeconds(30), drops[0].Back);
        Assert.Null(drops[1].Back);
    }

    [Fact]
    public void SomebodyWhoJoinsDuringTheAttackAndDropsCounts()
    {
        var tracker = new PlayerDropTracker();
        tracker.Baseline(["Alice"]);
        tracker.Observe(["Alice", "Dan"], T0.AddSeconds(1), fresh: true);
        tracker.Observe(["Alice"], T0.AddSeconds(2), fresh: true);

        Assert.Equal("Dan", Assert.Single(tracker.Drops).Name);
    }

    [Fact]
    public void AStaleRosterIsNotCompared()
    {
        var tracker = new PlayerDropTracker();
        tracker.Baseline(["Alice", "Bob"]);
        tracker.Observe([], T0.AddSeconds(1), fresh: false);
        tracker.Observe(["Alice", "Bob"], T0.AddSeconds(2), fresh: true);

        Assert.Empty(tracker.Drops);
        Assert.Equal(1, tracker.StaleSeconds);
    }

    [Fact]
    public void ANewBaselineForgetsTheLastAttack()
    {
        var tracker = new PlayerDropTracker();
        tracker.Baseline(["Alice"]);
        tracker.Observe([], T0, fresh: true);
        tracker.Baseline(["Alice"]);

        Assert.Empty(tracker.Drops);
    }

    [Fact]
    public void TwentyDropsStayInsideTheFieldLimit()
    {
        var drops = Enumerable.Range(1, 40)
            .Select(i => new PlayerDrop($"A-Very-Long-Player-Name-Number-{i:00}-xxxxxxxx", T0.AddSeconds(i), i % 2 == 0 ? T0.AddSeconds(i + 30) : null))
            .ToList();
        var report = Report() with { Players = new PlayerImpact(40, 0, 20, 40, drops) };

        var embed = AttackEmbeds.Summary(report).Build();
        var field = Assert.Single(embed.Fields, f => f.Name.StartsWith("Players dropped", StringComparison.Ordinal));

        Assert.True(field.Value.Length <= 1024, $"{field.Value.Length} characters");
        Assert.Contains("and 25 more", field.Value, StringComparison.Ordinal);
        Assert.Contains("40 player(s) dropped", report.Verdict, StringComparison.Ordinal);
        Assert.True(embed.Length <= 6000);
    }

    // ---- the embeds ----

    [Fact]
    public void TheSummaryFitsDiscordsLimitsAndCarriesNoAddresses()
    {
        var embed = AttackEmbeds.Summary(Report(seconds: 4 * 60 * 60)).Build();

        Assert.True(embed.Length <= 6000, $"embed is {embed.Length} characters");
        Assert.All(embed.Fields, f => Assert.True(f.Value.Length <= 1024, $"{f.Name} is {f.Value.Length}"));
        Assert.DoesNotContain("203.0.113", embed.Description + string.Concat(embed.Fields.Select(f => f.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public void ASummaryWithoutACaptureSaysWhy()
    {
        var embed = AttackEmbeds.Summary(Report(captured: false)).Build();

        Assert.Contains(embed.Fields, f => f.Name == "Breakdown" && f.Value.Contains("not running as root", StringComparison.Ordinal));
    }

    // ---- reading the kernel's counters ----

    [Fact]
    public void CountersAndTheDefaultRouteAreReadFromTheKernelFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pavlovbot-net-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stats = Path.Combine(root, "eth0", "statistics");
            Directory.CreateDirectory(stats);
            File.WriteAllText(Path.Combine(stats, "rx_packets"), "123456\n");
            File.WriteAllText(Path.Combine(stats, "rx_bytes"), "987654321\n");
            var route = Path.Combine(root, "route");
            File.WriteAllText(route,
                "Iface\tDestination\tGateway\tFlags\n" +
                "docker0\t000011AC\t00000000\t0001\n" +
                "eth0\t00000000\t0100A8C0\t0003\n");

            Assert.Equal((123456L, 987654321L), LinkCounters.Read("eth0", root));
            Assert.Null(LinkCounters.Read("eth1", root));
            Assert.Equal("eth0", LinkCounters.DefaultInterface(route));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>The monitor itself, one tick at a time, with the link, tshark and Discord faked.</summary>
public class AttackMonitorTests
{
    private sealed class Players : IPlayerTraffic
    {
        public List<string> Names { get; set; } = [.. Enumerable.Range(1, 28).Select(i => $"Player{i}")];

        public bool RosterFresh { get; set; } = true;

        public IReadOnlyCollection<string> OnlineNames() => [.. Names];

        public IReadOnlySet<uint> OnlineAddresses() => new HashSet<uint> { CapturedPacket.Number("10.1.1.1")!.Value };
    }

    private sealed class Capture(string? pcap) : IAttackCapture
    {
        public bool Stopped { get; private set; }

        public string? PcapPath => pcap;

        public Task<CaptureHealth> StopAsync()
        {
            Stopped = true;
            return Task.FromResult(new CaptureHealth(null, 1000, 0));
        }

        public Task<string?> StatisticsAsync(CancellationToken ct) =>
            Task.FromResult<string?>("Protocol Hierarchy Statistics\neth  frames:1000");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Posts : IAutoPostTarget, IFilePostTarget
    {
        public List<Embed> Sent { get; } = [];
        public List<Embed> Edited { get; } = [];
        public List<(Embed Embed, string File, string Content)> Files { get; } = [];

        public Task<AutoPostEdit> EditAsync(ulong channelId, ulong messageId, Embed embed, MessageComponent? components, CancellationToken ct)
        {
            Edited.Add(embed);
            return Task.FromResult(AutoPostEdit.Edited);
        }

        public Task<ulong?> SendAsync(ulong channelId, Embed embed, MessageComponent? components, CancellationToken ct)
        {
            Sent.Add(embed);
            return Task.FromResult<ulong?>(1);
        }

        public Task<ulong?> SendWithFileAsync(ulong channelId, Embed embed, string fileName, string content, CancellationToken ct)
        {
            Files.Add((embed, fileName, content));
            return Task.FromResult<ulong?>(2);
        }
    }

    private sealed class Rig
    {
        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 18, 13, 50, 0, TimeSpan.Zero));
        public Posts Posts { get; } = new();
        public Players Players { get; } = new();
        public List<Capture> Captures { get; } = [];
        public FloodRecorder? Recorder { get; private set; }
        public Func<IReadOnlySet<uint>>? Addresses { get; private set; }
        public string? CaptureProblem { get; set; }
        public int StartCalls { get; private set; }
        public AttackMonitor Monitor { get; }

        private long _packets;
        private long _bytes;

        public Rig() =>
            Monitor = new AttackMonitor(42, "eth0", new FloodThresholds(Pps: 10_000), pcapDirectory: null,
                _ => (_packets, _bytes),
                Players,
                (_, pcap, recorder, addresses) =>
                {
                    StartCalls++;
                    Recorder = recorder;
                    Addresses = addresses;
                    if (CaptureProblem is not null) return (null, CaptureProblem);
                    var capture = new Capture(pcap);
                    Captures.Add(capture);
                    return (capture, null);
                },
                Posts, Posts, Clock, NullLogger<AttackMonitor>.Instance);

        /// <summary>One second carrying <paramref name="pps"/> packets of 1,200 bytes.</summary>
        public async Task SecondAsync(long pps)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            _packets += pps;
            _bytes += pps * 1200;
            await Monitor.TickAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnAttackPostsAnAlertThenASummaryWithTheReport()
    {
        var rig = new Rig();
        await rig.SecondAsync(500);      // first read: nothing to subtract from
        await rig.SecondAsync(500);

        for (var i = 0; i < 10; i++) await rig.SecondAsync(50_000);
        rig.Recorder!.Add(new CapturedPacket(CapturedPacket.Number("203.0.113.5")!.Value, 7777, PacketProtocol.Udp, 1300), false);
        var everyone = rig.Players.Names;
        rig.Players.Names = [.. everyone.Where(n => n is not "Player3" and not "Player7")];   // two knocked off
        await rig.SecondAsync(50_000);
        rig.Players.Names = [.. everyone.Where(n => n is not "Player7")];                     // one comes back
        await rig.SecondAsync(50_000);

        for (var i = 0; i < 15; i++) await rig.SecondAsync(500);

        var alert = Assert.Single(rig.Posts.Sent);
        Assert.Contains("Attack in progress", alert.Title, StringComparison.Ordinal);
        Assert.NotEmpty(rig.Posts.Edited);   // the live updates

        var (summary, file, content) = Assert.Single(rig.Posts.Files);
        Assert.Equal("Attack ended · summary", summary.Title);
        Assert.StartsWith("attack-", file, StringComparison.Ordinal);
        Assert.Contains("203.0.113.5", content, StringComparison.Ordinal);
        Assert.Contains("online 28 before · 26 lowest · 27 after", content, StringComparison.Ordinal);
        Assert.Contains("2 disconnected, 1 rejoined", content, StringComparison.Ordinal);
        Assert.Contains("2 player(s) dropped", summary.Description, StringComparison.Ordinal);
        var dropped = Assert.Single(summary.Fields, f => f.Name.StartsWith("Players dropped", StringComparison.Ordinal));
        Assert.Contains("Player3", dropped.Value, StringComparison.Ordinal);
        Assert.Contains("Player7", dropped.Value, StringComparison.Ordinal);
        Assert.Contains("not back", dropped.Value, StringComparison.Ordinal);
        Assert.True(rig.Captures.Single().Stopped);
        Assert.Contains("tshark statistics of the pcap", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleRosterUnderAttackIsNotEverybodyDisconnecting()
    {
        /* RCON is often the first thing a flood takes out. Its roster going empty or stale must
           not turn into "28 players dropped". */
        var rig = new Rig();
        await rig.SecondAsync(500);
        await rig.SecondAsync(500);
        for (var i = 0; i < 4; i++) await rig.SecondAsync(50_000);

        rig.Players.RosterFresh = false;
        rig.Players.Names = [];
        for (var i = 0; i < 4; i++) await rig.SecondAsync(50_000);

        rig.Players.RosterFresh = true;
        rig.Players.Names = [.. Enumerable.Range(1, 28).Select(i => $"Player{i}")];
        for (var i = 0; i < 15; i++) await rig.SecondAsync(500);

        var (summary, _, content) = Assert.Single(rig.Posts.Files);
        Assert.DoesNotContain("player(s) dropped", summary.Description, StringComparison.Ordinal);
        Assert.Contains("RCON could not be read for 4s", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCaptureStartsBeforeTheAttackIsDeclared()
    {
        var rig = new Rig();
        await rig.SecondAsync(500);
        await rig.SecondAsync(500);

        await rig.SecondAsync(50_000);   // one second over: a watch, not an attack yet

        Assert.Single(rig.Captures);
        Assert.Empty(rig.Posts.Sent);
    }

    [Fact]
    public async Task AWatchThatNeverBecomesAnAttackIsDroppedAfterAMinute()
    {
        var rig = new Rig();
        await rig.SecondAsync(500);
        await rig.SecondAsync(500);
        await rig.SecondAsync(50_000);

        for (var i = 0; i < 61; i++) await rig.SecondAsync(500);

        Assert.True(rig.Captures.Single().Stopped);
        Assert.Empty(rig.Posts.Sent);
        Assert.Empty(rig.Posts.Files);
    }

    [Fact]
    public async Task WithoutTcpdumpTheSummaryStillComesFromTheCounters()
    {
        var rig = new Rig { CaptureProblem = "tshark is not installed - run `/packettools install`" };
        await rig.SecondAsync(500);
        await rig.SecondAsync(500);
        for (var i = 0; i < 5; i++) await rig.SecondAsync(50_000);
        for (var i = 0; i < 15; i++) await rig.SecondAsync(500);

        var (summary, _, content) = Assert.Single(rig.Posts.Files);
        Assert.Contains(summary.Fields, f => f.Name == "Breakdown" && f.Value.Contains("/packettools install", StringComparison.Ordinal));
        Assert.Contains("257,500 packets", content, StringComparison.Ordinal);   // 5 s of flood + the 15 s quiet tail
    }

    [Fact]
    public async Task ACaptureThatCannotStartIsNotRetriedEverySecond()
    {
        var rig = new Rig { CaptureProblem = "tshark is not installed" };
        await rig.SecondAsync(500);
        await rig.SecondAsync(500);
        for (var i = 0; i < 10; i++) await rig.SecondAsync(50_000);

        // Asked once for the whole attack, not once a second.
        Assert.Equal(1, rig.StartCalls);
        Assert.Empty(rig.Captures);
    }

    [Fact]
    public async Task KnownPlayersAreHandedToTheCapture()
    {
        var rig = new Rig();
        await rig.SecondAsync(500);
        await rig.SecondAsync(500);
        await rig.SecondAsync(50_000);

        Assert.Contains(CapturedPacket.Number("10.1.1.1")!.Value, rig.Addresses!());
    }
}
