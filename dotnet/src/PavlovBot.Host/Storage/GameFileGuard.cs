using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Storage;

/// <summary>
/// Paths this bot must never write to, however it is otherwise configured.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS: TWO BOTS, ONE GAME SERVER. A themed second bot runs beside the normal one
/// against the same install, and several subsystems here rewrite a whole file from their own
/// database - the ban list, the whitelist, a player's balance. Two bots doing that to one file
/// means each erases the other's work on a timer, and the loser is whichever wrote first.
///
/// LEAVING THE SETTING BLANK IS STILL THE WAY TO TURN A FEATURE OFF. This is the belt to that
/// pair of braces, and it earns its place because the second bot's .env is a COPY of the
/// first's - so every shared path is already in it, correct, and pointing at files it must not
/// touch. Deleting five settings correctly is a thing to get right once and forget forever;
/// naming the directory that belongs to the other bot is a thing to get right once and be
/// protected by.
///
/// WRITES ONLY. Reads are untouched, deliberately: the second bot still needs to read the
/// rosters to enforce one-faction-per-player, and a read cannot corrupt a file somebody else
/// owns. Refusing reads would break real behaviour to prevent nothing.
///
/// SEPARATOR-BOUNDED, via <see cref="GameFiles.InsideInstall"/>, so naming
/// <c>/home/steam/pavlovserver</c> does not also cover <c>/home/steam/pavlovserver-backup</c>.
/// </remarks>
public sealed class GameFileGuard
{
    /// <summary>Guards nothing. The default for a bot that owns every file it is given.</summary>
    public static GameFileGuard None { get; } = new([], null);

    private readonly IReadOnlyList<string> _ignored;
    private readonly ILogger? _logger;

    /// <summary>Paths already reported, so a five-minute timer does not fill the log.</summary>
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.OrdinalIgnoreCase);

    public GameFileGuard(IReadOnlyList<string> ignored, ILogger<GameFileGuard>? logger)
    {
        ArgumentNullException.ThrowIfNull(ignored);

        // Blank entries are what a trailing comma in .env produces, and an empty string would
        // match every path once it was turned into a full path.
        _ignored = [.. ignored.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];
        _logger = logger;
    }

    public bool Any => _ignored.Count > 0;

    /// <summary>The configured paths, for the startup summary.</summary>
    public IReadOnlyList<string> Paths => _ignored;

    /// <summary>
    /// Why this path may not be written, or null when it may.
    /// </summary>
    /// <remarks>
    /// Composes the ignore list with <see cref="GameFiles.Problem"/>, so every caller gets both
    /// rules from one call and neither can be forgotten at a new write site.
    /// </remarks>
    public string? Problem(string? path)
    {
        if (path is { Length: > 0 } && Any && GameFiles.InsideInstall(path, _ignored))
        {
            /* SAID ONCE PER PATH. The ban export runs every five minutes; a line per attempt
               would bury the one that matters. Once is enough to answer "why is this bot not
               writing that file", which is the only question this ever needs to answer. */
            if (_reported.TryAdd(path, 0))
            {
                _logger?.LogInformation(
                    "Not writing {Path} - it is covered by IGNORE_PATHS. Another bot or process owns " +
                    "this file. Reads are unaffected", path);
            }

            return "this path is in IGNORE_PATHS - another bot or process owns it";
        }

        return GameFiles.Problem(path);
    }
}
