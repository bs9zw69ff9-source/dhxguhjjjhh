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
/// Wages: accrued while on duty, banked once the officer logs off.
/// </summary>
/// <remarks>
/// THE TWO-STEP SHAPE IS FORCED BY WHO OWNS THE MONEY. Balances live in
/// <c>modsave/&lt;player&gt;.txt</c>, which the GAME rewrites from memory while a player is
/// connected - so the moment somebody is worth paying is the exact moment their ledger cannot
/// safely be written. Writing it anyway is what previously put balances out of step with the
/// servers.
///
/// Payroll writes to real currency on a timer with nobody watching, so most of these are
/// about NOT paying: a stale roster, a double run, a backlog, a settlement that fails.
/// </remarks>
public class PayrollTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pavlov-payroll-" + Guid.NewGuid().ToString("N"));
    private readonly string _rosters;
    private readonly string _ledgers;
    private readonly TestClock _time = new();
    private readonly SerializedStore _store;
    private readonly RosterService _rosterService;

    public PayrollTests()
    {
        _rosters = Path.Combine(_root, "rosters");
        _ledgers = Path.Combine(_root, "modsave");
        Directory.CreateDirectory(_rosters);
        Directory.CreateDirectory(_ledgers);

        _store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        _rosterService = new RosterService(_rosters, NullLogger<RosterService>.Instance);
    }

    /// <summary>The online roster, with freshness the test controls.</summary>
    /// <remarks>
    /// The reason <see cref="IOnlineRoster"/> exists: RconRegistry keeps its roster cache
    /// private and measures freshness against the real clock, so "the list is an hour stale" -
    /// the case that matters most here - could not be constructed at all.
    /// </remarks>
    private sealed record FakeRoster(bool IsTrustworthy, IReadOnlyList<string> Online) : IOnlineRoster;

    private static IOnlineRoster Roster(bool fresh, params string[] online) => new FakeRoster(fresh, online);

    private void PutOnRoster(params string[] players)
    {
        var faction = FactionRegistry.All["NYPD"];
        File.WriteAllText(
            Path.Combine(_rosters, faction.RankFiles[faction.Default]),
            string.Join("\n", players) + "\n");
    }

    /// <summary>Give a player a ledger. The bot never creates one, so this is a precondition.</summary>
    private void GiveLedger(string player, long balance = 0) =>
        File.WriteAllText(Path.Combine(_ledgers, $"{player}.txt"),
            balance.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private long? BalanceOf(string player)
    {
        var path = Path.Combine(_ledgers, $"{player}.txt");
        return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : null;
    }

    /// <summary>
    /// The REAL file-backed store, not a fake.
    /// </summary>
    /// <remarks>
    /// Deliberately the real one. An earlier version of these tests used an in-memory balance
    /// store that implemented Write honestly - and passed, while the production
    /// LedgerFileStore refused every write and payroll could not pay anybody at all.
    /// </remarks>
    private Payroll Build(IOnlineRoster online, long amount = 500, TimeSpan? period = null) =>
        new(_store, new Ledger(new LedgerFileStore(_ledgers, online)), _rosterService, online,
            NullLogger<Payroll>.Instance, amount, period ?? TimeSpan.FromMinutes(30), "NYPD", _time);

    // ---- accrue on duty, bank on leaving ----

    [Fact]
    public async Task AnOnlineOfficerAccruesButIsNotPaidYet()
    {
        /* THE WHOLE POINT. Their ledger must not be touched while they are in game: the
           server holds that balance in memory and overwrites the file on its next save. */
        PutOnRoster("Alice");
        GiveLedger("Alice", 1000);

        var run = await Build(Roster(fresh: true, "Alice")).RunAsync();

        Assert.Empty(run.Paid);
        Assert.Equal(500, run.Accrued["Alice"]);
        Assert.Equal(1000, BalanceOf("Alice"));
    }

    [Fact]
    public async Task TheWageIsBankedOnceTheyLogOff()
    {
        PutOnRoster("Alice");
        GiveLedger("Alice", 1000);

        await Build(Roster(fresh: true, "Alice")).RunAsync();       // on duty: accrues
        _time.Advance(TimeSpan.FromMinutes(31));
        var run = await Build(Roster(fresh: true)).RunAsync();      // logged off: banks

        Assert.Equal(500, run.Paid["Alice"]);
        Assert.Equal(1500, BalanceOf("Alice"));
        Assert.Equal(0, Build(Roster(fresh: true)).OwedTo("Alice"));
    }

    [Fact]
    public async Task AWholeShiftIsBankedInOnePayment()
    {
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        for (var i = 0; i < 4; i++)
        {
            await Build(Roster(fresh: true, "Alice")).RunAsync();
            _time.Advance(TimeSpan.FromMinutes(31));
        }

        Assert.Equal(2000, Build(Roster(fresh: true, "Alice")).OwedTo("Alice"));
        Assert.Equal(0, BalanceOf("Alice"));

        var run = await Build(Roster(fresh: true)).RunAsync();

        Assert.Equal(2000, run.Paid["Alice"]);
        Assert.Equal(2000, BalanceOf("Alice"));
    }

    [Fact]
    public async Task SomebodyOnlineButNotOnTheRosterAccruesNothing()
    {
        // A wage is for being on duty, not for being present.
        PutOnRoster("Alice");
        GiveLedger("Stranger", 100);

        await Build(Roster(fresh: true, "Alice", "Stranger")).RunAsync();
        _time.Advance(TimeSpan.FromMinutes(31));
        await Build(Roster(fresh: true)).RunAsync();

        Assert.Equal(100, BalanceOf("Stranger"));
    }

    [Fact]
    public async Task AnOfflineOfficerWhoNeverServedIsNotPaid()
    {
        /* The literal reading of "pay offline players" - which would credit somebody who has
           not played in a year, every period, forever. They accrue nothing, so they get
           nothing. */
        PutOnRoster("Alice", "Dormant");
        GiveLedger("Dormant", 50);

        await Build(Roster(fresh: true, "Alice")).RunAsync();
        _time.Advance(TimeSpan.FromMinutes(31));
        await Build(Roster(fresh: true)).RunAsync();

        Assert.Equal(50, BalanceOf("Dormant"));
    }

    // ---- the ways it goes wrong ----

    [Fact]
    public async Task AStaleRosterAccruesNothingAndBanksNothing()
    {
        /* A stale list would accrue for people who logged off and - far worse - BANK a wage
           for somebody who is actually still playing, which is the write this whole design
           exists to avoid. */
        PutOnRoster("Alice");
        GiveLedger("Alice", 1000);

        await Build(Roster(fresh: true, "Alice")).RunAsync();
        _time.Advance(TimeSpan.FromMinutes(31));

        var stale = await Build(Roster(fresh: false)).RunAsync();

        Assert.Empty(stale.Paid);
        Assert.Empty(stale.Accrued);
        Assert.Contains("fresh", stale.Skipped, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1000, BalanceOf("Alice"));

        // The period was not consumed either, so nothing was lost by refusing.
        var recovered = await Build(Roster(fresh: true)).RunAsync();
        Assert.Equal(500, recovered.Paid["Alice"]);
    }

    [Fact]
    public async Task RunningTwiceInsideOnePeriodAccruesOnce()
    {
        // The supervisor restarts failed services, and a restart re-runs the tick.
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Build(Roster(fresh: true, "Alice")).RunAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = await Build(Roster(fresh: true, "Alice")).RunAsync();

        Assert.Empty(second.Accrued);
        Assert.Equal(500, Build(Roster(fresh: true)).OwedTo("Alice"));
    }

    [Fact]
    public async Task ALongOutageDoesNotAccrueABacklog()
    {
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Build(Roster(fresh: true, "Alice")).RunAsync();
        _time.Advance(TimeSpan.FromHours(6));
        await Build(Roster(fresh: true, "Alice")).RunAsync();

        Assert.Equal(1000, Build(Roster(fresh: true)).OwedTo("Alice"));   // two periods, not thirteen
    }

    [Fact]
    public async Task NobodyOnDutyStillConsumesThePeriod()
    {
        /* "No officer was online" is a period that HAPPENED. Leaving it unstamped means the
           next tick a minute later accrues a full period the moment one person logs in. */
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Build(Roster(fresh: true)).RunAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var soonAfter = await Build(Roster(fresh: true, "Alice")).RunAsync();

        Assert.Empty(soonAfter.Accrued);
    }

    // ---- settlement that cannot complete ----

    [Fact]
    public async Task AnOfficerWithNoLedgerKeepsTheirWageOwed()
    {
        /* The bot never CREATES a ledger - the game does that when a player first banks. The
           wage must not be silently dropped for somebody who has not banked yet; it stays
           owed and lands the moment they have a file. */
        PutOnRoster("Newbie");

        await Build(Roster(fresh: true, "Newbie")).RunAsync();
        _time.Advance(TimeSpan.FromMinutes(31));

        var noLedger = await Build(Roster(fresh: true)).RunAsync();

        Assert.Empty(noLedger.Paid);
        Assert.Equal(500, Build(Roster(fresh: true)).OwedTo("Newbie"));
        Assert.Null(BalanceOf("Newbie"));

        // The game creates their ledger; the next tick banks what was owed.
        GiveLedger("Newbie", 25);
        var afterFirstBank = await Build(Roster(fresh: true)).RunAsync();

        Assert.Equal(500, afterFirstBank.Paid["Newbie"]);
        Assert.Equal(525, BalanceOf("Newbie"));
    }

    [Fact]
    public async Task AWageAccruedWhileSettlingIsNotSwallowed()
    {
        /* Settlement subtracts what it banked rather than removing the entry, because a
           period can accrue between reading the debt and clearing it. Removing the key would
           lose that period. */
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Build(Roster(fresh: true, "Alice")).RunAsync();      // accrue 500 on duty
        _time.Advance(TimeSpan.FromMinutes(31));

        await Build(Roster(fresh: true)).RunAsync();               // bank it
        Assert.Equal(500, BalanceOf("Alice"));

        _time.Advance(TimeSpan.FromMinutes(31));
        await Build(Roster(fresh: true, "Alice")).RunAsync();      // back on duty
        _time.Advance(TimeSpan.FromMinutes(31));
        await Build(Roster(fresh: true)).RunAsync();               // and off again

        Assert.Equal(1000, BalanceOf("Alice"));
    }

    // ---- the ledger file itself ----

    [Fact]
    public async Task TheLedgerIsABareNumberTheGameCanRead()
    {
        // modsave/<player>.txt is a bare integer and nothing else. Anything the bot adds -
        // a newline, a thousands separator, a BOM - is a balance the game cannot parse.
        PutOnRoster("Alice");
        GiveLedger("Alice", 1234);

        await Build(Roster(fresh: true, "Alice"), amount: 1000).RunAsync();
        _time.Advance(TimeSpan.FromMinutes(31));
        await Build(Roster(fresh: true), amount: 1000).RunAsync();

        var raw = File.ReadAllText(Path.Combine(_ledgers, "Alice.txt"));

        Assert.Equal("2234", raw);
    }

    [Fact]
    public async Task NoTemporaryFilesAreLeftInTheLedgerDirectory()
    {
        // The write is temp-then-rename so the game never reads a half-written number. A
        // leftover .tmp would be enumerated by MoneyLog as a player called "Alice.txt.bot".
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Build(Roster(fresh: true, "Alice")).RunAsync();
        _time.Advance(TimeSpan.FromMinutes(31));
        await Build(Roster(fresh: true)).RunAsync();

        Assert.Empty(Directory.EnumerateFiles(_ledgers, "*.tmp"));
        Assert.Single(Directory.EnumerateFiles(_ledgers));
    }

    // ---- off by default ----

    [Fact]
    public async Task ZeroAmountIsOff()
    {
        PutOnRoster("Alice");
        GiveLedger("Alice", 100);
        var payroll = Build(Roster(fresh: true, "Alice"), amount: 0);

        Assert.False(payroll.Enabled);
        var run = await payroll.RunAsync();

        Assert.False(run.DidSomething);
        Assert.Equal(100, BalanceOf("Alice"));
    }

    // ---- reporting ----

    [Fact]
    public async Task HistoryRecordsMoneyThatMovedRatherThanMoneyPromised()
    {
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Build(Roster(fresh: true, "Alice")).RunAsync();
        Assert.Empty(Build(Roster(fresh: true)).History());        // accrual alone is not a payment

        _time.Advance(TimeSpan.FromMinutes(31));
        await Build(Roster(fresh: true)).RunAsync();

        var history = Build(Roster(fresh: true)).History();
        Assert.Single(history);
        Assert.Equal(500, history[0].Total);
        Assert.Equal(500, Build(Roster(fresh: true)).TotalPaidTo("Alice"));
    }

    [Fact]
    public async Task OutstandingListsWhoIsCarryingAnUnbankedWage()
    {
        PutOnRoster("Alice", "Bob");
        GiveLedger("Alice", 0);
        GiveLedger("Bob", 0);

        await Build(Roster(fresh: true, "Alice", "Bob")).RunAsync();
        _time.Advance(TimeSpan.FromMinutes(31));
        await Build(Roster(fresh: true, "Alice", "Bob")).RunAsync();

        var outstanding = Build(Roster(fresh: true)).Outstanding();

        Assert.Equal(2, outstanding.Count);
        Assert.All(outstanding, row => Assert.Equal(1000, row.Owed));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
