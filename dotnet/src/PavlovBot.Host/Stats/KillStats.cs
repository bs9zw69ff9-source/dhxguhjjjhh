using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Logs;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Stats;

/// <summary>One player's kill record.</summary>
/// <param name="Suicides">
/// Counted separately and NOT as kills. A self-kill is one line in Pavlov.log with the same
/// name on both sides, and folding it into kills would let somebody farm a ratio off a
/// grenade.
/// </param>
public sealed record PlayerKills(
    string Player,
    long Kills,
    long Deaths,
    long Suicides,
    DateTimeOffset? LastAt)
{
    /// <summary>
    /// Kills per death, with no death treated as one.
    /// </summary>
    /// <remarks>
    /// DIVIDING BY ZERO IS THE WHOLE PROBLEM with a K/D, and every wrong answer to it is
    /// visible: NaN, infinity, or a silent zero that reads as "terrible" for somebody who has
    /// never died. Treating no deaths as one death is what every shooter does, so a player
    /// with three kills and no deaths reads 3.00 rather than something that needs explaining.
    /// </remarks>
    public double Ratio => Kills / (double)Math.Max(1, Deaths);

    public static PlayerKills Empty(string player) => new(player, 0, 0, 0, null);
}

/// <summary>
/// Kills and deaths, counted off Pavlov.log.
/// </summary>
/// <remarks>
/// BUFFERED IN MEMORY AND FLUSHED ON A TIMER, which is the only part of this worth arguing
/// about. Every kill on a full server is a line, and <see cref="SerializedStore"/> rewrites
/// the WHOLE dataset per update - so a write per kill is a few hundred full-document
/// serialisations a minute at peak. That is the exact shape of the problem the money log had
/// before it was deleted.
///
/// The cost of buffering is that a crash loses up to one flush interval of kills. For a
/// scoreboard that is the right trade; for anything a ban or a payment depends on it would
/// not be, which is why nothing else in this bot does it.
///
/// KEYED BY DISPLAY NAME, because that is all a kill line carries. Pavlov's kill output names
/// the killer and the killed and nothing else - no account id, no platform id - so a player
/// who renames starts a fresh record. The alternative is inventing a mapping from a name to
/// an account at kill time, which is exactly the guess that got the kill feed showing digits
/// instead of names.
/// </remarks>
public sealed class KillStats(SerializedStore store, ILogger<KillStats> logger)
{
    /// <summary>Deltas not yet written. Merged into the stored totals on the next flush.</summary>
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Pending
    {
        public long Kills;
        public long Deaths;
        public long Suicides;
        public DateTimeOffset LastAt;
    }

