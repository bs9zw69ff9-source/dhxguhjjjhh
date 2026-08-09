using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Economy;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Economy;

/// <param name="Paid">Player -> amount actually SETTLED into their ledger this run.</param>
/// <param name="Accrued">Player -> amount added to what they are owed for being on duty.</param>
/// <param name="Skipped">Why nothing was accrued, when nothing was. Null on a normal run.</param>
public sealed record PayrollRun(
    DateTimeOffset At,
    string Faction,
    IReadOnlyDictionary<string, long> Paid,
    IReadOnlyDictionary<string, long> Accrued,
    string? Skipped)
{
    public long Total => Paid.Values.Sum();

    public bool DidSomething => Paid.Count > 0 || Accrued.Count > 0;
}

/// <summary>Last payout instant per faction, and what each officer is still owed.</summary>
/// <remarks>
/// <see cref="Owed"/> IS THE WAGE THAT HAS BEEN EARNED BUT NOT YET BANKED. It has to be
/// persisted rather than held in memory: a shift can outlast the bot process, and losing an
/// officer's accrued pay to a deploy is exactly the kind of thing nobody reports and everybody
/// notices.
///
/// THE DEFAULT IS A METHOD, NOT A CACHED STATIC, and that distinction is load-bearing.
/// <c>SerializedStore.UpdateAsync</c> hands the fallback INSTANCE to the mutator when nothing
/// is stored yet, and the mutator writes into <see cref="LastPaid"/>. A
/// <c>static PayrollState Empty { get; }</c> holding one dictionary therefore gets MUTATED by
/// the first payroll run, and every later "empty" default in the process comes back already
/// carrying that timestamp.
///
/// Found by a test, not by reading: the first payroll run in a suite passed and every one
/// after it was refused with "only 0 minutes since the last run", because they were all
/// sharing one dictionary. In production the same bug makes payroll pay once and then refuse
/// forever after any restart that leaves the row unwritten.
/// </remarks>
/// <param name="LastPaid">Faction -> when its last tick ran. Used to measure elapsed time.</param>
/// <param name="Owed">Player -> earned but not yet banked.</param>
/// <param name="OnDutySeconds">
/// Player -> on-duty time carried toward their next whole period.
/// </param>
/// <param name="LastOnDuty">Faction -> who was on duty at the previous tick.</param>
/// <remarks>
/// THE LAST TWO ARE NULLABLE WITH DEFAULTS so a payroll_state written before they existed
/// still deserialises. A live server has one of those files, and a missing property that
/// threw would take payroll down on the deploy that introduced it.
/// </remarks>
public sealed record PayrollState(
    Dictionary<string, DateTimeOffset> LastPaid,
    Dictionary<string, long> Owed,
    Dictionary<string, long>? OnDutySeconds = null,
    Dictionary<string, List<string>>? LastOnDuty = null)
{
    /* NOT `??=`. The positional properties are init-only, so they cannot be assigned after
       construction - and a lazily-created dictionary that is thrown away on every read would
       silently lose every write. Materialised once here instead, which also means the record
       stays a plain data shape rather than one with hidden mutation. */
    private readonly Dictionary<string, long> _earned =
        OnDutySeconds ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<string>> _previous =
        LastOnDuty ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> Earned => _earned;

    public Dictionary<string, List<string>> Previous => _previous;

    public static PayrollState New() => new(
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Wages for on-duty faction members: ACCRUED while they play, BANKED once they log off.
/// </summary>
/// <remarks>
/// WHY IT IS TWO STEPS. A wage has to be earned by being on duty, and the money lives in a
/// file the GAME owns and rewrites from memory while a player is connected. Those two facts
/// pull in opposite directions: the moment somebody is worth paying is the exact moment their
/// ledger cannot safely be written. Crediting a live player is what previously put balances
/// out of step with the servers.
///
/// So being on duty accrues a debt in the bot's own database, and the debt is settled to the
/// ledger on the first tick after they leave, when the game no longer holds their balance.
/// Paying offline players DIRECTLY was the other way to read the same rule and is nonsense -
/// it pays somebody who has not played in a year, forever.
///
/// ON THE ROSTER AND ONLINE, for accrual. Accruing for everybody on the NYPD roster would
/// pay people who are asleep, which is not a wage, it is a subscription.
///
/// THREE WAYS THIS COULD PAY THE WRONG PEOPLE OR THE WRONG AMOUNT, all guarded:
///
///   A STALE ROSTER. <see cref="IOnlineRoster.Online"/> serves the last successful refresh.
///   If RCON has been down for an hour that list is an hour old, so it would accrue for people
///   who logged off and - worse - settle ledgers for people who are actually still playing.
///   The run is SKIPPED unless the roster is genuinely fresh.
///
///   A DOUBLE ACCRUAL. The service supervisor restarts a failed service, and a restart re-runs
///   the tick. Two runs a second apart would both accrue a full period. The last run instant
///   is PERSISTED and a run inside the period is refused, so a restart loop cannot mint money.
///
///   A CATCH-UP ACCRUAL. The opposite mistake: after six hours down, accruing twelve periods
///   at once because they were "owed". Nobody was on duty for those hours. A run accrues ONE
///   period, whatever happened before it.
///
/// SETTLEMENT IS NOT RATE LIMITED, and that is deliberate - it moves money the officer has
/// already earned rather than creating any, so it runs on every tick and gets the wage banked
/// promptly after they leave.
///
/// CREDITS THROUGH <see cref="Ledger"/>, never the balance store directly, so a settlement and
/// a <c>/givecaps</c> landing together cannot double-spend through the per-player queue.
/// </remarks>
public sealed class Payroll(
    SerializedStore store,
    Ledger ledger,
    RosterService rosters,
    IOnlineRoster online,
    ILogger<Payroll> logger,
    long amount,
    TimeSpan period,
    string factionName = "NYPD",
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool Enabled => amount > 0 && period > TimeSpan.Zero && rosters.Enabled;

    public long Amount => amount;
    public TimeSpan Period => period;
    public string Faction => factionName;

    /// <summary>
    /// How many runs are kept for <c>/stats</c> and audits.
    /// </summary>
    /// <remarks>
    /// Bounded because this list is written every period forever. At one run an hour, 500 is
    /// about three weeks - long enough to answer "was I paid on Tuesday" and short enough
    /// that the row never becomes the largest thing in the database.
    /// </remarks>
    public const int HistoryLimit = 500;

    /// <summary>
    /// Pay one period, or explain why it did not.
    /// </summary>
    /// <remarks>
    /// Returns a run either way. A skipped run is recorded with its reason rather than
    /// swallowed, because "why did nobody get paid last night" is answerable only if the
    /// refusals were written down too.
    /// </remarks>
    public async Task<PayrollRun> RunAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var none = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (!Enabled) return new PayrollRun(now, factionName, none, none, "payroll is not configured");

        if (!FactionRegistry.All.TryGetValue(factionName, out var faction))
            return new PayrollRun(now, factionName, none, none, $"no faction named \"{factionName}\"");

        /* ---- guard: is the roster current ----
           Checked before ANYTHING, including settlement, because settling needs to know who
           is offline just as much as accruing needs to know who is on. A stale list would
           bank a wage for somebody who is in fact still playing, which is the exact write
           this design exists to avoid. */
        if (!online.IsTrustworthy)
        {
            logger.LogWarning(
                "Payroll for {Faction} SKIPPED - no server has a fresh player list. Nothing was accrued, " +
                "nothing was banked, and no period was consumed", factionName);
            return new PayrollRun(now, factionName, none, none, "no server has a fresh player list");
        }

        var present = online.Online.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roster = await rosters.RosterAsync(faction, ct).ConfigureAwait(false);

        // ---- 1. bank what the people who have left already earned ----
        var paid = await SettleAsync(present, ct).ConfigureAwait(false);

        /* ---- 2. credit the time they actually played ----

           PAID FOR TIME ON DUTY, IN WHOLE PERIODS, WITH THE REMAINDER CARRIED. This used to
           be a faction-wide wall clock: every member online at a period boundary got a full
           period's pay, and anybody who logged off a minute before it got nothing for the
           twenty-nine minutes they had played. Two officers doing the same hour could be paid
           differently by half, decided by when they happened to log in.

           Now each member banks their own seconds. Every whole period in that total converts
           to one period's wage and is subtracted; what is left over stays and counts toward
           the next one. An hour at 30 per 30 minutes is 60. Forty-two minutes is 30, with
           twelve minutes carried - and eighteen minutes later, the second 30. */
        var state = store.Read(Datasets.PayrollState, PayrollState.New());

        var previous = state.Previous.GetValueOrDefault(factionName) ?? [];
        var wasOnDuty = previous.ToHashSet(StringComparer.OrdinalIgnoreCase);

        /* CAPPED AT ONE PERIOD. If the bot was down for six hours, the members online when it
           comes back were not necessarily on duty for those hours - nobody was watching. The
           existing rule was "a run accrues ONE period, whatever happened before it", and this
           keeps it: an outage can never mint more than a single period per member. */
        var sinceLast = state.LastPaid.TryGetValue(factionName, out var last)
            ? now - last
            : TimeSpan.Zero;
        var credited = sinceLast > period ? period : sinceLast;

        var onDuty = roster.Where(m => present.Contains(m.Player)).Select(m => m.Player).ToList();

        var accrued = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var carried = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var player in onDuty)
        {
            /* ONLY TIME BETWEEN TWO SIGHTINGS COUNTS. Somebody seen for the first time this
               tick has been on duty for an unknown part of the gap, so they are credited from
               now rather than from the last tick. That under-credits by at most one tick, and
               the alternative pays people for time nobody observed. */
            var seconds = state.Earned.GetValueOrDefault(player);
            if (wasOnDuty.Contains(player)) seconds += (long)credited.TotalSeconds;

            var periods = seconds / (long)period.TotalSeconds;
            if (periods > 0)
            {
                accrued[player] = periods * amount;
                seconds -= periods * (long)period.TotalSeconds;
            }

            carried[player] = seconds;
        }

        await StampAsync(now, factionName, accrued, carried, onDuty, ct).ConfigureAwait(false);

        if (accrued.Count > 0)
        {
            logger.LogInformation(
                "Payroll accrued {Total:N0} across {Count} {Faction} member(s) who completed a full period - banked when they log off",
                accrued.Values.Sum(), accrued.Count, factionName);
        }

        var skipped = onDuty.Count == 0
            ? $"no {factionName} member is online"
            : accrued.Count == 0 ? "nobody has completed a full period yet" : null;
        var run = new PayrollRun(now, factionName, paid, accrued, skipped);

        // History records money that MOVED, not money promised - so it is written from the
        // settlement, which is the only part that touches a balance.
        if (paid.Count > 0) await RecordAsync(run, ct).ConfigureAwait(false);

        return run;
    }

    private Task RecordAsync(PayrollRun run, CancellationToken ct) =>
        store.UpdateAsync<List<PayrollRun>>(Datasets.Wages, [], history =>
        {
            history.Add(run);
            if (history.Count > HistoryLimit) history.RemoveRange(0, history.Count - HistoryLimit);
            return history;
        }, ct);

    /// <summary>
    /// Bank the accrued wage of everybody who is no longer in game.
    /// </summary>
    /// <remarks>
    /// The debt is cleared ONLY on a successful credit. A ledger that refuses the write - the
    /// player has none yet, the file is locked, the directory is wrong - leaves the wage owed
    /// and it is tried again next tick. Clearing first would silently delete somebody's pay
    /// the one time the write failed.
    /// </remarks>
    private async Task<Dictionary<string, long>> SettleAsync(
        IReadOnlySet<string> present, CancellationToken ct)
    {
        var paid = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var owed = store.Read(Datasets.PayrollState, PayrollState.New()).Owed
            .Where(kv => kv.Value > 0 && !present.Contains(kv.Key))
            .ToList();

        foreach (var (player, wage) in owed)
        {
            ct.ThrowIfCancellationRequested();

            var change = await ledger.CreditAsync(player, wage, ct).ConfigureAwait(false);
            if (!change.Ok)
            {
                logger.LogDebug("Payroll could not bank {Wage:N0} for {Player} yet - still owed", wage, player);
                continue;
            }

            paid[player] = wage;

            await store.UpdateAsync(Datasets.PayrollState, PayrollState.New(), state =>
            {
                /* Subtract what was banked rather than removing the key, because a period may
                   have accrued between the read above and this write. Taking the whole entry
                   out would swallow it. */
                if (!state.Owed.TryGetValue(player, out var current)) return null;

                var remaining = current - wage;
                if (remaining > 0) state.Owed[player] = remaining; else state.Owed.Remove(player);
                return state;
            }, ct).ConfigureAwait(false);

            logger.LogInformation("Payroll banked {Wage:N0} for {Player} (balance {Before:N0} -> {After:N0})",
                wage, player, change.Before, change.After);
        }

        return paid;
    }

    /// <summary>
    /// Consume the period and add this run's accrual to what each officer is owed.
    /// </summary>
    /// <remarks>
    /// THE PERIOD IS CONSUMED EVEN WHEN NOBODY WAS ON DUTY, and that is deliberate. "No
    /// officer was online" is a period that HAPPENED - leaving it unstamped means the next
    /// tick a minute later accrues a full period the moment one person logs in. It is not
    /// stamped when the roster could not be trusted, because that run never took place.
    /// </remarks>
    private Task StampAsync(
        DateTimeOffset now, string faction, Dictionary<string, long> accrued,
        Dictionary<string, long> carried, IReadOnlyList<string> onDuty, CancellationToken ct) =>
        store.UpdateAsync(Datasets.PayrollState, PayrollState.New(), state =>
        {
            state.LastPaid[faction] = now;
            foreach (var (player, wage) in accrued)
                state.Owed[player] = state.Owed.GetValueOrDefault(player) + wage;

            /* The remainder is the whole point: it is what makes 42 minutes pay once now and
               once eighteen minutes later, rather than rounding somebody's time away. It is
               kept for members who are OFFLINE too - StampAsync only writes the players seen
               this tick, so logging off banks the partial period rather than forfeiting it.

               A ZERO IS REMOVED rather than stored. Every member who ever completes a period
               lands on exactly zero, and keeping those rows would grow the state file with
               entries that mean "nothing carried" - which is what an absent key already
               means. */
            foreach (var (player, seconds) in carried)
            {
                if (seconds > 0) state.Earned[player] = seconds;
                else state.Earned.Remove(player);
            }

            state.Previous[faction] = [.. onDuty];
            return state;
        }, ct);

    /// <summary>On-duty time a member has banked toward their next whole period.</summary>
    public TimeSpan EarnedTowardNextPeriod(string player) =>
        TimeSpan.FromSeconds(store.Read(Datasets.PayrollState, PayrollState.New())
            .Earned.GetValueOrDefault(player.Trim()));

    /// <summary>What an officer has earned on duty but not yet had banked.</summary>
    public long OwedTo(string player) =>
        store.Read(Datasets.PayrollState, PayrollState.New()).Owed.GetValueOrDefault(player.Trim());

    /// <summary>Everybody currently carrying an unbanked wage, largest first.</summary>
    public IReadOnlyList<(string Player, long Owed)> Outstanding() =>
        store.Read(Datasets.PayrollState, PayrollState.New()).Owed
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

    /// <summary>Recent runs, newest first.</summary>
    public IReadOnlyList<PayrollRun> History(int take = 10) =>
        store.Read<List<PayrollRun>>(Datasets.Wages, [])
            .Where(r => r is not null)
            .OrderByDescending(r => r.At)
            .Take(take)
            .ToList();

    /// <summary>Everything paid to one player, across every recorded run.</summary>
    public long TotalPaidTo(string player) =>
        store.Read<List<PayrollRun>>(Datasets.Wages, [])
            .Where(r => r?.Paid is not null)
            .Sum(r => r.Paid.TryGetValue(player, out var amount) ? amount : 0);
}
