namespace PavlovBot.Core.Data;

/// <summary>What a proposed roster write should do.</summary>
public enum RosterWriteDecision
{
    /// <summary>Safe to write.</summary>
    Allow,

    /// <summary>Refused: the write would delete implausibly many entries.</summary>
    RefusedBulkDrop,

    /// <summary>Refused: the current contents could not be read, so the change cannot be judged.</summary>
    RefusedUnreadable,

    /// <summary>Refused: the payload was not a list of lines.</summary>
    RefusedInvalidPayload,
}

/// <param name="Decision">Whether to proceed.</param>
/// <param name="Dropped">How many existing entries the write would remove.</param>
/// <param name="Existing">How many entries the file currently holds.</param>
/// <param name="Explanation">Log-ready reason, populated on every refusal.</param>
public sealed record RosterWriteVerdict(
    RosterWriteDecision Decision,
    int Dropped,
    int Existing,
    string? Explanation = null)
{
    public bool IsAllowed => Decision == RosterWriteDecision.Allow;
}

/// <summary>
/// Decides whether a roster write is plausible before it touches a file the game server
/// is reading live.
/// </summary>
/// <remarks>
/// This is the most valuable safety rail in the bot, and it is worth being explicit about
/// why. Pavlov reads faction rosters as plain text. An empty file is perfectly VALID - the
/// game reports no error, it simply gives nobody their spawn. So a parsing bug that
/// produced an empty list would silently strip a whole faction mid-round, and the only
/// signal would be players asking why they had lost their loadout.
///
/// The insight is that the check belongs on the SIZE OF THE CHANGE, not on the shape of
/// the data. A legitimate operation - add a member, promote someone, transfer a rank -
/// changes one or two lines. A write removing twenty is not a big operation; it is a bug
/// that has already happened.
///
/// It refuses rather than warns. A warning in a log nobody reads is not a safety mechanism.
///
/// Pure by design: the caller does the file I/O and passes the current contents in, so
/// this is fully testable without a filesystem.
/// </remarks>
public static class RosterWriteGuard
{
    /// <summary>
    /// Maximum entries a single write may remove before it is treated as corruption.
    /// Five is comfortably above any real operation and far below a wipe.
    /// </summary>
    public const int BulkDropLimit = 5;

    /// <summary>
    /// Judge a proposed write.
    /// </summary>
    /// <param name="current">
    /// Current file contents, already split into trimmed non-empty lines.
    /// <c>null</c> means the file could not be read - which is NOT the same as an empty
    /// file and is treated far more cautiously.
    /// </param>
    /// <param name="proposed">The lines about to be written.</param>
    /// <param name="fileExists">
    /// False when the file is simply absent. Creating a roster from nothing is legitimate;
    /// failing to read one that exists is not.
    /// </param>
    /// <param name="allowBulk">Set by an operation that intends a large removal, e.g. a deliberate wipe.</param>
    public static RosterWriteVerdict Evaluate(
        IReadOnlyList<string>? current,
        IReadOnlyList<string>? proposed,
        bool fileExists = true,
        bool allowBulk = false)
    {
        if (proposed is null)
            return new RosterWriteVerdict(RosterWriteDecision.RefusedInvalidPayload, 0, 0,
                "payload is not a list of lines");

        if (allowBulk)
            return new RosterWriteVerdict(RosterWriteDecision.Allow, 0, current?.Count ?? 0);

        // A file that does not exist yet has nothing to lose - creating it is always safe.
        if (!fileExists)
            return new RosterWriteVerdict(RosterWriteDecision.Allow, 0, 0);

        /* Unreadable is the dangerous case: we cannot tell a legitimate change from a wipe,
           so we refuse rather than gamble with a populated roster. */
        if (current is null)
            return new RosterWriteVerdict(RosterWriteDecision.RefusedUnreadable, 0, 0,
                "cannot read current contents - aborting to protect the roster");

        if (current.Count == 0)
            return new RosterWriteVerdict(RosterWriteDecision.Allow, 0, 0);

        var keep = new HashSet<string>(proposed, StringComparer.OrdinalIgnoreCase);
        var dropped = current.Count(line => !keep.Contains(line));

        return dropped > BulkDropLimit
            ? new RosterWriteVerdict(RosterWriteDecision.RefusedBulkDrop, dropped, current.Count,
                $"would remove {dropped} of {current.Count} entries (limit {BulkDropLimit}) - suspected corruption, roster left untouched")
            : new RosterWriteVerdict(RosterWriteDecision.Allow, dropped, current.Count);
    }

    /// <summary>Normalise raw file text the way the game and the bot both read it.</summary>
    public static List<string> SplitLines(string? raw) =>
        raw is null
            ? []
            : [.. raw.Split('\n').Select(l => l.Trim('\r', ' ', '\t')).Where(l => l.Length > 0)];
}
