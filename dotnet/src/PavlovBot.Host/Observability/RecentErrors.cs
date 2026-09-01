using System.Collections.Concurrent;

namespace PavlovBot.Host.Observability;

/// <param name="Operation">The command or component that failed, e.g. <c>/whitelist</c>.</param>
/// <param name="Trace">The stack trace, unabridged. Trimmed at display time, not here.</param>
public sealed record RecordedError(
    string CorrelationId,
    string Operation,
    ulong UserId,
    DateTimeOffset At,
    string ExceptionType,
    string Message,
    string Trace);

/// <summary>
/// The last few command failures, retrievable by the correlation id the caller was shown.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. A failing command tells the caller "the error has been logged
/// (`ada985e1`)", and until now that id could only be redeemed by someone with a shell on the
/// box, at the moment they still had the log. In practice that meant the id was reported, the
/// trace was not, and the fix was guessed at instead of read - which is how a one-line
/// exception stays open for days.
///
/// IN MEMORY AND BOUNDED, deliberately. This is a debugging aid for the failure somebody is
/// looking at right now, not an audit trail: the log is already the durable record, and
/// putting stack traces in the database would mean deciding how long to keep somebody's file
/// paths. A restart clears it, which is the honest cost of the simpler design - reproduce the
/// failure once and it is here again.
///
/// The trace is kept whole. Truncating on the way in destroys the frame that mattered in
/// exactly the case worth keeping - a deep exception - and the display is where a length
/// limit actually applies.
/// </remarks>
public sealed class RecentErrors
{
    /// <summary>Enough to cover a burst, small enough to hold traces without thought.</summary>
    public const int Capacity = 25;

    private readonly ConcurrentQueue<RecordedError> _errors = new();

    public void Record(string correlationId, string operation, ulong userId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _errors.Enqueue(new RecordedError(
            correlationId,
            operation,
            userId,
            DateTimeOffset.UtcNow,
            exception.GetType().Name,
            exception.Message,
            exception.ToString()));

        // Trim to the cap. A while rather than a single dequeue, because two threads can
        // enqueue between the count check and the removal.
        while (_errors.Count > Capacity && _errors.TryDequeue(out _)) { }
    }

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<RecordedError> All() => [.. _errors.Reverse()];

    /// <summary>
    /// One error by the id its caller was shown, or null when it has aged out.
    /// </summary>
    /// <remarks>
    /// Case-insensitive: the id is read off a screen and typed back in, and rejecting it over
    /// capitalisation would waste the one chance to look it up before it ages out.
    /// </remarks>
    public RecordedError? Find(string? correlationId) =>
        string.IsNullOrWhiteSpace(correlationId)
            ? null
            : _errors.FirstOrDefault(e =>
                string.Equals(e.CorrelationId, correlationId.Trim(), StringComparison.OrdinalIgnoreCase));
}
