using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Network;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Network;
using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>The per-minute source log, as it is written and read back.</summary>
public class SourceLogTests
{
    private static readonly DateTimeOffset Minute = new(2026, 10, 29, 13, 52, 0, TimeSpan.Zero);

    private static CapturedPacket Udp(string source, ushort port = 7777, int payload = 100) =>
        new(CapturedPacket.Number(source)!.Value, port, PacketProtocol.Udp, payload);

    [Fact]
    public void AMinuteIsATotalsLineThenTheBusiestSources()
    {
        var log = new SourceLog();
        for (var i = 0; i < 5; i++) log.Add(Udp("203.0.113.5", payload: 1200), fromPlayer: false);
        log.Add(Udp("10.1.1.1", port: 7778, payload: 40), fromPlayer: true);
        log.Add(new CapturedPacket(CapturedPacket.Number("198.51.100.9")!.Value, 22, PacketProtocol.Tcp, 0, Syn: true), false);
        log.Add(new CapturedPacket(CapturedPacket.Number("198.51.100.9")!.Value, 22, PacketProtocol.Tcp, 0, Syn: true), false);

        var lines = log.Flush(Minute);

        Assert.Equal(
        [
            "2026-10-29T13:52Z,*,8,6040,,3 sources,",
            "2026-10-29T13:52Z,203.0.113.5,5,6000,7777,udp,0",
            "2026-10-29T13:52Z,198.51.100.9,2,0,22,tcp,0",
            "2026-10-29T13:52Z,10.1.1.1,1,40,7778,udp,1",
        ], lines);
    }

    [Fact]
    public void FlushingStartsTheNextMinuteEmpty()
    {
        var log = new SourceLog();
        log.Add(Udp("203.0.113.5"), false);
        log.Flush(Minute);

        Assert.Empty(log.Flush(Minute.AddMinutes(1)));
    }

    [Fact]
    public void ASpoofedFloodIsAHundredLinesNotAHundredThousand()
    {
        var log = new SourceLog();
        for (uint i = 0; i < SourceLog.MaxSources + 50; i++) log.Add(new CapturedPacket(0x0A000000 + i, 7777, PacketProtocol.Udp, 10), false);

        var lines = log.Flush(Minute);

        Assert.Equal(SourceLog.TopPerMinute + 1, lines.Count);
        // Every packet is in the totals, and the "+" says there were more sources than were tracked.
        Assert.Equal($"2026-10-29T13:52Z,*,{SourceLog.MaxSources + 50},{(SourceLog.MaxSources + 50) * 10},,{SourceLog.MaxSources}+ sources,", lines[0]);
    }

    [Fact]
    public void ADaysFileIsNamedForItsUtcDate()
    {
        Assert.Equal("sources-2026-10-29.csv", SourceLog.FileName(Minute));
        Assert.Equal("sources-2026-10-30.csv", SourceLog.FileName(new DateTimeOffset(2026, 10, 29, 23, 30, 0, TimeSpan.FromHours(-5))));
    }

    [Theory]
    [InlineData("2026-10-29T13:52Z,203.0.113.5,5,6000,7777,udp,0", "203.0.113.5", true)]
    [InlineData("2026-10-29T13:52Z,203.0.113.55,5,6000,7777,udp,0", "203.0.113.5", false)]   // not a prefix match
    [InlineData("2026-10-29T13:52Z,1.203.0.113.5,5,6000,7777,udp,0", "203.0.113.5", false)]
    [InlineData("2026-10-29T13:52Z,*,8,6040,,3 sources,", "203.0.113.5", false)]
    [InlineData("minute_utc,source,packets,payload_bytes,top_port,protocol,player", "203.0.113.5", false)]
    public void ALineIsForAnAddressOnlyWhenItIsTheSource(string line, string address, bool expected) =>
        Assert.Equal(expected, SourceLog.IsFor(line, address));

    [Fact]
    public void TotalsAddUpTheMinutes()
    {
        var totals = SourceLog.Summarise(
        [
            "2026-10-28T09:00Z,203.0.113.5,10,1000,7777,udp,0",
            "2026-10-29T13:52Z,203.0.113.5,5,600,7777,udp,1",
            "garbage",
        ]);

        Assert.Equal(new SourceLog.Totals(2, 15, 1600, "2026-10-28T09:00Z", "2026-10-29T13:52Z", Player: true), totals);
    }
}

