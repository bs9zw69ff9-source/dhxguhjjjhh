using System.Collections.Concurrent;

namespace PavlovBot.Core.Economy;

/// <param name="Ok">False when the mutator vetoed or the write failed; the balance is unchanged.</param>
/// <param name="Before">The balance before, always populated.</param>
/// <param name="After">The balance now - equal to <see cref="Before"/> when <see cref="Ok"/> is false.</param>
public readonly record struct BalanceChange(bool Ok, long Before, long After)
{
    public long Delta => After - Before;
}

/// <summary>Reading and writing one player's balance. Implemented over the game's ledger files.</summary>
public interface IBalanceStore
{
    /// <summary>The balance, or null when the player has no ledger. Null is NOT zero.</summary>
    long? Read(string playerId);

    /// <summary>Persist a balance. False when the write failed.</summary>
    bool Write(string playerId, long balance);
}

/// <summary>
/// Serialised credit and debit, one queue per player.
/// </summary>
/// <remarks>
/// The underlying store is a plain read-then-write with no locking, which is a
/// double-spend waiting to happen: two concurrent payouts both read 500, both write 600,
/// and one payout vanishes. Nothing errors, the money is simply gone, and it surfaces days
/// later as "the bot ate my payout".
///
/// Serialising PER PLAYER rather than globally matters at scale - a busy server pays out
/// constantly, and one global lock would turn every payout into a queue behind every other.
///
/// The queue is dropped once it goes idle. Keeping one per player forever is a slow leak
/// that only shows up on a server that has seen a lot of players, which is exactly the one
/// you cannot afford it on.
/// </remarks>
public sealed class Ledger
{
    private readonly IBalanceStore _store;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public Ledger(IBalanceStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Apply <paramref name="mutate"/> to the current balance and persist it.
    /// </summary>
    /// <param name="mutate">
    /// Returns the new balance, or NULL to veto. A veto is how "insufficient funds" is
    /// expressed - it is an ordinary outcome, not an error, and nothing is written.
    /// </param>
    public async Task<BalanceChange> MutateAsync(
        string playerId, Func<long, long?> mutate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerId);
        ArgumentNullException.ThrowIfNull(mutate);

        var gate = _gates.GetOrAdd(playerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // An absent ledger reads as zero HERE, at the point of use, rather than in the
            // store - the store keeps the distinction so a caller that needs to know
            // whether a player has ever had a balance still can.
            var before = _store.Read(playerId) ?? 0;

            long? after;
            try { after = mutate(before); }
            catch (Exception) { return new BalanceChange(false, before, before); }

            if (after is null) return new BalanceChange(false, before, before);

            var ok = _store.Write(playerId, after.Value);
            return new BalanceChange(ok, before, ok ? after.Value : before);
        }
        finally
        {
            gate.Release();
            // Drop the gate once nobody is waiting on it. Checking CurrentCount avoids
            // removing one that a queued caller is about to acquire.
            if (gate.CurrentCount == 1) _gates.TryRemove(new KeyValuePair<string, SemaphoreSlim>(playerId, gate));
        }
    }

    /// <summary>Credit an amount. Negative amounts are a debit and may drive the balance below zero.</summary>
    public Task<BalanceChange> CreditAsync(string playerId, long amount, CancellationToken ct = default) =>
        MutateAsync(playerId, before => before + amount, ct);

    /// <summary>
    /// Debit an amount, refusing to go below zero.
    /// </summary>
    /// <remarks>
    /// The floor is checked INSIDE the mutator, which is the point of the veto: checking
    /// the balance first and then debiting is exactly the race the serialisation exists to
    /// prevent.
    /// </remarks>
    public Task<BalanceChange> DebitAsync(string playerId, long amount, CancellationToken ct = default) =>
        MutateAsync(playerId, before => before >= amount ? before - amount : null, ct);

    /// <summary>Live queue count. Exposed so a leak in the gate map is observable rather than theoretical.</summary>
    public int ActiveQueues => _gates.Count;
}
