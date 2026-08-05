using PavlovBot.Core.Concurrency;
using PavlovBot.Core.Economy;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The ledger under concurrent mutation of ONE player.
/// </summary>
/// <remarks>
/// The suite had good coverage of what a payout means and none of what two payouts at once
/// mean, which is how a lost-update bug lived in the money path through every green run.
///
/// The store here deliberately WIDENS the race rather than reproducing production timing.
/// A read-modify-write only loses an update when a second writer interleaves between the
/// read and the write, and against an in-memory dictionary that window is a few nanoseconds
/// wide - narrow enough that a broken lock still passes most of the time, which is worse than
/// no test at all. The yield inside <see cref="WideningStore.Read"/> holds the window open so
/// the assertion means something on every run.
/// </remarks>
public class LedgerConcurrencyTests
{
    /// <summary>A balance store whose read-to-write window is wide enough to lose updates.</summary>
    private sealed class WideningStore : IBalanceStore
    {
        private readonly Dictionary<string, long> _balances = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _sync = new();

        public long? Read(string playerId)
        {
            long? value;
            lock (_sync) value = _balances.TryGetValue(playerId, out var v) ? v : null;

            // The window. Without it a broken lock passes by luck.
            Thread.Sleep(1);
            return value;
        }

        public bool Write(string playerId, long balance)
        {
            lock (_sync) _balances[playerId] = balance;
            return true;
        }

        public long Current(string playerId)
        {
            lock (_sync) return _balances.TryGetValue(playerId, out var v) ? v : 0;
        }
    }

    [Fact]
    public async Task ConcurrentCreditsToOnePlayerAreAllApplied()
    {
        /* THE ONE THAT MATTERS. Fifty concurrent +1 credits must leave exactly 50. Any
           interleaving that lets two callers hold the same player at once loses at least one
           and the total comes out short - which is the "bot ate my payout" report, arriving
           days later and impossible to prove after the fact.

           Balances are mutated from four independent places (payroll accrual, arrest fines,
           the money-log poller, /pay), so concurrent mutation of one player is routine rather
           than exotic. */
        var store = new WideningStore();
        var ledger = new Ledger(store);

        const int writers = 50;
        await Task.WhenAll(Enumerable.Range(0, writers)
            .Select(_ => Task.Run(() => ledger.CreditAsync("Alice", 1))));

        Assert.Equal(writers, store.Current("Alice"));
    }

    [Fact]
    public async Task PlayerIdsThatDifferOnlyByCaseAreTheSamePlayer()
    {
        /* The store compares ids OrdinalIgnoreCase, so "Alice" and "alice" are one balance.
           If the lock disagreed with the store about that, the two spellings would serialise
           independently while writing the same entry - the same lost update, reachable just by
           typing a name differently in two commands. */
        var store = new WideningStore();
        var ledger = new Ledger(store);

        await Task.WhenAll(
            Task.Run(() => ledger.CreditAsync("Alice", 1)),
            Task.Run(() => ledger.CreditAsync("alice", 1)),
            Task.Run(() => ledger.CreditAsync("ALICE", 1)));

        Assert.Equal(3, store.Current("Alice"));
    }