    private Dictionary<string, PlayerKills> Stored() =>
        store.Read(Datasets.KillStats, new Dictionary<string, PlayerKills>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Count one kill line.
    /// </summary>
    /// <remarks>
    /// THE THREE SHAPES PAVLOV PRODUCES, and they are not interchangeable:
    ///   killer != killed  - a kill for one, a death for the other.
    ///   killer == killed  - a suicide: a death, and no kill for anybody.
    ///   no killer at all  - a fall or a drowning: a death, and no kill for anybody.
    /// Counting either of the last two as a kill is how a leaderboard gets farmed.
    /// </remarks>
    public void Record(KillEvent kill, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(kill);

        if (kill.Killed is not { Length: > 0 } killed) return;

        var suicide = kill.Killer is { Length: > 0 } k && string.Equals(k, killed, StringComparison.OrdinalIgnoreCase);

        Add(killed, deaths: 1, suicides: suicide ? 1 : 0, at: at);

        if (!suicide && kill.Killer is { Length: > 0 } killer)
            Add(killer, kills: 1, at: at);
    }

    private void Add(string player, long kills = 0, long deaths = 0, long suicides = 0, DateTimeOffset at = default)
    {
        var pending = _pending.GetOrAdd(player, _ => new Pending());

        /* Interlocked rather than a lock: log ingest is single-threaded today, and a counter
           that silently loses increments the day it is not would be invisible. */
        Interlocked.Add(ref pending.Kills, kills);
        Interlocked.Add(ref pending.Deaths, deaths);
        Interlocked.Add(ref pending.Suicides, suicides);
        if (at > pending.LastAt) pending.LastAt = at;
    }

    /// <summary>
    /// One player's totals, including whatever has not been flushed yet.
    /// </summary>
    /// <remarks>
    /// The pending deltas are added in rather than ignored, so a player who checks their
    /// stats straight after a kill sees it. A scoreboard that is a minute behind is the sort
    /// of thing that gets reported as a bug forever.
    /// </remarks>
    public PlayerKills Of(string player)
    {
        if (string.IsNullOrWhiteSpace(player)) return PlayerKills.Empty(player ?? "");

        var stored = Stored().GetValueOrDefault(player) ?? PlayerKills.Empty(player);
        if (!_pending.TryGetValue(player, out var pending)) return stored;

        return stored with
        {
            Player = stored.Player is { Length: > 0 } named ? named : player,
            Kills = stored.Kills + Interlocked.Read(ref pending.Kills),
            Deaths = stored.Deaths + Interlocked.Read(ref pending.Deaths),
            Suicides = stored.Suicides + Interlocked.Read(ref pending.Suicides),
            LastAt = pending.LastAt > (stored.LastAt ?? DateTimeOffset.MinValue) ? pending.LastAt : stored.LastAt,
        };
    }

    /// <summary>Everyone on record, most kills first. For a future board and for tests.</summary>
    public IReadOnlyList<PlayerKills> All()
    {
        var totals = Stored();
        foreach (var name in _pending.Keys) totals[name] = Of(name);
        return [.. totals.Values.OrderByDescending(p => p.Kills)];
    }

    /// <summary>
    /// Write the buffered kills. A no-op when nothing happened.
    /// </summary>
    /// <remarks>
    /// THE DELTAS ARE TAKEN OFF THE BUFFER FIRST, then merged. Reading and clearing
    /// separately would drop any kill that landed in between - rare, unnoticeable, and
    /// permanent.
    /// </remarks>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_pending.IsEmpty) return;

        var taken = new List<(string Player, Pending Delta)>();
        foreach (var name in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(name, out var delta)) taken.Add((name, delta));
        }

        if (taken.Count == 0) return;

        try
        {
            await store.UpdateAsync(Datasets.KillStats,
                new Dictionary<string, PlayerKills>(StringComparer.OrdinalIgnoreCase),
                totals =>
                {
                    foreach (var (player, delta) in taken)
                    {
                        var current = totals.GetValueOrDefault(player) ?? PlayerKills.Empty(player);
                        totals[player] = current with
                        {
                            Player = current.Player is { Length: > 0 } named ? named : player,
                            Kills = current.Kills + delta.Kills,
                            Deaths = current.Deaths + delta.Deaths,
                            Suicides = current.Suicides + delta.Suicides,
                            LastAt = delta.LastAt > (current.LastAt ?? DateTimeOffset.MinValue)
                                ? delta.LastAt
                                : current.LastAt,
                        };
                    }
                    return totals;
                }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* PUT BACK, not dropped. A failed write with the buffer already cleared loses
               those kills for good; returning them means the next flush tries again. */
            foreach (var (player, delta) in taken)
            {
                var pending = _pending.GetOrAdd(player, _ => new Pending());
                Interlocked.Add(ref pending.Kills, delta.Kills);
                Interlocked.Add(ref pending.Deaths, delta.Deaths);
                Interlocked.Add(ref pending.Suicides, delta.Suicides);
                if (delta.LastAt > pending.LastAt) pending.LastAt = delta.LastAt;
            }

            logger.LogWarning(ex, "Could not write kill stats - {Count} player(s) held for the next flush", taken.Count);
        }
    }
}
