using Microsoft.Extensions.Logging;
using PavlovBot.Core.Logs;
using PavlovBot.Host.Observability;

namespace PavlovBot.Host.Logs;

/// <summary>
/// Tailing Pavlov's Stats.log and raising the kills it records.
/// </summary>
/// <remarks>
/// THE SAME SHAPE AS THE Pavlov.log TAIL, and deliberately sharing <see cref="LogTailer"/>
/// with it. That class already solves the three things a naive tail gets wrong - rotation,
/// a poll landing mid-line, and a first pass over a months-old file - and Stats.log rotates
/// more often than Pavlov.log does, roughly hourly, so the rotation handling matters more
/// here rather than less.
///
/// ONE READER PER FILE. The parser is a state machine holding a partly-read block, so two
/// servers sharing one would interleave their blocks into nonsense.
///
/// Rotation is the one case the reader cannot see coming: the tailer starts the new file at
/// offset zero and the old block's closing brace never arrives. The reader drops it and
/// counts it, which is the right trade - one kill lost at the rotation boundary against
/// holding a broken block forever.
/// </remarks>
public sealed class StatsLogService
{
    private readonly Dictionary<string, StatsLogReader> _readers = new(StringComparer.Ordinal);
    private readonly LogTailer _tailer;
    private readonly MetricsRegistry _metrics;
    private readonly ILogger<StatsLogService> _logger;

    private readonly IReadOnlyList<string> _paths;
    private readonly Func<string, string?>? _resolveName;

    /// <param name="resolveName">
    /// Turns a unique id into a display name. See <see cref="Named"/> for why this is not
    /// optional in practice.
    /// </param>
    public StatsLogService(
        IReadOnlyList<string> paths, LogTailer tailer, MetricsRegistry metrics, ILogger<StatsLogService> logger,
        Func<string, string?>? resolveName = null)
    {
        ArgumentNullException.ThrowIfNull(tailer);
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _tailer = tailer;
        _metrics = metrics;
        _logger = logger;
        _resolveName = resolveName;
    }

    /// <summary>
    /// A name for whatever Stats.log wrote in a Killer or Killed field.
    /// </summary>
    /// <remarks>
    /// STATS.LOG WRITES THE ID RCON TARGETS, not the display name - so the kill feed was a
    /// wall of seventeen-digit numbers shooting other seventeen-digit numbers, which is
    /// unreadable and tells a moderator nothing.
    ///
    /// NOT AN EOS ID, and the difference matters. An EOS id is 32 hex characters beginning
    /// 0002 and is what Pavlov.log and the game's ban file carry; this is the plain number a
    /// Shack server uses, and the two never meet. The account registry is keyed on the first,
    /// so it cannot answer for the second however many entries it holds - the roster index is
    /// what actually resolves these.
    ///
    /// UNRESOLVED IS LEFT ALONE, not replaced with "unknown". Two reasons: the field
    /// sometimes already holds a name, in which case there is nothing to resolve and passing
    /// it through is exactly right; and an id nobody can name is still worth printing,
    /// because it is the only handle anybody has on that player.
    /// </remarks>
    private string Named(string raw)
    {
        if (_resolveName is null) return raw;

        try
        {
            var resolved = _resolveName(raw);
            return string.IsNullOrWhiteSpace(resolved) ? raw : resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* A lookup that throws must not cost the kill. The id is a worse line than the
               name and an infinitely better one than nothing. */
            _logger.LogDebug(ex, "Could not resolve a name for {Id}", raw);
            return raw;
        }
    }

    /// <summary>The stats logs found, resolved once at startup.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>Whether there is anything to read. False leaves the Pavlov.log scrape in charge.</summary>
    public bool Enabled => _paths.Count > 0;

    /// <summary>A kill, with the timestamp the game wrote rather than the clock now.</summary>
    public event Func<StatsKill, Task>? Killed;

    /// <summary>A round starting or ending.</summary>
    public event Func<StatsRoundState, Task>? RoundChanged;

