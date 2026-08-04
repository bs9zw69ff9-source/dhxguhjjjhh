using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Flagging a player who earns too much, too fast.
/// </summary>
/// <remarks>
/// The exploit worth catching is rarely one huge credit - it is a hundred small ones inside
/// ten minutes, each unremarkable on its own. Hence a sliding window rather than a per-tick
/// threshold, and hence most of these tests being about the window rather than the number.
/// </remarks>
public class MoneyAnomalyTests
{
    private readonly TestClock _time = new();
    private readonly SerializedStore _store = new(new MemoryBackend(), new SystemTextJsonCodec());

    private MoneyAnomalyDetector Build(long threshold = 100_000, TimeSpan? window = null) =>
        new(_store, NullLogger<MoneyAnomalyDetector>.Instance,
            threshold, window ?? TimeSpan.FromMinutes(15), _time);

    private static (string, long)[] Change(string player, long delta) => [(player, delta)];

    // ---- the threshold ----

    [Fact]
    public async Task OneHugeCreditIsFlagged()
    {
        var alerts = await Build().ObserveAsync(Change("Alice", 250_000));

        var alert = Assert.Single(alerts);
        Assert.Equal("Alice", alert.Player);
        Assert.Equal(250_000, alert.Total);
        Assert.Equal(1, alert.Events);
    }

    [Fact]
    public async Task ManySmallCreditsAddingUpAreFlagged()
    {
        // THE CASE A PER-TICK THRESHOLD MISSES. Ten thousand at a time is unremarkable;
        // twelve of them inside a quarter of an hour is not.
        var detector = Build();
        var fired = new List<MoneyAlert>();

        // Collected across the loop, not just kept from the last pass: the alert lands on the
        // TENTH credit - the one that crosses - and the two after it are inside the cooldown.
        for (var i = 0; i < 12; i++)
        {
            fired.AddRange(await detector.ObserveAsync(Change("Alice", 10_000)));
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        var alert = Assert.Single(fired);
        Assert.Equal(100_000, alert.Total);
        Assert.Equal(10, alert.Events);
    }

    [Fact]
    public async Task EarningsBelowTheThresholdAreNotFlagged()
    {
        Assert.Empty(await Build().ObserveAsync(Change("Alice", 99_999)));
    }

    // ---- what does not count ----

    [Fact]
    public async Task SpendingIsIgnored()
    {
        /* Netting credits against debits would let somebody hide a million in income behind
           a million in outgoings - which is exactly what laundering looks like. */
        var detector = Build();

        await detector.ObserveAsync(Change("Alice", 90_000));
        var alerts = await detector.ObserveAsync(Change("Alice", -500_000));

        Assert.Empty(alerts);
        Assert.Equal(90_000, detector.EarnedRecently("Alice"));
    }

    [Fact]
    public async Task EachPlayerIsCountedSeparately()
    {
        var alerts = await Build().ObserveAsync([("Alice", 60_000), ("Bob", 60_000)]);
        Assert.Empty(alerts);
    }

    // ---- the window slides ----

    [Fact]
    public async Task EarningsOutsideTheWindowStopCounting()
    {
        var detector = Build(window: TimeSpan.FromMinutes(15));

        await detector.ObserveAsync(Change("Alice", 90_000));
        _time.Advance(TimeSpan.FromMinutes(16));

        var alerts = await detector.ObserveAsync(Change("Alice", 90_000));

        Assert.Empty(alerts);
        Assert.Equal(90_000, detector.EarnedRecently("Alice"));
    }

    [Fact]
    public async Task EarningsJustInsideTheWindowStillCount()
    {
        var detector = Build(window: TimeSpan.FromMinutes(15));

        await detector.ObserveAsync(Change("Alice", 60_000));
        _time.Advance(TimeSpan.FromMinutes(14));

        Assert.Single(await detector.ObserveAsync(Change("Alice", 60_000)));
    }

    // ---- the cooldown ----

    [Fact]
    public async Task OneSpreeIsNotReportedOnEveryTick()
    {
        /* Without the cooldown a single spree alerts every tick for as long as it stays
           inside the window, and staff learn to ignore the channel. */
        var detector = Build();

        Assert.Single(await detector.ObserveAsync(Change("Alice", 250_000)));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(await detector.ObserveAsync(Change("Alice", 10_000)));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(await detector.ObserveAsync(Change("Alice", 10_000)));
    }

    [Fact]
    public async Task ANewSpreeAfterTheCooldownIsFlaggedAgain()
    {
        var detector = Build();

        await detector.ObserveAsync(Change("Alice", 250_000));
        _time.Advance(MoneyAnomalyDetector.AlertCooldown + TimeSpan.FromMinutes(1));

        Assert.Single(await detector.ObserveAsync(Change("Alice", 250_000)));
    }

    // ---- housekeeping ----

    [Fact]
    public async Task RowsAreDroppedOnceTheyExpire()
    {
        /* This dataset is written on every money tick forever. A player who earned once two
           years ago must not still have a row - and a detector that grows without bound is
           one that eventually makes every tick slower. */
        var detector = Build();

        await detector.ObserveAsync(Change("Alice", 1_000));
        _time.Advance(TimeSpan.FromMinutes(16) + MoneyAnomalyDetector.AlertCooldown);
        await detector.ObserveAsync(Change("Bob", 1_000));

        var stored = _store.Read(Datasets.MoneyWindow,
            new Dictionary<string, EarningWindow>(StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain("Alice", stored.Keys);
        Assert.Contains("Bob", stored.Keys);
    }

    [Fact]
    public async Task TheWindowSurvivesARestart()
    {
        // A detector that forgets on restart is one a deploy silently disarms, and this bot
        // gets deployed a lot. The state is in the store, not in the instance.
        await Build().ObserveAsync(Change("Alice", 90_000));

        var afterRestart = Build();

        Assert.Equal(90_000, afterRestart.EarnedRecently("Alice"));
        Assert.Single(await afterRestart.ObserveAsync(Change("Alice", 20_000)));
    }

    // ---- off by default ----

    [Fact]
    public async Task ZeroThresholdIsOff()
    {
        var detector = Build(threshold: 0);

        Assert.False(detector.Enabled);
        Assert.Empty(await detector.ObserveAsync(Change("Alice", 10_000_000)));
        Assert.Equal(0, detector.EarnedRecently("Alice"));
    }

    [Fact]
    public async Task NoChangesIsANoOp()
    {
        Assert.Empty(await Build().ObserveAsync([]));
    }
}
