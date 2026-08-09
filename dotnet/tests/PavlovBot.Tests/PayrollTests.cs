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

    /// <summary>One tick with a fresh roster listing exactly these players as online.</summary>
    private Task<PayrollRun> Tick(params string[] online) =>
        Build(Roster(fresh: true, online)).RunAsync();

    private void Wait(int minutes) => _time.Advance(TimeSpan.FromMinutes(minutes));

    /* ---- accrue on duty, bank on leaving ----

       THE FIRST TICK OF A SHIFT ACCRUES NOTHING, and nearly every test here opens with one.
       Pay is for time the bot OBSERVED somebody on duty, which means the span between two
       sightings; a member seen for the first time this tick has been on for an unknown part
       of the preceding gap. Crediting that gap pays for time nobody watched, and at a
       one-minute service interval the under-credit is at most a minute. */

    [Fact]
    public async Task AnOnlineOfficerAccruesButIsNotPaidYet()
    {
        /* THE WHOLE POINT. Their ledger must not be touched while they are in game: the
           server holds that balance in memory and overwrites the file on its next save. */
        PutOnRoster("Alice");
        GiveLedger("Alice", 1000);

        await Tick("Alice");                    // first sighting
        Wait(30);
        var run = await Tick("Alice");          // a whole period on duty

        Assert.Empty(run.Paid);
        Assert.Equal(500, run.Accrued["Alice"]);
        Assert.Equal(1000, BalanceOf("Alice"));
    }

    [Fact]
    public async Task TheWageIsBankedOnceTheyLogOff()
    {
        PutOnRoster("Alice");
        GiveLedger("Alice", 1000);

        await Tick("Alice");
        Wait(30);
        await Tick("Alice");                    // on duty: accrues
        Wait(1);
        var run = await Tick();                 // logged off: banks

        Assert.Equal(500, run.Paid["Alice"]);
        Assert.Equal(1500, BalanceOf("Alice"));
        Assert.Equal(0, Build(Roster(fresh: true)).OwedTo("Alice"));
    }

    [Fact]
    public async Task AWholeShiftIsBankedInOnePayment()
    {
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Tick("Alice");
        for (var i = 0; i < 4; i++)
        {
            Wait(30);
            await Tick("Alice");
        }

        Assert.Equal(2000, Build(Roster(fresh: true, "Alice")).OwedTo("Alice"));
        Assert.Equal(0, BalanceOf("Alice"));

        Wait(1);
        var run = await Tick();

        Assert.Equal(2000, run.Paid["Alice"]);
        Assert.Equal(2000, BalanceOf("Alice"));
    }

    // ---- whole periods only, with the remainder carried ----

    /// <summary>
    /// Forty-two minutes at 500 per thirty pays 500, then the second 500 eighteen minutes
    /// later. It never pays 1000 for 42 minutes and never rounds the 12 minutes away.
    /// </summary>
    /// <remarks>
    /// THE RULE THIS WHOLE ACCRUAL SHAPE EXISTS FOR. Payroll used to be a faction-wide wall
    /// clock: everybody online when the period boundary came round got a full period's wage,
    /// and anybody who logged off a minute before it got nothing for the twenty-nine minutes
    /// they had played. Two officers doing the same hour could be paid differently by half,
    /// decided by when they happened to log in.
    /// </remarks>
    [Fact]
    public async Task APartialPeriodIsCarriedAndPaidOnceItCompletes()
    {
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Tick("Alice");                                    // sighting
        var atThirty = await Advance(30, "Alice");              // 30 minutes on duty
        Assert.Equal(500, atThirty.Accrued["Alice"]);

        var atFortyTwo = await Advance(12, "Alice");            // 42 total: 12 carried
        Assert.Empty(atFortyTwo.Accrued);
        Assert.Equal(500, Build(Roster(fresh: true)).OwedTo("Alice"));
        Assert.Equal(TimeSpan.FromMinutes(12), Build(Roster(fresh: true)).EarnedTowardNextPeriod("Alice"));

        var atSixty = await Advance(18, "Alice");               // the remaining 18 completes it
        Assert.Equal(500, atSixty.Accrued["Alice"]);
        Assert.Equal(1000, Build(Roster(fresh: true)).OwedTo("Alice"));
        Assert.Equal(TimeSpan.Zero, Build(Roster(fresh: true)).EarnedTowardNextPeriod("Alice"));
    }

    /// <summary>Logging off banks the part-period rather than forfeiting it.</summary>
    /// <remarks>
    /// Otherwise the rule would be "whole periods, in one sitting", which punishes exactly
    /// the members who play in short stints - and quietly, since nothing reports a forfeit.
    /// </remarks>
    [Fact]
    public async Task CarriedTimeSurvivesLoggingOffAndBackOn()
    {
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Tick("Alice");
        await Advance(20, "Alice");                             // 20 minutes, then leaves
        await Advance(5);                                       // offline
        Assert.Equal(TimeSpan.FromMinutes(20), Build(Roster(fresh: true)).EarnedTowardNextPeriod("Alice"));

        await Tick("Alice");                                    // back on: a new first sighting
        var completes = await Advance(10, "Alice");             // 20 carried + 10 = one period

        Assert.Equal(500, completes.Accrued["Alice"]);
    }

    /// <summary>Two members on duty for different lengths are paid differently.</summary>
    /// <remarks>
    /// The half of the fix that a single-player test cannot see. Under the old wall clock
    /// both of these were paid the same, because the only thing that mattered was being
    /// online at the boundary.
    /// </remarks>
    [Fact]
    public async Task EachMemberIsPaidForTheirOwnTimeNotTheFactionsClock()
    {
        PutOnRoster("Alice", "Bob");
        GiveLedger("Alice", 0);
        GiveLedger("Bob", 0);

        await Tick("Alice");                                    // Alice starts
        await Advance(20, "Alice", "Bob");                      // Bob joins 20 minutes in
        var run = await Advance(10, "Alice", "Bob");            // Alice: 30. Bob: 10.

        Assert.Equal(500, run.Accrued["Alice"]);
        Assert.False(run.Accrued.ContainsKey("Bob"));
        Assert.Equal(TimeSpan.FromMinutes(10), Build(Roster(fresh: true)).EarnedTowardNextPeriod("Bob"));
    }

    /// <summary>Advance the clock, then tick with these players online.</summary>
    private Task<PayrollRun> Advance(int minutes, params string[] online)
    {
        Wait(minutes);
        return Tick(online);
    }

    [Fact]
    public async Task SomebodyOnlineButNotOnTheRosterAccruesNothing()
    {
        // A wage is for being on duty, not for being present.
        PutOnRoster("Alice");
        GiveLedger("Stranger", 100);

        await Tick("Alice", "Stranger");
        await Advance(30, "Alice", "Stranger");
        await Advance(1);

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

        await Tick("Alice");
        await Advance(30, "Alice");
        await Advance(1);

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

        await Tick("Alice");
        await Advance(30, "Alice");                 // 500 accrued and owed
        Wait(1);

        var stale = await Build(Roster(fresh: false)).RunAsync();

        Assert.Empty(stale.Paid);
        Assert.Empty(stale.Accrued);
        Assert.Contains("fresh", stale.Skipped, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1000, BalanceOf("Alice"));

        // The period was not consumed either, so nothing was lost by refusing.
        var recovered = await Tick();
        Assert.Equal(500, recovered.Paid["Alice"]);
    }

    [Fact]
    public async Task RunningTwiceInsideOnePeriodAccruesOnce()
    {
        // The supervisor restarts failed services, and a restart re-runs the tick. A second
        // run a moment later credits that moment, which is nowhere near a whole period.
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Tick("Alice");
        await Advance(30, "Alice");
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = await Tick("Alice");

        Assert.Empty(second.Accrued);
        Assert.Equal(500, Build(Roster(fresh: true)).OwedTo("Alice"));
    }

    [Fact]
    public async Task ALongOutageDoesNotAccrueABacklog()
    {
        /* Nobody was watching for those six hours, so nobody can be shown to have been on
           duty for them. One period is the most a single tick can ever credit. */
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Tick("Alice");
        _time.Advance(TimeSpan.FromHours(6));
        await Tick("Alice");

        Assert.Equal(500, Build(Roster(fresh: true)).OwedTo("Alice"));   // one period, not twelve
    }

    [Fact]
    public async Task NobodyOnDutyStillConsumesThePeriod()
    {
        /* "No officer was online" is a period that HAPPENED. Leaving it unstamped means the
           next tick a minute later credits the whole gap since the last real run. */
        PutOnRoster("Alice");
        GiveLedger("Alice", 0);

        await Tick();                               // nobody online; the period is stamped
        await Advance(1, "Alice");                  // Alice's first sighting
        var soonAfter = await Advance(1, "Alice");  // one observed minute

        Assert.Empty(soonAfter.Accrued);
        Assert.Equal(TimeSpan.FromMinutes(1), Build(Roster(fresh: true)).EarnedTowardNextPeriod("Alice"));
    }

    // ---- settlement that cannot complete ----

    [Fact]
    public async Task AnOfficerWithNoLedgerKeepsTheirWageOwed()
    {
        /* The bot never CREATES a ledger - the game does that when a player first banks. The
           wage must not be silently dropped for somebody who has not banked yet; it stays
           owed and lands the moment they have a file. */
        PutOnRoster("Newbie");

        await Tick("Newbie");
        await Advance(30, "Newbie");
        Wait(1);

        var noLedger = await Tick();

        Assert.Empty(noLedger.Paid);
        Assert.Equal(500, Build(Roster(fresh: true)).OwedTo("Newbie"));
        Assert.Null(BalanceOf("Newbie"));

        // The game creates their ledger; the next tick banks what was owed.
        GiveLedger("Newbie", 25);
        var afterFirstBank = await Tick();

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

        await Tick("Alice");
        await Advance(30, "Alice");                 // accrue 500 on duty
        await Advance(1);                           // bank it
        Assert.Equal(500, BalanceOf("Alice"));

        await Tick("Alice");                        // back on duty
        await Advance(30, "Alice");                 // accrue another 500
        await Advance(1);                           // and off again

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
        Wait(30);
        await Build(Roster(fresh: true, "Alice"), amount: 1000).RunAsync();
        Wait(1);
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

        await Tick("Alice");
        await Advance(30, "Alice");
        var banked = await Advance(1);

        // Assert the payment happened, or the rest of this passes against a directory
        // nothing ever wrote to.
        Assert.Equal(500, banked.Paid["Alice"]);
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

        await Tick("Alice");
        await Advance(30, "Alice");
        Assert.Empty(Build(Roster(fresh: true)).History());        // accrual alone is not a payment

        await Advance(1);

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

        await Tick("Alice", "Bob");
        await Advance(30, "Alice", "Bob");
        await Advance(30, "Alice", "Bob");

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
