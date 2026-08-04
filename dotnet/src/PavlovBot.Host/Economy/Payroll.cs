using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Economy;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Economy;

/// <param name="Paid">Player -> amount credited. Empty when nobody qualified.</param>
/// <param name="Skipped">Why nothing was paid, when nothing was. Null on a normal run.</param>
public sealed record PayrollRun(
    DateTimeOffset At, string Faction, IReadOnlyDictionary<string, long> Paid, string? Skipped)
{
    public long Total => Paid.Values.Sum();
}

/// <summary>Last payout instant per faction.</summary>
/// <remarks>
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
public sealed record PayrollState(Dictionary<string, DateTimeOffset> LastPaid)
{
    public static PayrollState New() => new(new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Wages for on-duty faction members, paid on a timer.
/// </summary>
/// <remarks>
/// ON THE ROSTER AND ONLINE. Paying everybody on the NYPD roster would pay people who are
/// asleep, which is not a wage - it is a subscription. The intersection of "on the roster"
/// and "on a server right now" is what makes it a duty payment.
///
/// THREE WAYS THIS COULD PAY THE WRONG PEOPLE OR THE WRONG AMOUNT, all guarded:
///
///   A STALE ROSTER. <see cref="RconRegistry.AllOnlinePlayers"/> serves the last successful
///   refresh. If RCON has been down for an hour that list is an hour old, and paying from it
///   pays people who logged off and misses everyone who joined. The run is SKIPPED unless at
///   least one server's roster is genuinely fresh - a missed payment is recoverable and a
///   wrong one is an argument.
///
///   A DOUBLE PAY. The service supervisor restarts a failed service, and a restart re-runs
///   the tick. Two runs a second apart would both pay a full period. The last payout instant
///   is PERSISTED and a run inside the period is refused, so a restart loop cannot mint money.
///
///   A CATCH-UP PAY. The opposite mistake: after six hours down, paying six periods at once
///   on the theory that they were "owed". Nobody was on duty for those hours. A run pays ONE
///   period, whatever happened before it.
///
/// CREDITS THROUGH <see cref="Ledger"/>, never the balance store directly, because the game
/// writes these same files. Read-then-write without the per-player queue is a double-spend:
/// a payout and a purchase land together and one of them vanishes.
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
        var empty = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (!Enabled) return new PayrollRun(now, factionName, empty, "payroll is not configured");

        if (!FactionRegistry.All.TryGetValue(factionName, out var faction))
            return new PayrollRun(now, factionName, empty, $"no faction named \"{factionName}\"");

        // ---- guard: has a full period actually elapsed ----
        var state = store.Read(Datasets.PayrollState, PayrollState.New());
        if (state.LastPaid.TryGetValue(factionName, out var last) && now - last < period)
        {
            var wait = period - (now - last);
            logger.LogDebug("Payroll for {Faction} skipped - {Minutes:0} minute(s) left in the period",
                factionName, wait.TotalMinutes);
            return new PayrollRun(now, factionName, empty, $"only {(now - last).TotalMinutes:0} minutes since the last run");
        }

        // ---- guard: is the roster we are about to pay from actually current ----
        if (!online.IsTrustworthy)
        {
            /* NOT STAMPED. The period is deliberately left unconsumed here, unlike the
               "nobody online" case below - this run never happened, so the payment it would
               have made is still owed the moment RCON comes back. */
            logger.LogWarning(
                "Payroll for {Faction} SKIPPED - no server has a fresh player list, so the online roster " +
                "cannot be trusted. Nobody was paid and nobody was charged a period", factionName);
            return new PayrollRun(now, factionName, empty, "no server has a fresh player list");
        }

        var present = online.Online.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (present.Count == 0)
            return await FinishAsync(now, factionName, empty, "nobody is online", ct).ConfigureAwait(false);

        var roster = await rosters.RosterAsync(faction, ct).ConfigureAwait(false);
        var onDuty = roster.Where(m => present.Contains(m.Player)).ToList();

        if (onDuty.Count == 0)
            return await FinishAsync(now, factionName, empty, $"no {factionName} member is online", ct).ConfigureAwait(false);

        var paid = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in onDuty)
        {
            ct.ThrowIfCancellationRequested();

            var change = await ledger.CreditAsync(member.Player, amount, ct).ConfigureAwait(false);
            if (change.Ok) { paid[member.Player] = amount; continue; }

            /* A failed credit is NOT fatal to the run. One unwritable ledger - the game
               holding the file, a name with something odd in it - must not cost every other
               officer their wage. */
            logger.LogWarning("Payroll could not credit {Player} - their ledger did not accept the write", member.Player);
        }

        logger.LogInformation("Payroll paid {Count} {Faction} member(s) {Amount:N0} each ({Total:N0} total)",
            paid.Count, factionName, amount, paid.Count * amount);

        return await FinishAsync(now, factionName, paid, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stamp the period as paid and append the run to history.
    /// </summary>
    /// <remarks>
    /// THE PERIOD IS STAMPED EVEN WHEN NOBODY QUALIFIED, and that is deliberate. "No officer
    /// was online" is a period that HAPPENED - leaving it unstamped means the next tick a
    /// minute later pays a full period the moment one person logs in. It is not stamped for
    /// the guard refusals above, because those mean the run never took place.
    /// </remarks>
    private async Task<PayrollRun> FinishAsync(
        DateTimeOffset now, string faction, Dictionary<string, long> paid, string? skipped, CancellationToken ct)
    {
        var run = new PayrollRun(now, faction, paid, skipped);

        await store.UpdateAsync(Datasets.PayrollState, PayrollState.New(), state =>
        {
            state.LastPaid[faction] = now;
            return state;
        }, ct).ConfigureAwait(false);

        if (paid.Count > 0)
        {
            await store.UpdateAsync<List<PayrollRun>>(Datasets.Wages, [], history =>
            {
                history.Add(run);
                if (history.Count > HistoryLimit) history.RemoveRange(0, history.Count - HistoryLimit);
                return history;
            }, ct).ConfigureAwait(false);
        }

        return run;
    }

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