    [Fact]
    public async Task DifferentPlayersDoNotSerialiseBehindEachOther()
    {
        /* The reason this is striped per key rather than one global lock. Fifty different
           players each taking a 1ms store round trip must not run end to end; if they did,
           a busy server's payouts would queue behind one another. Generous bound: the point
           is to catch a global lock, not to measure the scheduler. */
        var store = new WideningStore();
        var ledger = new Ledger(store);

        var started = DateTimeOffset.UtcNow;
        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => ledger.CreditAsync($"player{i}", 1))));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(500),
            "50 distinct players serialised, so the lock is global rather than per key");
    }

    [Fact]
    public async Task AThrowingMutatorPropagatesRatherThanLookingLikeAVeto()
    {
        /* A veto (null) and a bug (throw) used to be reported identically, as an ordinary
           failed change. A veto is expected and common; a throw is a defect. Reporting the
           second as the first means nobody ever finds it. */
        var ledger = new Ledger(new WideningStore());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.MutateAsync("Alice", _ => throw new InvalidOperationException("bug")));
    }

    [Fact]
    public async Task TheLockIsReleasedAfterAMutatorThrows()
    {
        // A stripe left held would deadlock every later caller that hashes to it, turning one
        // bad mutator into a permanently wedged ledger.
        var store = new WideningStore();
        var ledger = new Ledger(store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.MutateAsync("Alice", _ => throw new InvalidOperationException("bug")));

        var after = await ledger.CreditAsync("Alice", 5);

        Assert.True(after.Ok);
        Assert.Equal(5, store.Current("Alice"));
    }

    // ---- why the old scheme was replaced ----

    /// <summary>
    /// The reclaiming lock map admits two holders for one key. Shown deterministically.
    /// </summary>
    /// <remarks>
    /// THIS TEST DOES NOT GO THROUGH <see cref="Ledger"/>, and that is the point worth
    /// recording rather than hiding.
    ///
    /// The window in the old code was between reading the semaphore out of the dictionary and
    /// awaiting it - two adjacent statements with no await between them, so it is nanoseconds
    /// wide. A black-box test against Ledger cannot widen it, and an attempt to hit it by
    /// hammering CreditAsync passes against the broken implementation: the race is real but
    /// its probability per operation is very low.
    ///
    /// So the flaw is demonstrated where it lives, in the ALGORITHM, by driving the same
    /// sequence by hand. If this ordering is possible - and nothing prevents it - then the
    /// scheme admits two writers to one balance. It is kept as a regression test against
    /// anyone reintroducing per-key reclamation as a "fix" for the fixed lock set.
    /// </remarks>
    [Fact]
    public async Task TheOldReclaimingLockMapAdmitsTwoHoldersForOneKey()
    {
        var gates = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        SemaphoreSlim Get() => gates.GetOrAdd("alice", _ => new SemaphoreSlim(1, 1));

        // A takes the lock.
        var a = Get();
        await a.WaitAsync();

        // B reads the SAME lock, and is descheduled before awaiting it. This is the window:
        // B holds a reference, is not inside the semaphore, and is invisible to CurrentCount.
        var b = Get();

        // A finishes and reclaims, exactly as the old finally block did.
        a.Release();
        Assert.Equal(1, a.CurrentCount);                       // "nobody is waiting"
        gates.TryRemove(new KeyValuePair<string, SemaphoreSlim>("alice", a));

        // B resumes and acquires the lock it captured.
        await b.WaitAsync();

        // C arrives, finds no entry, and builds a second lock for the same key.
        var c = Get();
        Assert.NotSame(b, c);

        // Both are now inside the critical section for one player.
        Assert.True(c.Wait(TimeSpan.Zero),
            "C should not have been able to enter while B holds the key - two locks, one key");
    }

    // ---- the primitive itself ----

    [Fact]
    public async Task KeyedLockAdmitsOneHolderPerKey()
    {
        var locks = new KeyedLock();
        var concurrent = 0;
        var peak = 0;
        var sync = new Lock();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            using var _handle = await locks.AcquireAsync("k");
            var now = Interlocked.Increment(ref concurrent);
            lock (sync) peak = Math.Max(peak, now);
            await Task.Delay(2);
            Interlocked.Decrement(ref concurrent);
        })));

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task DisposingAKeyedLockHandleTwiceDoesNotAdmitASecondHolder()
    {
        // A double release would raise the semaphore's count above one and let two callers
        // into the next critical section, which is the corruption this type exists to stop.
        var locks = new KeyedLock();

        var handle = await locks.AcquireAsync("k");
        handle.Dispose();
        handle.Dispose();

        using var first = await locks.AcquireAsync("k");
        var second = locks.AcquireAsync("k", new CancellationTokenSource(50).Token).AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }
}
