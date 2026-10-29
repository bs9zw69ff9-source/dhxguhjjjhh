using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Network;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Network;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>Reading tcpdump, deciding what is an attack, and the numbers in the report.</summary>
/// <remarks>The sample lines and statistics are copied from tcpdump 4.99.4 / libpcap 1.10.4.</remarks>
public class FloodAnalysisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 13, 50, 34, TimeSpan.Zero);

    // ---- tcpdump lines ----

    [Fact]
    public void AUdpLine()
    {
        var p = CapturedPacket.Parse("IP 203.0.113.5.51234 > 10.0.0.2.7777: UDP, length 1200")!.Value;

        Assert.Equal("203.0.113.5", CapturedPacket.Address(p.Source));
        Assert.Equal(7777, p.DestinationPort);
        Assert.Equal(PacketProtocol.Udp, p.Protocol);
        Assert.Equal(1200, p.Payload);
        Assert.False(p.Syn);
    }

    [Theory]
    [InlineData("IP 127.0.0.1.56920 > 127.0.0.1.9999: Flags [S], seq 1442071647, win 65495, options [mss 65495,sackOK,TS val 588826661 ecr 0,nop,wscale 10], length 0", true)]
    [InlineData("IP 127.0.0.1.9999 > 127.0.0.1.56920: Flags [R.], seq 0, ack 1442071648, win 0, length 0", false)]
    [InlineData("IP 10.0.0.9.443 > 10.0.0.2.51000: Flags [S.], seq 1, ack 2, win 64240, length 0", false)]
    [InlineData("IP 10.0.0.9.5000 > 10.0.0.2.80: Flags [P.], seq 1:101, ack 1, win 501, length 100", false)]
    public void TcpOnlyABareSynIsASyn(string line, bool syn)
    {
        var p = CapturedPacket.Parse(line)!.Value;

        Assert.Equal(PacketProtocol.Tcp, p.Protocol);
        Assert.Equal(syn, p.Syn);
    }

    [Fact]
    public void AnIcmpLineHasNoPortAndTakesTheLastLength()
    {
        var p = CapturedPacket.Parse("IP 127.0.0.1 > 127.0.0.1: ICMP 127.0.0.1 udp port 7779 unreachable, length 556")!.Value;

        Assert.Equal(PacketProtocol.Icmp, p.Protocol);
        Assert.Equal(0, p.DestinationPort);
        Assert.Equal(556, p.Payload);
    }

    [Fact]
    public void AFragmentIsCountedAsOther()
    {
        var p = CapturedPacket.Parse("IP 198.51.100.7 > 10.0.0.2: ip-proto-17")!.Value;

        Assert.Equal(PacketProtocol.Other, p.Protocol);
        Assert.Equal("198.51.100.7", CapturedPacket.Address(p.Source));
    }

    [Theory]
    [InlineData("listening on lo, link-type EN10MB (Ethernet), snapshot length 128 bytes")]
    [InlineData("IP6 ::1.5000 > ::1.7777: UDP, length 10")]
    [InlineData("IP 999.1.1.1.5 > 10.0.0.2.7777: UDP, length 10")]
    [InlineData("IP 10.0.0.1.5 > 10.0.0.2.port: UDP, length 10")]
    [InlineData("IP no arrow here: UDP")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotAPacket(string? line) => Assert.Null(CapturedPacket.Parse(line));

    [Fact]
    public void TcpdumpsClosingStatisticsAreRead()
    {
        var health = TcpdumpCapture.Health(
            "tcpdump: verbose output suppressed, use -v[v]... for full protocol decode\n" +
            "listening on eth0, link-type EN10MB (Ethernet), snapshot length 128 bytes\n" +
            "2465096 packets captured\n2470000 packets received by filter\n4904 packets dropped by kernel\n");

        Assert.Equal(2_465_096, health.Captured);
        Assert.Equal(4_904, health.DroppedByKernel);
        Assert.InRange(health.Completeness, 0.997, 0.999);
    }

    [Fact]
    public void TheStreamCopyKeepsHeadersOnlyAndDecodesNothingExtra()
    {
        var argv = TcpdumpCapture.StreamArgv("eth0");

        Assert.Equal(["-i", "eth0", "-l", "-n", "-t", "-s", "128", "-B", "16384", "inbound", "and", "ip"], argv);
        Assert.DoesNotContain(argv, a => a.StartsWith("-v", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePcapCopyIsCappedAndWritesWhereItIsTold()
    {
        var argv = TcpdumpCapture.PcapArgv("eth0", "/root/pavlov-bot/data/attacks/attack-1.pcap");

        Assert.Contains("-c", argv);
        Assert.Equal(TcpdumpCapture.PcapPacketLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), argv[argv.ToList().IndexOf("-c") + 1]);
        Assert.Equal("/root/pavlov-bot/data/attacks/attack-1.pcap", argv[argv.ToList().IndexOf("-w") + 1]);
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
        Assert.Contains("open it in Wireshark", text, StringComparison.Ordinal);
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

/// <summary>The monitor itself, one tick at a time, with the link, tcpdump and Discord faked.</summary>
public class AttackMonitorTests
{
    private sealed class Players : IPlayerTraffic
    {
        public int OnlineCount { get; set; } = 28;

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
        rig.Players.OnlineCount = 26;
        await rig.SecondAsync(50_000);
        rig.Players.OnlineCount = 28;

        for (var i = 0; i < 15; i++) await rig.SecondAsync(500);

        var alert = Assert.Single(rig.Posts.Sent);
        Assert.Contains("Attack in progress", alert.Title, StringComparison.Ordinal);
        Assert.NotEmpty(rig.Posts.Edited);   // the live updates

        var (summary, file, content) = Assert.Single(rig.Posts.Files);
        Assert.Equal("Attack ended · summary", summary.Title);
        Assert.StartsWith("attack-", file, StringComparison.Ordinal);
        Assert.Contains("203.0.113.5", content, StringComparison.Ordinal);
        Assert.Contains("online 28 before · 26 lowest · 28 after", content, StringComparison.Ordinal);
        Assert.True(rig.Captures.Single().Stopped);
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
        var rig = new Rig { CaptureProblem = "tcpdump is not installed - `apt install tcpdump`" };
        await rig.SecondAsync(500);
        await rig.SecondAsync(500);
        for (var i = 0; i < 5; i++) await rig.SecondAsync(50_000);
        for (var i = 0; i < 15; i++) await rig.SecondAsync(500);

        var (summary, _, content) = Assert.Single(rig.Posts.Files);
        Assert.Contains(summary.Fields, f => f.Name == "Breakdown" && f.Value.Contains("apt install tcpdump", StringComparison.Ordinal));
        Assert.Contains("257,500 packets", content, StringComparison.Ordinal);   // 5 s of flood + the 15 s quiet tail
    }

    [Fact]
    public async Task ACaptureThatCannotStartIsNotRetriedEverySecond()
    {
        var rig = new Rig { CaptureProblem = "tcpdump is not installed" };
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
