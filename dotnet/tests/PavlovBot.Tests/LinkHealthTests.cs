using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Network;
using PavlovBot.Host.Network;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>The kernel's loss counters, read from the formats /proc really has.</summary>
public class LinkHealthTests
{
    // Two CPUs of /proc/net/softnet_stat on a 6.8 kernel: processed, dropped, time_squeeze, ...
    private const string Softnet =
        "0004a1b2 0000000a 00000003 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000\n" +
        "00012f00 000001f4 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000001\n";

    // /proc/net/stat/nf_conntrack on a 6.8 kernel: a header, then one line per CPU.
    private const string Conntrack =
        "entries  clashres found new invalid ignore delete delete_list insert insert_failed drop early_drop icmp_error  expect_new expect_create expect_delete search_restart\n" +
        "0000002a  00000000 00000000 00000000 00000005 00000000 00000000 00000000 00000000 00000002 00000010 00000001 00000000  00000000 00000000 00000000 00000000\n" +
        "0000002a  00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000020 00000000 00000000  00000000 00000000 00000000 00000000\n";

    [Fact]
    public void SoftnetDropsAreTheSecondHexColumnSummedAcrossCpus() =>
        Assert.Equal(0x0a + 0x1f4, LinkHealth.SoftnetDrops(Softnet));

    [Fact]
    public void ConntrackDropsAreReadByColumnNameNotPosition() =>
        // insert_failed 2 + drop 0x10 + early_drop 1 on CPU 0, drop 0x20 on CPU 1. "invalid" (5) is not a drop.
        Assert.Equal(2 + 0x10 + 1 + 0x20, LinkHealth.ConntrackDropCount(Conntrack));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("entries drop\n")]
    public void MissingOrHeaderOnlyTextIsNoDrops(string? text)
    {
        Assert.Equal(0, LinkHealth.SoftnetDrops(text));
        Assert.Equal(0, LinkHealth.ConntrackDropCount(text));
    }

    [Fact]
    public void SinceTurnsCountersIntoDifferencesAndKeepsGauges()
    {
        var before = new LinkHealth(10, 1, 0, 0, 100, 5, 900, 1000, "up");
        var now = new LinkHealth(15, 1, 2, 0, 160, 5, 950, 1000, "down");

        var d = now.Since(before);

        Assert.Equal((5L, 0L, 2L, 60L, 0L), (d.NicDropped, d.NicMissed, d.NicFifo, d.SoftnetDropped, d.ConntrackDrops));
        Assert.Equal(67, d.Lost);
        Assert.Equal((950L, 1000L, "down", false), (d.ConntrackCount, d.ConntrackMax, d.OperState, d.Up));
        Assert.Equal(0.95, d.ConntrackFill, 3);
    }

    [Fact]
    public void ACounterThatWentDownWasResetAndCountsFromZero()
    {
        var d = new LinkHealth(3, 0, 0, 0, 0, 0, 0, 0, "unknown").Since(new LinkHealth(1_000, 0, 0, 0, 0, 0, 0, 0, "up"));

        Assert.Equal(3, d.NicDropped);
        Assert.True(d.Up);   // "unknown" is what virtual NICs report while passing traffic
        Assert.Equal(0, d.ConntrackFill);
    }
}

/// <summary>The saved reading that lets downtime be recorded.</summary>
public sealed class ReadingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reading-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ReadingStore Store() => new(Path.Combine(_root, "state", "last-reading.json"), NullLogger<ReadingStore>.Instance);

    [Fact]
    public void AReadingComesBackAsItWasSaved()
    {
        var accounting = new Accounting(
            new Dictionary<FlowKey, Counter> { [new("203.0.113.5", "udp", 7777)] = new(4_000_000_000, 9) },
            new Dictionary<FlowKey, Counter> { [new("2001:db8::1", "tcp", 22)] = new(7, 420) },
            new Dictionary<FlowKey, Counter> { [new("198.51.100.9", "ipv6-icmp", 0)] = new(1, 2) },
            new Dictionary<string, Counter> { ["203.0.113.5"] = new(50, 5000) });
        var saved = new Reading(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), "ens3", (123, 456),
            new LinkHealth(1, 2, 3, 4, 5, 6, 7, 8, "up"), accounting);
        var store = Store();

        store.Save(saved);
        var loaded = store.Load()!;

        Assert.Equal((saved.Time, saved.Interface, saved.Link, saved.Health), (loaded.Time, loaded.Interface, loaded.Link, loaded.Health));
        Assert.Equal(accounting.Flows, loaded.Accounting.Flows);
        Assert.Equal(accounting.Syn, loaded.Accounting.Syn);
        Assert.Equal(accounting.Other, loaded.Accounting.Other);
        Assert.Equal(accounting.Dropped, loaded.Accounting.Dropped);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
    }

    [Fact]
    public void WithoutLinkOrHealthTheReadingStillLoads()
    {
        var store = Store();
        store.Save(new Reading(DateTimeOffset.UnixEpoch, "ens3", null, null, Accounting.Empty));

        var loaded = store.Load()!;

        Assert.Null(loaded.Link);
        Assert.Null(loaded.Health);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"v\":1}")]
    [InlineData("{\"v\":1,\"ts\":\"yesterday\",\"iface\":\"ens3\"}")]
    [InlineData("{\"v\":1,\"ts\":\"2026-10-31T12:00:00Z\",\"iface\":\"ens3\",\"flows\":[[\"x\"]]}")]
    public void ADamagedFileIsNoReadingRatherThanACrash(string text)
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, text);

        Assert.Null(store.Load());
    }

    [Fact]
    public void NoFileIsNoReading() => Assert.Null(Store().Load());
}
