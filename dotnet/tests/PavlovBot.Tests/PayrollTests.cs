using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Economy;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Wages, and the three ways paying them automatically goes wrong.
/// </summary>
/// <remarks>
/// Payroll writes to the same ledger files the game writes, on a timer, with nobody
/// watching. Every test here is about NOT paying: a stale roster, a double run, a backlog.
/// Paying correctly is one test; refusing to pay wrongly is five, which is the right ratio
/// for anything that mints currency.
/// </remarks>
public class PayrollTests : IDisposable
{
    private readonly string _rosters = Path.Combine(Path.GetTempPath(), "pavlov-payroll-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _time = new();
    private readonly MemoryBalances _balances = new();
    private readonly SerializedStore _store;
    private readonly RosterService _rosterService;

    public PayrollTests()
    {
        Directory.CreateDirectory(_rosters);
        _store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        _rosterService = new RosterService(_rosters, NullLogger<RosterService>.Instance);
    }

    /// <summary>A balance store in memory, so a wage is a number rather than a file.</summary>
    private sealed class MemoryBalances : IBalanceStore
    {
        public Dictionary<string, long> Balances { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Names whose write fails, for the "one bad ledger" case.</summary>
        public HashSet<string> Unwritable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long? Read(string playerId) => Balances.TryGetValue(playerId, out var v) ? v : null;

        public bool Write(string playerId, long balance)
        {
            if (Unwritable.Contains(playerId)) return false;
            Balances[playerId] = balance;
            return true;
        }
    }

    private void PutOnRoster(params string[] players)
    {
        var faction = FactionRegistry.All["NYPD"];
        var file = faction.RankFiles[faction.Default];
        File.WriteAllText(Path.Combine(_rosters, file), string.Join("\n", players) + "\n");
    }

    /// <summary>
    /// The online roster, with freshness the test controls.
    /// </summary>
    /// <remarks>
    /// The reason <see cref="IOnlineRoster"/> exists. RconRegistry keeps its roster cache
    /// private and measures freshness against the real clock, so "the roster is an hour
    /// stale" - the case that matters most here - could not be constructed at all.
    /// </remarks>
    private sealed record FakeRoster(bool IsTrustworthy, IReadOnlyList<string> Online) : IOnlineRoster;

    private static IOnlineRoster Roster(bool fresh, params string[] online) => new FakeRoster(fresh, online);

    private Payroll Build(IOnlineRoster online, long amount = 500, TimeSpan? period = null) =>
        new(_store, new Ledger(_balances), _rosterService, online,
            NullLogger<Payroll>.Instance, amount, period ?? TimeSpan.FromMinutes(30), "NYPD", _time);

    // ---- the happy path ----

    [Fact]
    public async Task OnDutyOfficersArePaid()
    {
        PutOnRoster("Alice", "Bob", "Carol");
        var payroll = Build(Roster(fresh: true, "Alice", "Bob"));   // Carol is offline

        var run = await payroll.RunAsync();

        Assert.Null(run.Skipped);
        Assert.Equal(2, run.Paid.Count);
        Assert.Equal(1000, run.Total);
        Assert.Equal(500, _balances.Balances["Alice"]);
        Assert.Equal(500, _balances.Balances["Bob"]);
        Assert.DoesNotContain("Carol", _balances.Balances.Keys);
    }

    [Fact]
    public async Task SomebodyOnlineButNotOnTheRosterIsNotPaid()
    {
        // A wage is for being on duty, not for being present.
        PutOnRoster("Alice");
        var payroll = Build(Roster(fresh: true, "Alice", "Stranger"));

        await payroll.RunAsync();

        Assert.DoesNotContain("Stranger", _balances.Balances.Keys);
    }

    // ---- the three ways it goes wrong ----

    [Fact]
    public async Task AStaleRosterPaysNobodyAndChargesNoPeriod()
    {
        /* AllOnlinePlayers serves the last SUCCESSFUL refresh. With RCON down for an hour
           that list is an hour old - it pays people who logged off and misses everyone who
           joined. Refusing costs one period; paying from it costs an argument.

           And crucially the period must NOT be stamped, or the outage silently eats a real
           payment once RCON comes back. */
        PutOnRoster("Alice");
        var payroll = Build(Roster(fresh: false, "Alice"));

        var skipped = await payroll.RunAsync();

        Assert.Empty(skipped.Paid);
        Assert.Contains("fresh", skipped.Skipped, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_balances.Balances);

        // RCON comes back within the same period: the payment that was owed still happens.
        var recovered = await Build(Roster(fresh: true, "Alice")).RunAsync();

        Assert.Null(recovered.Skipped);
        Assert.Equal(500, _balances.Balances["Alice"]);
    }

    [Fact]
    public async Task RunningTwiceInsideOnePeriodPaysOnce()
    {
        /* THE ONE THAT MINTS MONEY. The service supervisor restarts a failed service, and a
           restart re-runs the tick. Two runs a second apart must not both pay a full period. */
        PutOnRoster("Alice");
        var payroll = Build(Roster(fresh: true, "Alice"));

        await payroll.RunAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = await payroll.RunAsync();

        Assert.Empty(second.Paid);
        Assert.NotNull(second.Skipped);
        Assert.Equal(500, _balances.Balances["Alice"]);
    }

    [Fact]
    public async Task ALongOutageDoesNotPayABacklog()
    {
        /* The opposite mistake to the one above: after six hours down, paying twelve periods
           at once because they were "owed". Nobody was on duty for those hours. */
        PutOnRoster("Alice");
        var payroll = Build(Roster(fresh: true, "Alice"));

        await payroll.RunAsync();
        _time.Advance(TimeSpan.FromHours(6));
        await payroll.RunAsync();

        Assert.Equal(1000, _balances.Balances["Alice"]);   // two periods, not thirteen
    }

    [Fact]
    public async Task ThePeriodElapsingPaysAgain()
    {
        PutOnRoster("Alice");
        var payroll = Build(Roster(fresh: true, "Alice"));

        await payroll.RunAsync();
        _time.Advance(TimeSpan.FromMinutes(31));
        await payroll.RunAsync();

        Assert.Equal(1000, _balances.Balances["Alice"]);
    }

    [Fact]
    public async Task NobodyOnlineStillConsumesThePeriod()
    {
        /* "No officer was online" is a period that HAPPENED. Leaving it unstamped means the
           next tick a minute later pays a full period the moment one person logs in. */
        PutOnRoster("Alice");

        var quiet = await Build(Roster(fresh: true)).RunAsync();
        Assert.Empty(quiet.Paid);
        Assert.NotNull(quiet.Skipped);

        _time.Advance(TimeSpan.FromMinutes(1));
        var soonAfter = await Build(Roster(fresh: true, "Alice")).RunAsync();

        Assert.Empty(soonAfter.Paid);
        Assert.Empty(_balances.Balances);
    }

    // ---- partial failure ----

    [Fact]
    public async Task OneUnwritableLedgerDoesNotCostEverybodyElseTheirWage()
    {
        PutOnRoster("Alice", "Bob");
        _balances.Unwritable.Add("Bob");

        var run = await Build(Roster(fresh: true, "Alice", "Bob")).RunAsync();

        Assert.Single(run.Paid);
        Assert.Equal(500, _balances.Balances["Alice"]);
        Assert.DoesNotContain("Bob", _balances.Balances.Keys);
    }

    [Fact]
    public async Task WagesAccumulateOnTopOfAnExistingBalance()
    {
        _balances.Balances["Alice"] = 2000;
        PutOnRoster("Alice");

        await Build(Roster(fresh: true, "Alice")).RunAsync();

        Assert.Equal(2500, _balances.Balances["Alice"]);
    }

    // ---- off by default ----

    [Fact]
    public async Task ZeroAmountIsOff()
    {
        PutOnRoster("Alice");
        var payroll = Build(Roster(fresh: true, "Alice"), amount: 0);

        Assert.False(payroll.Enabled);
        var run = await payroll.RunAsync();

        Assert.Empty(run.Paid);
        Assert.Empty(_balances.Balances);
    }

    [Fact]
    public async Task HistoryRecordsWhatWasPaid()
    {
        PutOnRoster("Alice", "Bob");
        var payroll = Build(Roster(fresh: true, "Alice", "Bob"));

        await payroll.RunAsync();

        var history = payroll.History();
        Assert.Single(history);
        Assert.Equal(1000, history[0].Total);
        Assert.Equal(500, payroll.TotalPaidTo("Alice"));
    }

    [Fact]
    public async Task ASkippedRunIsNotWrittenToHistory()
    {
        // History is a payment record. Rows that paid nothing would bury the ones that did.
        PutOnRoster("Alice");

        await Build(Roster(fresh: false, "Alice")).RunAsync();

        Assert.Empty(Build(Roster(fresh: true)).History());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_rosters)) Directory.Delete(_rosters, recursive: true); } catch (IOException) { }
    }
}
