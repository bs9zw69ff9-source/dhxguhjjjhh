using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Events;
using PavlovBot.Host.Events;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The buffered writer in front of the timeline.
/// </summary>
/// <remarks>
/// THE PROPERTY THAT MATTERS MOST IS THAT IT CANNOT BREAK WHAT IT OBSERVES. Events are
/// produced from log ingestion, the hottest path in the bot, and a timeline that can throw
/// from a join handler or block behind a slow disk would be a strictly worse bot than one
/// with no timeline at all.
/// </remarks>
public class EventRecorderTests
{
    /// <summary>Collects what was written, and can be told to fail or to hang.</summary>
    private sealed class RecordingStore : IEventStore
    {
        private readonly Lock _sync = new();
        private readonly List<ServerEvent> _written = [];

        public int Batches { get; private set; }
        public bool Throw { get; set; }

        public IReadOnlyList<ServerEvent> Written { get { lock (_sync) return [.. _written]; } }

        public Task AppendAsync(IReadOnlyCollection<ServerEvent> events, CancellationToken ct = default)
        {
            if (Throw) throw new IOException("disk is full");

            lock (_sync)
            {
                _written.AddRange(events);
                Batches++;
            }
            return Task.CompletedTask;
        }

        public IReadOnlyList<ServerEvent> Query(EventQuery query) => Written;
        public long Count() => Written.Count;
        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static EventRecorder Build(IEventStore store) =>
        new(store, NullLogger<EventRecorder>.Instance);

    private static ServerEvent Event(string kind = "player.join") =>
        new(DateTimeOffset.UtcNow, EventCategory.Player, kind, "Alice");

    /// <summary>Disposing flushes, so nothing accepted is silently lost on shutdown.</summary>
    /// <remarks>
    /// Without the flush the timeline would be lossy across every restart, and a deploy is
    /// exactly when somebody later asks what happened.
    /// </remarks>
    [Fact]
    public async Task EverythingRecordedIsWrittenByTheTimeItIsDisposed()
    {
        var store = new RecordingStore();
        var recorder = Build(store);

        for (var i = 0; i < 20; i++) recorder.Record(Event($"e{i}"));

        await recorder.DisposeAsync();

        Assert.Equal(20, store.Written.Count);
    }

    /// <summary>
    /// A burst becomes a handful of transactions, not one per event.
    /// </summary>
    /// <remarks>
    /// The performance claim, asserted rather than assumed. A full server reconnecting after
    /// a map change produces a burst of joins, and a transaction each means an fsync each.
    ///
    /// WAITS FOR THE DRAIN RATHER THAN DISPOSING, and that distinction is the whole test. The
    /// first version of this disposed and then counted, which passed against a deliberately
    /// un-batched writer: disposal flushes whatever is queued in ONE final batch, so it was
    /// measuring the shutdown path and reporting it as evidence about the running one. Caught
    /// by sabotage, not by review.
    ///
    /// Un-batched, the drain takes one event per flush interval, so 500 events would need
    /// about seventeen minutes and this times out long before it.
    /// </remarks>
    [Fact]
    public async Task ABurstIsBatchedRatherThanWrittenOneAtATime()
    {
        var store = new RecordingStore();
        await using var recorder = Build(store);

        for (var i = 0; i < 500; i++) recorder.Record(Event($"e{i}"));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (store.Written.Count < 500 && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.Equal(500, store.Written.Count);
        Assert.True(store.Batches < 20, $"500 events took {store.Batches} transactions");
    }

    /// <summary>
    /// A failing store does not take the recorder down with it.
    /// </summary>
    /// <remarks>
    /// The drain loop is the only consumer of the queue. If a write exception killed it, the
    /// timeline would stop permanently and silently for the rest of the process - which is
    /// worse than the failed write it started with.
    /// </remarks>
    [Fact]
    public async Task AFailingStoreDoesNotKillTheWriter()
    {
        var store = new RecordingStore { Throw = true };
        var recorder = Build(store);

        recorder.Record(Event());
        await Task.Delay(200);

        store.Throw = false;
        recorder.Record(Event("later"));

        await recorder.DisposeAsync();

        // The first batch was lost, which is the accepted cost. The writer survived it.
        Assert.Contains(store.Written, e => e.Kind == "later");
    }

    /// <summary>Recording never throws, whatever it is handed.</summary>
    /// <remarks>
    /// Every caller is an event handler on a hot path. An exception here would abort the log
    /// line being processed, and the ingest loop's own docs record what that costs: one throw
    /// discarded the rest of the batch, so joins duplicated and leaves vanished.
    /// </remarks>
    [Fact]
    public async Task RecordingNeverThrows()
    {
        var store = new RecordingStore { Throw = true };
        var recorder = Build(store);

        recorder.Record(null!);
        recorder.Record(Event());
        recorder.RecordAll([Event("a"), Event("b")]);

        await recorder.DisposeAsync();
    }

    /// <summary>Recording after disposal is a no-op rather than an exception.</summary>
    /// <remarks>
    /// Shutdown is not instantaneous: a background service can still be finishing a tick when
    /// the recorder has been disposed, and an ObjectDisposedException out of a join handler
    /// during shutdown would be a crash on the way down.
    /// </remarks>
    [Fact]
    public async Task RecordingAfterDisposalIsSafe()
    {
        var store = new RecordingStore();
        var recorder = Build(store);

        await recorder.DisposeAsync();

        recorder.Record(Event("late"));

        Assert.True(recorder.Dropped >= 1);
    }

    /// <summary>
    /// Concurrent producers all land, which is how it is really used.
    /// </summary>
    /// <remarks>
    /// Several log files are tailed at once and each raises events, so the queue has multiple
    /// writers by construction. The channel is configured single-READER, not single-writer,
    /// and this is what would fail if that were changed.
    /// </remarks>
    [Fact]
    public async Task ConcurrentProducersDoNotLoseEvents()
    {
        var store = new RecordingStore();
        var recorder = Build(store);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++) recorder.Record(Event($"w{worker}-{i}"));
        })));

        await recorder.DisposeAsync();

        Assert.Equal(400, store.Written.Count);
        Assert.Equal(0, recorder.Dropped);
    }
}
