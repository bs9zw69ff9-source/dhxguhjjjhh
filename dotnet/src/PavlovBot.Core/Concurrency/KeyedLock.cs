namespace PavlovBot.Core.Concurrency;

/// <summary>
/// Serialises work per key, across a fixed set of locks.
/// </summary>
/// <remarks>
/// STRIPED RATHER THAN ONE LOCK PER KEY, and the difference is a correctness one rather than
/// a taste one.
///
/// A per-key dictionary of locks has to reclaim entries or it grows for the lifetime of the
/// process. Every reclamation scheme has the same hole. Whatever test decides "nobody needs
/// this lock any more" runs after the last holder releases, and it cannot see a caller that
/// has already READ the lock out of the dictionary but has not yet awaited it. That caller is
/// between two statements, not inside the semaphore, so it is invisible to
/// <c>CurrentCount</c>, to a reference count taken under the lock, and to every other check
/// available at that point. Remove the entry there and the next caller builds a SECOND lock
/// for the same key. Two locks for one key is not a lock: both callers proceed, both
/// read-modify-write, and one of the two writes is lost.
///
/// Striping removes the reclamation step entirely, so there is nothing to get wrong. The lock
/// set is allocated once and never changes.
///
/// THE TRADE IS REAL AND IT IS SMALL. Two different keys that hash to the same stripe
/// serialise when they need not. With the default 256 stripes that is a 1-in-256 chance per
/// concurrent pair, and the cost when it happens is the length of one critical section. In
/// exchange: constant memory, no bookkeeping, and no interleaving that can produce two
/// holders. For a ledger write measured in single-digit milliseconds that is a trade worth
/// making every time.
/// </remarks>
public sealed class KeyedLock
{
    private readonly SemaphoreSlim[] _stripes;
    private readonly StringComparer _comparer;

    /// <param name="stripes">
    /// How many independent locks. Higher means less false sharing between unrelated keys and
    /// more memory; the default is ample for per-player serialisation.
    /// </param>
    /// <param name="comparer">
    /// Must match how the caller considers two keys equal. Passing an ordinal comparer while
    /// the caller treats keys case-insensitively would put "Alice" and "alice" on different
    /// stripes and let both run at once, which is the bug this type exists to prevent.
    /// </param>
    public KeyedLock(int stripes = 256, StringComparer? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripes);

        _comparer = comparer ?? StringComparer.Ordinal;
        _stripes = new SemaphoreSlim[stripes];
        for (var i = 0; i < stripes; i++) _stripes[i] = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Wait for exclusive use of <paramref name="key"/>. Dispose the result to release.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var gate = StripeFor(key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Handle(gate);
    }

    /// <remarks>
    /// Cast through <see cref="uint"/> before the modulus: <see cref="object.GetHashCode"/> is
    /// free to return <see cref="int.MinValue"/>, whose absolute value does not fit in an int,
    /// so the obvious <c>Math.Abs(hash) % length</c> throws on exactly one input in four
    /// billion. That is the kind of defect that survives every test suite and then happens.
    /// </remarks>
    private SemaphoreSlim StripeFor(string key) =>
        _stripes[(int)((uint)_comparer.GetHashCode(key) % (uint)_stripes.Length)];

    /// <summary>Releases the stripe once, however many times it is disposed.</summary>
    /// <remarks>
    /// Interlocked rather than a bool: a double dispose would otherwise release the semaphore
    /// twice and admit two callers to the next critical section, turning a harmless caller
    /// mistake into the exact corruption this type prevents.
    /// </remarks>
    private sealed class Handle(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
