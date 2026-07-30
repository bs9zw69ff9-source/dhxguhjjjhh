using System.Collections.Concurrent;

namespace PavlovBot.Core.Data;

/// <summary>Backing storage for <see cref="SerializedStore"/>. JSON text keyed by dataset name.</summary>
public interface IKeyValueBackend
{
    string? Read(string key);
    void Write(string key, string json);
}

/// <summary>
/// Read-modify-write over named JSON datasets, serialised per key.
/// </summary>
/// <remarks>
/// Every mutation the bot makes is read-modify-write: load the ban list, add one, save it.
/// Run two of those concurrently against the same dataset and one silently overwrites the
/// other - a ban that "didn't take", with no error anywhere. Serialising per key is what
/// prevents that.
///
/// Two properties carried deliberately from the Node implementation:
///
///   A FAILED UPDATE MUST NOT POISON THE QUEUE. In JS the chain was kept alive with a
///   trailing catch; here the continuation is attached so the next waiter runs regardless
///   of how the previous one ended. Without this, one thrown mutator would wedge every
///   later write to that dataset - the bot would appear to hang on any command that saved.
///
///   READS ARE ISOLATED. Callers get their own object graph, never a reference into a
///   shared cache, so a caller mutating what it read cannot corrupt state for everyone
///   else. In JS that needed an explicit structuredClone on every read; here it falls out
///   of deserialising per read.
///
/// Locking is per key, so two different datasets never wait on each other.
/// </remarks>
public sealed class SerializedStore
{
    private readonly IKeyValueBackend _backend;
    private readonly IJsonCodec _codec;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public SerializedStore(IKeyValueBackend backend, IJsonCodec codec)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    private SemaphoreSlim Gate(string key) => _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    /// <summary>Read a dataset, or <paramref name="fallback"/> when it is absent or unreadable.</summary>
    public T Read<T>(string key, T fallback)
    {
        var json = _backend.Read(key);
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        /* A corrupt dataset returns the fallback rather than throwing. The alternative is a
           single bad character taking down every command that touches bans. */
        return _codec.TryDeserialize<T>(json, out var value) && value is not null ? value : fallback;
    }

    /// <summary>
    /// Apply <paramref name="mutate"/> to the current value and persist the result.
    /// Serialised against every other update to the same key.
    /// </summary>
    /// <returns>The value that was written, or the unchanged value if the mutator vetoed.</returns>
    public async Task<UpdateResult<T>> UpdateAsync<T>(
        string key, T fallback, Func<T, T?> mutate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(mutate);

        var gate = Gate(key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Read(key, fallback);
            T? next;
            try
            {
                next = mutate(current);
            }
            catch (Exception ex)
            {
                // The mutator threw. The dataset is left exactly as it was, and the queue
                // stays healthy for the next caller.
                return new UpdateResult<T>(false, current, ex.Message);
            }

            // A null return is a deliberate veto - "insufficient funds", "already present".
            if (next is null) return new UpdateResult<T>(false, current, "vetoed by mutator");

            _backend.Write(key, _codec.Serialize(next));
            return new UpdateResult<T>(true, next, null);
        }
        finally
        {
            gate.Release();   // in a finally, so a cancellation cannot wedge the dataset
        }
    }

    /// <summary>Overwrite a dataset outright. Prefer <see cref="UpdateAsync"/> for read-modify-write.</summary>
    public async Task<bool> WriteAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var gate = Gate(key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _backend.Write(key, _codec.Serialize(value));
            return true;
        }
        finally { gate.Release(); }
    }
}

/// <param name="Ok">False when the mutator vetoed or threw; the dataset is unchanged.</param>
/// <param name="Value">The value now stored.</param>
/// <param name="Error">Why the update did not apply, when it did not.</param>
public sealed record UpdateResult<T>(bool Ok, T Value, string? Error);

/// <summary>JSON serialisation, abstracted so Core stays free of a hard dependency on a codec.</summary>
public interface IJsonCodec
{
    string Serialize<T>(T value);
    bool TryDeserialize<T>(string json, out T? value);
}