/// <summary>The always-on capture's files: the ring, the daily logs, and the pcap an attack keeps.</summary>
public sealed class PacketLogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 13, 52, 30, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "packetlog-" + Guid.NewGuid().ToString("N"));
    private readonly string _packets;
    private readonly string _attacks;
    private readonly Clock _clock = new(Now);
    private readonly List<IReadOnlyList<string>> _merges = [];

    public PacketLogTests()
    {
        _packets = Path.Combine(_root, "packets");
        _attacks = Path.Combine(_root, "attacks");
        Directory.CreateDirectory(_packets);
        Directory.CreateDirectory(_attacks);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoPlayers : IPlayerTraffic
    {
        public bool RosterFresh => true;

        public IReadOnlyCollection<string> OnlineNames() => [];

        public IReadOnlySet<uint> OnlineAddresses() => new HashSet<uint>();

        public IReadOnlySet<uint> KnownAddresses() => new HashSet<uint>();
    }

    /// <summary>A mergecap that joins the files' bytes, and says what the real one says about a file still being written.</summary>
    private Task<ProcessOutcome> FakeMergecap(IReadOnlyList<string> argv, CancellationToken ct)
    {
        _merges.Add(argv);
        using (var target = File.Create(argv[1]))
        {
            foreach (var file in argv.Skip(2)) target.Write(File.ReadAllBytes(file));
        }
        return Task.FromResult(new ProcessOutcome(true, false, 2, "",
            "mergecap: The file \"ring_00003.pcap\" appears to have been cut short in the middle of a packet."));
    }

    private PacketLog Log(int ringMegabytes = 100, int keepDays = 30,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessOutcome>>? mergecap = null) =>
        new("lo", _packets, ringMegabytes, keepDays, new NoPlayers(), _clock, NullLogger<PacketLog>.Instance, mergecap ?? FakeMergecap);

    private string Ring(int sequence, string started, long length, DateTimeOffset written, string? directory = null)
    {
        var path = Path.Combine(directory ?? _packets, $"ring_{sequence:D5}_{started}.pcap");
        using (var file = File.Create(path)) file.SetLength(length);   // sparse: no real disk
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return path;
    }

    private const long Mb = 1024 * 1024;

    // ---- the ring ----

    [Fact]
    public void RingFilesAreInWritingOrderAcrossRestarts()
    {
        Ring(2, "20261029120000", 1, Now);
        Ring(10, "20261029120000", 1, Now);   // sequence 10 after 2, not before it as text
        Ring(1, "20261029130000", 1, Now);    // a later run starts again at 1
        Ring(1, "20261028230000", 1, Now);
        File.WriteAllText(Path.Combine(_packets, "ring_notours.pcap"), "");

        Assert.Equal(
            ["ring_00001_20261028230000.pcap", "ring_00002_20261029120000.pcap", "ring_00010_20261029120000.pcap", "ring_00001_20261029130000.pcap"],
            PacketLog.RingFiles(_packets).Select(f => f.Name));
    }

    [Fact]
    public void TheRingKeepsItsNewestFilesWithinItsBudget()
    {
        var oldest = Ring(1, "20261029120000", 50 * Mb, Now.AddMinutes(-30));
        var older = Ring(2, "20261029120000", 50 * Mb, Now.AddMinutes(-20));
        var newer = Ring(3, "20261029120000", 50 * Mb, Now.AddMinutes(-10));
        var writing = Ring(4, "20261029120000", 10 * Mb, Now);

        Log(ringMegabytes: 100).Housekeep(Now);

        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(writing));
    }

    [Fact]
    public void TheFileBeingWrittenIsNeverDeletedHoweverBig()
    {
        var writing = Ring(1, "20261029120000", 500 * Mb, Now);

        Log(ringMegabytes: 100).Housekeep(Now);

        Assert.True(File.Exists(writing));
    }

    [Fact]
    public void DailyLogsAreKeptForTheirDays()
    {
        foreach (var day in new[] { "2026-09-28", "2026-09-29", "2026-10-29" })
            File.WriteAllText(Path.Combine(_packets, $"sources-{day}.csv"), SourceLog.Header + "\n");

        Log(keepDays: 30).Housekeep(Now);

        Assert.Equal(["sources-2026-09-29.csv", "sources-2026-10-29.csv"],
            Directory.EnumerateFiles(_packets, "sources-*.csv").Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // ---- the source log on disk ----

    [Fact]
    public void AMinuteIsAppendedToItsDayUnderOneHeader()
    {
        var log = Log();
        log.Record(new CapturedPacket(CapturedPacket.Number("203.0.113.5")!.Value, 7777, PacketProtocol.Udp, 100), false);
        log.Flush(Now.AddMinutes(1));
        log.Record(new CapturedPacket(CapturedPacket.Number("203.0.113.5")!.Value, 7777, PacketProtocol.Udp, 100), false);
        log.Flush(Now.AddMinutes(2));
        log.Flush(Now.AddMinutes(3));   // a quiet minute writes nothing

        var path = Path.Combine(_packets, "sources-2026-10-29.csv");
        Assert.Equal(
            SourceLog.Header + "\n" +
            "2026-10-29T13:52Z,*,1,100,,1 sources,\n2026-10-29T13:52Z,203.0.113.5,1,100,7777,udp,0\n" +
            "2026-10-29T13:53Z,*,1,100,,1 sources,\n2026-10-29T13:53Z,203.0.113.5,1,100,7777,udp,0\n",
            File.ReadAllText(path));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    // ---- an attack's pcap ----

    [Fact]
    public async Task AnAttackKeepsTheRingFilesWrittenDuringItAndJoinsThemWhenItEnds()
    {
        var before = Ring(1, "20261029120000", 10, Now.AddMinutes(-5));
        var log = Log(ringMegabytes: 100);
        var target = Path.Combine(_attacks, "attack-20261029-135230.pcap");
        var capture = log.Attach(new FloodRecorder(), () => new HashSet<uint>(), target);

        // During the attack tshark closes two files; the ring's pruning must not be able to touch them.
        var during1 = Ring(2, "20261029120000", 20, Now.AddSeconds(30));
        var during2 = Ring(3, "20261029120000", 30, Now.AddSeconds(60));
        Ring(4, "20261029120000", 40, Now.AddSeconds(61));   // being written
        log.Housekeep(Now.AddSeconds(61));

        Assert.True(File.Exists(before));
        Assert.False(File.Exists(during1));
        Assert.True(File.Exists(Path.Combine(target + ".parts", Path.GetFileName(during1))));

        await capture.StopAsync();

        // The kept files and the one still being written, in order; and the parts are gone.
        var merge = Assert.Single(_merges);
        Assert.Equal(["-w", target], merge.Take(2));
        Assert.Equal(["ring_00002_20261029120000.pcap", "ring_00003_20261029120000.pcap", "ring_00004_20261029120000.pcap"],
            merge.Skip(2).Select(Path.GetFileName));
        Assert.Equal(target, capture.PcapPath);
        Assert.Equal(90, new FileInfo(target).Length);
        Assert.False(Directory.Exists(target + ".parts"));
        Assert.False(File.Exists(during2));
    }

    [Fact]
    public async Task ABlipThrownAwayGivesItsFilesBackToTheRing()
    {
        var log = Log();
        var target = Path.Combine(_attacks, "attack-20261029-135230.pcap");
        var capture = log.Attach(new FloodRecorder(), () => new HashSet<uint>(), target);
        var during = Ring(2, "20261029120000", 20, Now.AddSeconds(5));
        Ring(3, "20261029120000", 20, Now.AddSeconds(6));
        log.Housekeep(Now.AddSeconds(6));
        Assert.False(File.Exists(during));

        await capture.DisposeAsync();

        Assert.True(File.Exists(during));
        Assert.False(Directory.Exists(target + ".parts"));
        Assert.Empty(_merges);
    }

    [Fact]
    public async Task AFailedJoinKeepsThePartsForTheNextStart()
    {
        var failing = Log(mergecap: (argv, _) =>
        {
            File.WriteAllText(argv[1], "half");
            return Task.FromResult(new ProcessOutcome(true, false, 1, "", "mergecap: disk full"));
        });
        var target = Path.Combine(_attacks, "attack-20261029-135230.pcap");
        var capture = failing.Attach(new FloodRecorder(), () => new HashSet<uint>(), target);
        Ring(2, "20261029120000", 20, Now.AddSeconds(5));
        Ring(3, "20261029120000", 20, Now.AddSeconds(6));

        await capture.StopAsync();

        Assert.Null(capture.PcapPath);
        Assert.False(File.Exists(target));   // half a pcap is not left to pass for a whole one
        Assert.Single(PacketLog.RingFiles(target + ".parts"));

        await Log().RecoverAsync(CancellationToken.None);

        Assert.True(File.Exists(target));
        Assert.False(Directory.Exists(target + ".parts"));
    }

    [Theory]
    [InlineData(0, "", true)]
    [InlineData(2, "appears to have been cut short in the middle of a packet", true)]
    [InlineData(2, "The file isn't a capture file in a format mergecap understands", false)]
    [InlineData(1, "cut short", false)]
    public void MergecapCuttingTheLiveFileShortIsASuccess(int exit, string stderr, bool ok) =>
        Assert.Equal(ok, PacketLog.MergeSucceeded(new ProcessOutcome(true, false, exit, "", stderr)));

    [Fact]
    public void TheRingWritesHeadersOnlyInFiftyMegabyteFiles()
    {
        var argv = PacketLog.RingArgv("ens3", "/root/pavlov-bot/data/packets");

        Assert.Equal(
            ["-i", "ens3", "-n", "-s", "128", "-B", "64", "-f", "inbound and ip", "-b", "filesize:51200", "-w", "/root/pavlov-bot/data/packets/ring.pcap"],
            argv);
    }
}

/// <summary>Turning the packet log on and off, and looking an address up from Discord.</summary>
public class PacketLogSettingsTests
{
    private static FeatureOptions Bind(params (string Key, string Value)[] settings) =>
        FeatureOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [Fact]
    public void ItIsOnWithTheDdosMonitorAndOffWithout()
    {
        Assert.True(Bind(("ATTACK_CHANNEL", "1234567890123456789")).PacketLog);
        Assert.False(Bind().PacketLog);
        Assert.False(Bind(("ATTACK_CHANNEL", "1234567890123456789"), ("PACKET_LOG", "off")).PacketLog);
        Assert.True(Bind(("PACKET_LOG", "on")).PacketLog);
    }

    [Fact]
    public void ItsBudgetsDefaultToThirtyDaysAndTwoGigabytes()
    {
        var features = Bind();
        Assert.Equal(30, features.PacketLogDays);
        Assert.Equal(2048, features.PacketRingMegabytes);

        features = Bind(("PACKET_LOG_DAYS", "7"), ("PACKET_RING_MB", "500"));
        Assert.Equal(7, features.PacketLogDays);
        Assert.Equal(500, features.PacketRingMegabytes);
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData(" 203.0.113.5 ", "203.0.113.5")]
    [InlineData("203.000.113.005", null)]   // the log never writes it that way
    [InlineData("2001:db8::1", null)]
    [InlineData("203.0.113", null)]
    [InlineData("example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyACanonicalIpv4AddressIsSearched(string? input, string? expected) =>
        Assert.Equal(expected, PacketToolsCommand.CanonicalIpv4(input));

    [Fact]
    public void TheSearchReadsTheDaysThatExistOldestFirst()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var day in new[] { "2026-10-20", "2026-10-27", "2026-10-29" })
                File.WriteAllText(Path.Combine(directory, $"sources-{day}.csv"), "");

            var logs = PacketToolsCommand.SourceLogs(directory, new DateTimeOffset(2026, 10, 29, 13, 0, 0, TimeSpan.Zero), days: 7);

            Assert.Equal(["sources-2026-10-27.csv", "sources-2026-10-29.csv"], logs.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
