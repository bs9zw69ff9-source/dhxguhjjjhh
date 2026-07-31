using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Observability;
using Xunit;

namespace PavlovBot.Tests;

public class MoneyLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-money-" + Guid.NewGuid().ToString("N"));
    private readonly MoneyLog _log;

    public MoneyLogTests()
    {
        Directory.CreateDirectory(_directory);
        // No webhook URL registered, so posting is a no-op and the tick's return value is
        // what gets asserted - the feed itself is tested separately.
        var feeds = new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, new MetricsRegistry());
        _log = new MoneyLog(_directory, feeds, NullLogger<MoneyLog>.Instance);
    }

    private void Write(string player, string contents)
    {
        var path = Path.Combine(_directory, $"{player}.txt");
        File.WriteAllText(path, contents);
        // Nudge the mtime so the poller cannot skip it. Real writes are seconds apart;
        // a test writing twice in one tick would otherwise land on the same timestamp.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
    }

    [Fact]
    public async Task TheFirstTickIsABaselineAndReportsNothing()
    {
        /* Reporting on it would announce every player's entire balance as a credit the
           moment the bot starts. */
        Write("Alice", "5000");
        Assert.Empty(await _log.TickAsync());
        Assert.Equal(5000, _log.Cached("Alice"));
    }

    [Fact]
    public async Task AChangeIsReportedAsADelta()
    {
        Write("Alice", "5000");
        await _log.TickAsync();

        Write("Alice", "5400");
        var changes = await _log.TickAsync();

        Assert.Equal([("Alice", 400L)], changes);
    }

    [Fact]
    public async Task ADebitIsANegativeDelta()
    {
        Write("Alice", "5000");
        await _log.TickAsync();

        Write("Alice", "4655");
        Assert.Equal([("Alice", -345L)], await _log.TickAsync());
    }

    [Fact]
    public async Task APlayerFirstSeenAfterPrimingIsABaseline_NotAHugeCredit()
    {
        Write("Alice", "100");
        await _log.TickAsync();

        Write("Bob", "9999");
        Assert.Empty(await _log.TickAsync());
        Assert.Equal(9999, _log.Cached("Bob"));
    }

    [Fact]
    public async Task AnUnparseableLedgerCarriesTheCachedEntryForward_NeverFabricatingACredit()
    {
        /* THE bug this shape exists to prevent. Dropping the cached entry makes the player
           look new on the next successful read, so their whole balance is reported as a
           credit: "+5,500" for a player who was paid 500. A fabricated transaction in a
           money log is worse than a missed one, because somebody will act on it. */
        Write("Alice", "5000");
        await _log.TickAsync();

        Write("Alice", "<partial write>");
        Assert.Empty(await _log.TickAsync());
        Assert.Equal(5000, _log.Cached("Alice"));   // the old value survives

        Write("Alice", "5500");
        Assert.Equal([("Alice", 500L)], await _log.TickAsync());
    }

    [Fact]
    public async Task ATouchedButUnchangedLedgerReportsNothing()
    {
        Write("Alice", "5000");
        await _log.TickAsync();

        Write("Alice", "5000");
        Assert.Empty(await _log.TickAsync());
    }

    [Fact]
    public async Task SeveralPlayersChangingInOneTickAreAllReported()
    {
        Write("Alice", "1000");
        Write("Bob", "2000");
        await _log.TickAsync();

        Write("Alice", "1400");
        Write("Bob", "1655");

        var changes = (await _log.TickAsync()).OrderBy(c => c.Player).ToList();
        Assert.Equal([("Alice", 400L), ("Bob", -345L)], changes);
    }

    [Fact]
    public async Task AnUnconfiguredDirectoryIsANoOp()
    {
        var feeds = new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, new MetricsRegistry());
        var log = new MoneyLog(null, feeds, NullLogger<MoneyLog>.Instance);

        Assert.False(log.Enabled);
        Assert.Empty(await log.TickAsync());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}

public class LedgerFileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-ledger-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ARoundTripPreservesTheBalance()
    {
        var store = new LedgerFileStore(_directory);
        Assert.True(store.Write("Alice", 5000));
        Assert.Equal(5000, store.Read("Alice"));
    }

    [Fact]
    public void SpacesInNamesAreKept()
    {
        /* Filenames must match what the GAME writes. Stripping spaces would send wages to
           a phantom "ButterLife.txt" that nothing reads. */
        var store = new LedgerFileStore(_directory);
        store.Write("Butter Life", 700);

        Assert.True(File.Exists(Path.Combine(_directory, "Butter Life.txt")));
        Assert.Equal(700, store.Read("Butter Life"));
    }

    [Fact]
    public void PathSeparatorsAreStrippedSoALedgerCannotEscapeItsDirectory()
    {
        var store = new LedgerFileStore(_directory);
        store.Write("../escape", 1);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_directory)!, "escape.txt")));
    }

    [Fact]
    public void AnAbsentLedgerReadsAsNull_NotZero()
    {
        // Null means UNKNOWN; the ledger treats it as zero at the point of use. Returning 0
        // here would let a transient read error wipe somebody's balance.
        Assert.Null(new LedgerFileStore(_directory).Read("NeverSeen"));
    }

    [Fact]
    public void AnUnconfiguredStoreRefusesToWrite()
    {
        Assert.False(new LedgerFileStore(null).Write("Alice", 1));
        Assert.Null(new LedgerFileStore(null).Read("Alice"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