    /// <summary>Read whatever has been appended to each stats log since the last tick.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        foreach (var path in _paths)
        {
            if (!_readers.TryGetValue(path, out var reader)) _readers[path] = reader = new StatsLogReader();

            foreach (var line in _tailer.Poll(path))
            {
                ct.ThrowIfCancellationRequested();

                foreach (var record in reader.Read(line.Text))
                {
                    /* PER RECORD. The tailer has already advanced past this whole batch, so
                       an exception escaping here discards every kill after it with no way to
                       get them back. One bad record must cost that record and nothing else. */
                    try
                    {
                        await RaiseAsync(record).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to handle a record from {Path} - skipping it", path);
                        _metrics.Increment("stats_log_errors_total", help: "Stats.log records that threw");
                    }
                }
            }
        }
    }

    private async Task RaiseAsync(object record)
    {
        switch (record)
        {
            case StatsKill kill:
                _metrics.Increment("kills_total",
                    MetricLabels.Of("headshot", kill.Headshot ? "yes" : "no"), help: "Kills read from Stats.log");

                /* RESOLVED HERE rather than at each consumer. Every one of them wants the
                   name, and leaving it to them means the next one added quietly prints ids
                   again. */
                if (Killed is { } onKill)
                {
                    await onKill(kill with { Killer = Named(kill.Killer), Killed = Named(kill.Killed) })
                        .ConfigureAwait(false);
                }
                break;

            case StatsRoundState round:
                if (RoundChanged is { } onRound) await onRound(round).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Find Stats.log beside each Pavlov.log, or wherever STATS_LOGS says.
    /// </summary>
    /// <remarks>
    /// DERIVED FROM THE LOG PATHS rather than guessed independently. A server's stats log
    /// sits at Pavlov/Saved/Stats/Stats.log against the same install root as its
    /// Pavlov/Saved/Logs/Pavlov.log, so deriving one from the other keeps a multi-server box
    /// consistent by construction - and keeps the numbering the same, which is what makes
    /// "Server 2" mean the same thing in both feeds.
    ///
    /// Only paths that exist are returned. A tailer pointed at a file that is not there
    /// reads nothing forever and looks exactly like a quiet server.
    /// </remarks>
    public static IReadOnlyList<string> Discover(string? configured, IReadOnlyList<string> pavlovLogs, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pavlovLogs);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var listed = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(File.Exists).ToList();

            if (listed.Count == 0)
            {
                logger?.LogWarning(
                    "STATS_LOGS is set to \"{Configured}\" but none of those files exist - kills will be read " +
                    "from Pavlov.log instead, without headshots and without the game's own timestamps", configured);
            }
            return listed;
        }

        var derived = pavlovLogs
            .Select(RootOf)
            .Where(root => root is not null)
            .Select(root => Path.Combine(root!, "Pavlov", "Saved", "Stats", "Stats.log"))
            .Distinct(StringComparer.Ordinal)
            .Where(File.Exists)
            .ToList();

        if (derived.Count > 0)
            logger?.LogInformation("Found {Count} stats log(s): {Paths}", derived.Count, string.Join(", ", derived));
        else
            logger?.LogInformation(
                "No Stats.log found - kills come from Pavlov.log instead, which needs bVerboseLogging=true " +
                "and carries no headshot flag. Set STATS_LOGS to use it");

        return derived;
    }

    /// <summary>The install root above a .../Pavlov/Saved/Logs/Pavlov.log path.</summary>
    private static string? RootOf(string pavlovLog)
    {
        // .../<root>/Pavlov/Saved/Logs/Pavlov.log - four levels up from the file.
        var directory = Path.GetDirectoryName(pavlovLog);            // .../Saved/Logs
        var saved = Path.GetDirectoryName(directory);                // .../Saved
        var pavlov = Path.GetDirectoryName(saved);                   // .../Pavlov
        return Path.GetDirectoryName(pavlov);                        // the root
    }
}
