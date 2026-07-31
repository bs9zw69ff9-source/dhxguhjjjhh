using System.Collections.Concurrent;
using System.Text.Json;
using PavlovBot.Core.Data;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>In-memory backend so the store's behaviour is tested without a database.</summary>
internal sealed class MemoryBackend : IKeyValueBackend
{
    private readonly ConcurrentDictionary<string, string> _data = new(StringComparer.Ordinal);
    public int Writes;

    public string? Read(string key) => _data.TryGetValue(key, out var v) ? v : null;
    public void Write(string key, string json) { Interlocked.Increment(ref Writes); _data[key] = json; }
    public void Seed(string key, string json) => _data[key] = json;
}

/* NO local codec stub here. There used to be one, and because it sat in the test
   assembly's own namespace it SHADOWED the production PavlovBot.Host.Storage codec at
   every call site in this project - so tests that looked like they exercised the real
   serialiser were exercising a three-line fake. That hid the Node interop bug entirely:
   the fake had no converters and no naming policy, so it round-tripped C# to C# happily
   while the real one could not read a single Node record. Use the real type. */

/// <summary>
/// Ported from test/database.test.js, plus the concurrency properties the JS suite could
/// only assert indirectly.
/// </summary>
public class SerializedStoreTests
{
    private static SerializedStore NewStore(out MemoryBackend backend)
    {
        backend = new MemoryBackend();
        return new SerializedStore(backend, new SystemTextJsonCodec());
    }

    [Fact]
    public void MissingDatasetReturnsTheFallback()
    {
        var store = NewStore(out _);
        Assert.Empty(store.Read("bans", new List<string>()));
    }

    [Fact]
    public void CorruptJsonReturnsTheFallbackRatherThanThrowing()
    {
        // One bad character must not take down every command that touches bans.
        var store = NewStore(out var backend);
        backend.Seed("bans", "{ this is not json");
        Assert.Empty(store.Read("bans", new List<string>()));
    }

    [Fact]
    public async Task UpdateReadsModifiesAndPersists()
    {
        var store = NewStore(out _);
        var result = await store.UpdateAsync("bans", new List<string>(), list => { list.Add("Griefer"); return list; });

        Assert.True(result.Ok);
        Assert.Equal(["Griefer"], store.Read("bans", new List<string>()));
    }

    [Fact]
    public async Task ReadsAreIsolated_MutatingWhatYouReadCannotCorruptTheStore()
    {
        /* In JS this needed an explicit structuredClone on every read; here it falls out of
           deserialising per read. The property is what matters, not the mechanism. */
        var store = NewStore(out _);
        await store.UpdateAsync("bans", new List<string>(), l => { l.Add("Griefer"); return l; });

        var mine = store.Read("bans", new List<string>());
        mine.Add("NotReallyBanned");

        Assert.Single(store.Read("bans", new List<string>()));
    }

    [Fact]
    public async Task ConcurrentUpdatesAreSerialised_NoLostWrites()
    {
        /* The bug this prevents: two bans land at once, the second overwrites the first,
           and the missing ban surfaces days later as "we banned them, they're still on". */
        var store = NewStore(out _);
        const int writers = 40;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(i =>
            store.UpdateAsync("bans", new List<string>(), list => { list.Add("player" + i); return list; })));

        Assert.Equal(writers, store.Read("bans", new List<string>()).Count);
    }

    [Fact]
    public async Task AThrowingMutatorDoesNotPoisonTheQueue()
    {
        /* The critical resilience property. Without it one bad mutator wedges every later
           write to that dataset, and the bot appears to hang on any command that saves. */
        var store = NewStore(out _);
        await store.UpdateAsync("bans", new List<string>(), l => { l.Add("first"); return l; });

        var failed = await store.UpdateAsync<List<string>>("bans", [], _ => throw new InvalidOperationException("boom"));
        Assert.False(failed.Ok);
        Assert.Contains("boom", failed.Error, StringComparison.Ordinal);

        var after = await store.UpdateAsync("bans", new List<string>(), l => { l.Add("second"); return l; });
        Assert.True(after.Ok);
        Assert.Equal(["first", "second"], store.Read("bans", new List<string>()));
    }

    [Fact]
    public async Task AVetoingMutatorLeavesTheDatasetUntouchedAndWritesNothing()
    {
        // Returning null is how "insufficient funds" is expressed - it is not an error.
        var store = NewStore(out var backend);
        await store.UpdateAsync("balance", 100, _ => 100);
        var writesBefore = backend.Writes;

        var vetoed = await store.UpdateAsync<int?>("balance", 100, _ => null);

        Assert.False(vetoed.Ok);
        Assert.Equal(writesBefore, backend.Writes);
        Assert.Equal(100, store.Read("balance", 0));
    }

    [Fact]
    public async Task DifferentDatasetsDoNotBlockEachOther()
    {
        /* Locking is per key. Note the mutator runs SYNCHRONOUSLY inside the gate, so the
           blocking one has to be started on its own thread - starting it inline would block
           this test before the second update was ever created. That is a property of the
           API, not a defect: a sync mutator holds the caller, which is exactly what makes
           read-modify-write easy to reason about. */
        var store = NewStore(out _);
        var released = new ManualResetEventSlim(false);

        var slow = Task.Run(() => store.UpdateAsync("bans", new List<string>(), l => { released.Wait(); return l; }));
        var fast = Task.Run(() => store.UpdateAsync("playtime", new List<string>(), l => { l.Add("x"); return l; }));

        var done = await Task.WhenAny(fast, Task.Delay(3000));
        released.Set();
        await slow;

        Assert.Same(fast, done);   // it did not wait on the other dataset
        Assert.Single(store.Read("playtime", new List<string>()));
    }
}

