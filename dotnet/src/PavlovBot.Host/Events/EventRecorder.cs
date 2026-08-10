using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Events;

namespace PavlovBot.Host.Events;

/// <summary>
/// The write side of the timeline: buffered, batched, and never on the caller's thread.
/// </summary>
/// <remarks>
/// WHY A QUEUE RATHER THAN A DIRECT WRITE. Events are produced from log ingestion, which runs
/// per line and is the hottest path in the bot. A synchronous insert there puts an fsync
/// between reading one log line and the next, and a full server reconnecting after a map
/// change produces a burst of joins. Buffering turns that burst into one transaction.
///
/// RECORDING NEVER THROWS AND NEVER BLOCKS. A timeline is an observability feature: it must
/// not be able to break the thing it observes. <see cref="Record"/> is synchronous, returns
/// immediately, and swallows a full queue rather than propagating - losing a timeline entry
/// is an acceptable cost, and stalling log ingestion behind a slow disk is not.
///
/// THE QUEUE IS BOUNDED AND DROPS THE OLDEST when full. An unbounded queue in front of a disk
/// is a memory leak with extra steps: if writes cannot keep up, an unbounded one grows until
/// the process dies, which takes the bot down to protect a log. Bounded and lossy is the
/// honest trade, and the drop is counted rather than silent so the loss is visible.
/// </remarks>
public sealed class EventRecorder : IAsyncDisposable
{
    private readonly IEventStore _store;
    private readonly ILogger<EventRecorder> _logger;
    private readonly Channel<ServerEvent> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _drain;
    private long _dropped;

    /// <summary>How many events may wait to be written before the oldest are dropped.</summary>
    /// <remarks>
    /// Two thousand is minutes of a busy multi-server evening, and a few hundred kilobytes.
    /// The queue only fills at all if the disk has stopped keeping up, at which point the
    /// bot has a larger problem than a gap in its timeline.
    /// </remarks>
    public const int QueueCapacity = 2000;

    /// <summary>Most events written in one transaction.</summary>
    public const int BatchSize = 256;

    /// <summary>How long a partial batch waits for company before being written anyway.</summary>
    /// <remarks>
    /// Without this, a quiet server's single event sits in the queue until the next one
    /// arrives - which could be an hour, and a timeline that is an hour behind during a quiet
    /// period is useless exactly when somebody is investigating what happened during it.
    /// </remarks>
    public static TimeSpan FlushInterval { get; } = TimeSpan.FromSeconds(2);

    public EventRecorder(IEventStore store, ILogger<EventRecorder> logger)
    {
        _store = store;
        _logger = logger;

        _queue = Channel.CreateBounded<ServerEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        _drain = Task.Run(() => DrainAsync(_stopping.Token));
    }

    /// <summary>Events dropped because the queue was full. Zero on a healthy bot.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Record one event. Returns immediately and never throws.
    /// </summary>
    /// <remarks>
    /// SYNCHRONOUS ON PURPOSE, despite writing to a channel. Every caller is an event handler
    /// on a hot path that has no use for a Task it would only have to fire and forget - and a
    /// fire-and-forget Task at every call site is an unobserved exception at every call site.
    /// </remarks>
    public void Record(ServerEvent e)
    {
        if (e is null) return;

        if (!_queue.Writer.TryWrite(e))
        {
            // DropOldest means TryWrite effectively always succeeds, so this is the
            // completed-channel case: shutting down. Counted, not logged - logging per event
            // during shutdown is how a clean stop turns into a wall of noise.
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Record several at once - a backfill, or one action that produced many.</summary>
    /// <remarks>
    /// NAMED DIFFERENTLY rather than overloading Record. As an overload the two were ambiguous
    /// for any null-ish argument and, worse, a caller passing a collection could silently bind
    /// to whichever the compiler preferred. A distinct name costs nothing and cannot be got
    /// wrong.
    /// </remarks>
    public void RecordAll(IEnumerable<ServerEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var e in events) Record(e);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        var batch = new List<ServerEvent>(BatchSize);

        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();

                // Take what is there now, up to a batch. Not waiting for a full batch: the
                // common case is one or two events and they should not wait for 254 more.
                while (batch.Count < BatchSize && _queue.Reader.TryRead(out var e)) batch.Add(e);

                if (batch.Count == 0) continue;

                await WriteAsync(batch, ct).ConfigureAwait(false);

                /* A PAUSE BETWEEN BATCHES, so a steady trickle is coalesced rather than
                   producing a transaction per event. It also bounds how hard this can hammer
                   the disk during a burst: at most one commit per interval. */
                try { await Task.Delay(FlushInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down. The final flush below is what matters.
        }

        // FLUSH ON THE WAY OUT. Events accepted from a caller that never reached the disk
        // would make the timeline silently lossy across every restart.
        batch.Clear();
        while (batch.Count < QueueCapacity && _queue.Reader.TryRead(out var e)) batch.Add(e);
        if (batch.Count > 0) await WriteAsync(batch, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteAsync(IReadOnlyCollection<ServerEvent> batch, CancellationToken ct)
    {
        try
        {
            await _store.AppendAsync(batch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* NEVER RETHROWN. This is the only consumer of the queue; letting it die would
               stop the timeline permanently and silently, and a failed write is a lost line
               in a log rather than a lost ban. Logged so a disk that has stopped accepting
               writes is findable. */
            _logger.LogError(ex, "Could not write {Count} timeline event(s)", batch.Count);
        }
    }

    /// <summary>Stop accepting events and flush what is queued.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        /* CANCELLED ONLY AFTER A GRACE PERIOD. Cancelling immediately would abandon queued
           events; waiting forever would hang shutdown behind a wedged disk. Five seconds is
           long enough for any realistic backlog and short enough not to delay a deploy. */
        try
        {
            await _drain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeline writer did not finish within 5s; {Queued} event(s) may be lost", QueueCapacity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Timeline writer stopped with an error");
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _stopping.Dispose();

        if (Dropped > 0)
            _logger.LogWarning("{Count} timeline event(s) were dropped this run - the writer could not keep up", Dropped);
    }
}
