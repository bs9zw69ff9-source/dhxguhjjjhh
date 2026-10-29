using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Network;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Network;
using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>The always-on capture's files: the ring, and the pcap an attack keeps.</summary>
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
        new("eth0", _packets, _attacks, ringMegabytes, keepDays, _clock, NullLogger<PacketLog>.Instance, mergecap ?? FakeMergecap);

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

        Log(ringMegabytes: 100).Housekeep();

        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(writing));
    }

    [Fact]
    public void TheFileBeingWrittenIsNeverDeletedHoweverBig()
    {
        var writing = Ring(1, "20261029120000", 500 * Mb, Now);

        Log(ringMegabytes: 100).Housekeep();

        Assert.True(File.Exists(writing));
    }

    // ---- an attack's pcap ----

    [Fact]
    public async Task AnAttackKeepsTheRingFilesWrittenDuringItAndJoinsThemWhenItEnds()
    {
        var before = Ring(1, "20261029120000", 10, Now.AddMinutes(-5));
        var log = Log(ringMegabytes: 100);
        var target = Path.Combine(_attacks, "attack-20261029-135230.pcap");
        var capture = log.Attach(target);

        // During the attack tshark closes two files; the ring's pruning must not be able to touch them.
        var during1 = Ring(2, "20261029120000", 20, Now.AddSeconds(30));
        var during2 = Ring(3, "20261029120000", 30, Now.AddSeconds(60));
        Ring(4, "20261029120000", 40, Now.AddSeconds(61));   // being written
        log.Housekeep();

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
        var capture = log.Attach(target);
        var during = Ring(2, "20261029120000", 20, Now.AddSeconds(5));
        Ring(3, "20261029120000", 20, Now.AddSeconds(6));
        log.Housekeep();
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
        var capture = failing.Attach(target);
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

/// <summary>Turning the capture on and off.</summary>
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
    public void TheRingDefaultsToTwoGigabytes()
    {
        Assert.Equal(2048, Bind().PacketRingMegabytes);
        Assert.Equal(500, Bind(("PACKET_RING_MB", "500")).PacketRingMegabytes);
    }
}