/// <summary>
/// The roster write guard. These assertions encode the reason the limit exists, not just
/// the number.
/// </summary>
public class RosterWriteGuardTests
{
    private static List<string> Roster(int n) => [.. Enumerable.Range(0, n).Select(i => "player" + i)];

    [Fact]
    public void ASmallChangeIsAllowed()
    {
        // Add one, remove one - the shape of every real operation.
        var current = Roster(20);
        var proposed = new List<string>(current.Take(19)) { "newMember" };

        var verdict = RosterWriteGuard.Evaluate(current, proposed);

        Assert.True(verdict.IsAllowed);
        Assert.Equal(1, verdict.Dropped);
    }

    [Fact]
    public void AWipeIsRefused()
    {
        /* An empty roster file is VALID to Pavlov - it reports no error and simply gives
           nobody their spawn. A parse bug producing an empty list would silently strip a
           faction mid-round, so this is refused, not warned about. */
        var verdict = RosterWriteGuard.Evaluate(Roster(20), []);

        Assert.False(verdict.IsAllowed);
        Assert.Equal(RosterWriteDecision.RefusedBulkDrop, verdict.Decision);
        Assert.Equal(20, verdict.Dropped);
        Assert.Contains("suspected corruption", verdict.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLimitBoundaryIsExact()
    {
        var current = Roster(20);
        Assert.True(RosterWriteGuard.Evaluate(current, [.. current.Take(15)]).IsAllowed);      // 5 dropped
        Assert.False(RosterWriteGuard.Evaluate(current, [.. current.Take(14)]).IsAllowed);     // 6 dropped
    }

    [Fact]
    public void AnUnreadableFileIsRefused_NotTreatedAsEmpty()
    {
        /* The subtle one. "Cannot read" and "is empty" look identical if you are careless,
           and conflating them turns a transient permission error into a wipe. */
        var verdict = RosterWriteGuard.Evaluate(current: null, proposed: ["someone"], fileExists: true);

        Assert.False(verdict.IsAllowed);
        Assert.Equal(RosterWriteDecision.RefusedUnreadable, verdict.Decision);
    }

    [Fact]
    public void AMissingFileIsAllowed_CreatingARosterIsLegitimate()
    {
        var verdict = RosterWriteGuard.Evaluate(current: null, proposed: ["someone"], fileExists: false);
        Assert.True(verdict.IsAllowed);
    }

    [Fact]
    public void AnEmptyExistingRosterCanBeFilled()
    {
        Assert.True(RosterWriteGuard.Evaluate([], Roster(30)).IsAllowed);
    }

    [Fact]
    public void ADeliberateWipeIsAllowedWhenAskedFor()
    {
        // /whitelist wipe means it. The guard blocks accidents, not intent.
        Assert.True(RosterWriteGuard.Evaluate(Roster(50), [], allowBulk: true).IsAllowed);
    }

    [Fact]
    public void ANullPayloadIsRefused()
    {
        var verdict = RosterWriteGuard.Evaluate(Roster(10), proposed: null);
        Assert.Equal(RosterWriteDecision.RefusedInvalidPayload, verdict.Decision);
    }

    [Fact]
    public void ComparisonIsCaseInsensitive()
    {
        // Pavlov names vary in case between the log and the roster file; a case flip must
        // not read as 20 removals and 20 additions.
        var current = Roster(20);
        var proposed = current.Select(n => n.ToUpperInvariant()).ToList();

        var verdict = RosterWriteGuard.Evaluate(current, proposed);

        Assert.True(verdict.IsAllowed);
        Assert.Equal(0, verdict.Dropped);
    }

    [Fact]
    public void SplitLinesNormalisesTheWayTheGameReadsIt()
    {
        var lines = RosterWriteGuard.SplitLines("alice\r\n  bob  \n\n\ncarol\n");
        Assert.Equal(["alice", "bob", "carol"], lines);
    }
}
