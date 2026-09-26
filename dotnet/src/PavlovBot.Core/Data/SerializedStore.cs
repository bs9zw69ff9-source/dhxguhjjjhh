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
    private readonly ConcurrentDictionary<string, byte> _unreadable = new(StringComparer.Ordinal);
    private readonly Action<string>? _onUnreadable;

    /// <param name="onUnreadable">
    /// Told the name of a dataset that holds data but will not parse - once, until it parses
    /// again. The host logs it at Error: a dataset in that state can no longer be changed.
    /// </param>
    public SerializedStore(IKeyValueBackend backend, IJsonCodec codec, Action<string>? onUnreadable = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _onUnreadable = onUnreadable;
    }

    /// <summary>Datasets currently holding data that does not parse.</summary>
    public IReadOnlyCollection<string> Unreadable => [.. _unreadable.Keys];

    /// <summary>
    /// Parse a dataset. Absent (or a bare JSON null) is NOT the same as unreadable, and the
    /// difference decides whether a write is safe.
    /// </summary>
    private bool TryLoad<T>(string key, out T? value, out bool unreadable)
    {
        value = default;
        unreadable = false;

        var json = _backend.Read(key);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return false;

        if (_codec.TryDeserialize<T>(json, out var parsed) && parsed is not null)
        {
            _unreadable.TryRemove(key, out _);
            value = parsed;
            return true;
        }

        unreadable = true;
        if (_unreadable.TryAdd(key, 0)) _onUnreadable?.Invoke(key);
        return false;
    }

    private SemaphoreSlim Gate(string key) => _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// The stored text for a key, unparsed.
    /// </summary>
    /// <remarks>
    /// FOR TELLING "ABSENT" FROM "UNREADABLE", and nothing else. <see cref="Read{T}"/> maps
    /// both to the fallback on purpose - a corrupt row must not take down every command that
    /// touches it - but that makes a row full of unparseable text look exactly like an empty
    /// one to anybody reporting what is stored. This is the only way to say which.
    /// </remarks>
    public string? ReadRaw(string key) => _backend.Read(key);

    /// <summary>Read a dataset, or <paramref name="fallback"/> when it is absent or unreadable.</summary>
    public T Read<T>(string key, T fallback)
    {
        /* A corrupt dataset returns the fallback rather than throwing. The alternative is a
           single bad character taking down every command that touches bans. It is REPORTED,
           though, and UpdateAsync will not write over it - see there. */
        return TryLoad<T>(key, out var value, out _) ? value! : fallback;
    }

    /// <summary>
    /// Read a string-keyed dictionary, keeping the comparer the caller asked for.
    /// </summary>
    /// <remarks>
    /// A SILENT BUG AT EVERY CALL SITE THAT READS ONE. They all look like this:
    ///
    ///     store.Read(Datasets.AutobanExempt,
    ///         new Dictionary&lt;string, DateTimeOffset&gt;(StringComparer.OrdinalIgnoreCase))
    ///
    /// and that comparer only ever applied when the row was ABSENT. JSON carries no
    /// comparer, so the moment the row existed the deserializer handed back an ORDINAL
    /// dictionary and every lookup became case-sensitive - no error, no log line, and
    /// behaviour that changed the first time somebody saved something.
    ///
    /// It matters most where it shows least. IsExempt is one of the four protections between
    /// a player and an automatic permanent ban and it looks an in-game name up in one of
    /// these. An unban tombstone is keyed on what staff typed and matched against what the
    /// ban file spells. Neither fails loudly; both just stop protecting.
    ///
    /// A SEPARATE NAME rather than an overload of Read: overload resolution between two
    /// generic methods is ambiguous here, not more-specific, so the compiler rejects it. It
    /// therefore has to be called deliberately, and it is - at every site on the enforcement
    /// path. The rest of the codebase still reads dictionaries the old way; those hold
    /// warnings, arrests, playtime and the like, where a case mismatch is a wrong number
    /// rather than a wrongful ban.
    /// </remarks>
    public Dictionary<string, TValue> ReadMap<TValue>(string key, Dictionary<string, TValue> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        return TryLoad<Dictionary<string, TValue>>(key, out var value, out _)
            ? new Dictionary<string, TValue>(value!, fallback.Comparer)
            : fallback;
    }


    /// <summary>
    /// Apply <paramref name="mutate"/> to the current value and persist the result.
    /// Serialised against every other update to the same key.
    /// </summary>
    /// <returns>The value that was written, or the unchanged value if the mutator vetoed.</returns>
    /// <summary>
    /// Read-modify-write a string-keyed dictionary, keeping the caller's comparer.
    /// </summary>
    /// <remarks>
    /// The write half of the same bug. Without this the mutator is handed an ORDINAL
    /// dictionary, so <c>map[name] = value</c> adds a second entry differing only in case
    /// rather than replacing the first, and <c>Remove</c> misses.
    /// </remarks>
    public Task<UpdateResult<Dictionary<string, TValue>>> UpdateMapAsync<TValue>(
        string key,
        Dictionary<string, TValue> fallback,
        Func<Dictionary<string, TValue>, Dictionary<string, TValue>?> mutate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(mutate);

        return UpdateAsync<Dictionary<string, TValue>>(key, fallback,
            current => mutate(new Dictionary<string, TValue>(current, fallback.Comparer)), ct);
    }

    public async Task<UpdateResult<T>> UpdateAsync<T>(
        string key, T fallback, Func<T, T?> mutate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(mutate);

        var gate = Gate(key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            /* AN UNREADABLE DATASET IS NEVER WRITTEN OVER. This used to read the fallback -
               empty - apply the change to it, and save the result: one bad character, or a
               shape one call site reads differently from another, and the next ban or join
               replaced the whole dataset with a single entry. Refusing loses one change and
               says so; overwriting lost everything, silently. */
            if (!TryLoad<T>(key, out var loaded, out var unreadable) && unreadable)
            {
                return new UpdateResult<T>(false, fallback,
                    $"dataset \"{key}\" holds data that cannot be read, so it was not changed - fix or restore it");
            }

            var current = loaded ?? fallback;
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
