using PavlovBot.Core.Data;
using PavlovBot.Core.Text;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <summary>
/// Every moderation action, recorded.
/// </summary>
/// <remarks>
/// Two separate audiences, and the split matters:
///
///   THE APPLICATION LOG is for whoever is debugging the bot. It rotates, it is on the
///   host, and nobody reads it during an argument about a ban.
///
///   THIS is the record staff and players are shown - what <c>/staffactivity</c> reads and
///   what answers "who banned them and when". It has to survive a restart, which the
///   application log does not.
///
/// BOUNDED. A busy server produces thousands of actions a month and the whole dataset is
/// deserialised on every read, so it is trimmed to a ceiling. Losing the oldest entries is
/// the right trade against a file that eventually takes a second to parse - and the ones
/// anybody asks about are recent.
/// </remarks>
public sealed class AuditLog(SerializedStore store)
{
    /// <summary>Roughly a year of a busy server, and small enough to parse instantly.</summary>
    public const int MaxEntries = 5000;

    public Task RecordAsync(string action, string moderator, string player, string? reason = null, CancellationToken ct = default) =>
        store.UpdateAsync<List<ModAction>>(Datasets.ModLog, [], log =>
        {
            log.Add(new ModAction(action, moderator, player, Sanitize.Message(reason ?? ""), DateTimeOffset.UtcNow));

            // Trim from the FRONT - the oldest go first.
            if (log.Count > MaxEntries) log.RemoveRange(0, log.Count - MaxEntries);
            return log;
        }, ct);

    public IReadOnlyList<ModAction> All() => store.Read<List<ModAction>>(Datasets.ModLog, []);

    /// <summary>
    /// Newest first, with ties broken by insertion order rather than left to chance.
    /// </summary>
    /// <remarks>
    /// Timestamps are stored as whole milliseconds (see <c>EpochMillisecondsConverter</c> -
    /// the format the Node bot reads), so two actions taken in the same millisecond carry
    /// the SAME <c>At</c>. <c>OrderByDescending</c> is a stable sort, so it leaves tied
    /// entries in source order - which is append order, oldest first - and a staffer who
    /// kicks two players in one burst sees them listed backwards.
    ///
    /// Reversing before the sort is what fixes it: the log is append-only and trimmed from
    /// the front, so its order IS chronological order. Reversed, the stable sort keeps the
    /// later-recorded action ahead of the earlier one whenever their timestamps tie.
    /// </remarks>
    private static IEnumerable<ModAction> NewestFirst(IEnumerable<ModAction> actions) =>
        actions.Reverse().OrderByDescending(a => a.At);

    /// <summary>One staffer's actions, most recent first.</summary>
    public IReadOnlyList<ModAction> By(string moderator, DateTimeOffset? since = null) =>
        NewestFirst(All())
            .Where(a => string.Equals(a.Moderator, moderator, StringComparison.OrdinalIgnoreCase))
            .Where(a => since is null || a.At >= since)
            .ToList();

    /// <summary>Everything done TO one player, most recent first.</summary>
    public IReadOnlyList<ModAction> Against(string player) =>
        NewestFirst(All())
            .Where(a => string.Equals(a.Player, player, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
