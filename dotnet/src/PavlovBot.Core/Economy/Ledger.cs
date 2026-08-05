using PavlovBot.Core.Concurrency;

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
/// THE SERIALISATION IS STRIPED, not a lock per player, and that is load-bearing. This class
/// used to keep a <c>ConcurrentDictionary</c> of one semaphore per player and drop each entry
/// when it looked idle, to avoid growing forever. The idle test could not see a caller that
/// had read the semaphore out of the dictionary but had not yet awaited it, so the entry was
/// removed underneath that caller and the next arrival built a second semaphore for the same
/// player. Two semaphores, two concurrent writers, one lost payout: precisely the double-spend
/// described above, reintroduced by the code written to prevent it.
/// <see cref="KeyedLock"/> has no reclamation step and therefore no such window.
/// </remarks>
public sealed class Ledger
{
    private readonly IBalanceStore _store;

    /// <remarks>
    /// OrdinalIgnoreCase to match how the rest of the bot compares player ids. On an ordinal
    /// comparer "Alice" and "alice" would land on different stripes and mutate one ledger
    /// concurrently, which is the whole failure this serialisation exists to prevent.
    /// </remarks>
    private readonly KeyedLock _locks = new(comparer: StringComparer.OrdinalIgnoreCase);

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

        using var _ = await _locks.AcquireAsync(playerId, ct).ConfigureAwait(false);

        // An absent ledger reads as zero HERE, at the point of use, rather than in the
        // store - the store keeps the distinction so a caller that needs to know
        // whether a player has ever had a balance still can.
        var before = _store.Read(playerId) ?? 0;

        /* A THROWING MUTATOR IS A BUG IN THE CALLER, AND IT PROPAGATES. This used to be
           wrapped in `catch (Exception) { return new BalanceChange(false, ...); }`, which
           made every such bug indistinguishable from "insufficient funds" - the ordinary,
           expected, entirely uninteresting outcome. No log, no metric, no stack trace, and a
           veto is common enough that nobody would ever look twice at one.

           Core has no logger by design (it is dependency-free and AOT-safe), so swallowing
           here cannot be made observable here. The host CAN see it: DiscordGateway counts
           command_errors_total with the exception, and Payroll logs its own failures. Letting
           it out is what puts it in front of someone. */
        var after = mutate(before);

        if (after is null) return new BalanceChange(false, before, before);

        var ok = _store.Write(playerId, after.Value);
        return new BalanceChange(ok, before, ok ? after.Value : before);
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
}
