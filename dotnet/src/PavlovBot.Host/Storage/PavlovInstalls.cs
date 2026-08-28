using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Storage;

/// <summary>
/// Where every Pavlov install lives on this box.
/// </summary>
/// <remarks>
/// A port of the Node bot's discovery, and it must stay a port: anything written to one
/// install has to reach ALL of them, and two bots disagreeing about how many there are is
/// how a player ends up whitelisted on server 1 and rejected by server 2.
///
/// The rule is: an explicit <c>PAVLOV_BASES</c> wins; otherwise every sibling of
/// <c>PAVLOV_BASE_1</c> whose name starts the same way and which actually contains a
/// <c>Pavlov</c> directory. That last check is what keeps a backup folder called
/// <c>pavlovserver.old</c> from being written to as though it were live.
/// </remarks>
public static class PavlovInstalls
{
    public const string DefaultBase = "/home/steam/pavlovserver";

    /// <summary>
    /// Resolve the install roots.
    /// </summary>
    /// <param name="explicitBases">PAVLOV_BASES, comma or colon separated. Wins outright.</param>
    /// <param name="firstBase">PAVLOV_BASE_1. Its parent and prefix drive the scan.</param>
    public static IReadOnlyList<string> Discover(
        string? explicitBases = null, string? firstBase = null, ILogger? logger = null)
    {
        var listed = (explicitBases ?? "")
            .Split([',', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (listed.Count > 0)
        {
            logger?.LogInformation("Using the {Count} install(s) named in PAVLOV_BASES", listed.Count);
            return listed;
        }

        var root = string.IsNullOrWhiteSpace(firstBase) ? DefaultBase : firstBase.TrimEnd('/');

        try
        {
            var parent = Path.GetDirectoryName(root);
            var prefix = Path.GetFileName(root);

            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(prefix) && Directory.Exists(parent))
            {
                var found = Directory.EnumerateDirectories(parent, $"{prefix}*")
                    // A REAL install, not a backup copy or an unpacked archive sitting beside one.
                    .Where(d => Directory.Exists(Path.Combine(d, "Pavlov")))
                    .OrderBy(d => d, StringComparer.Ordinal)
                    .ToList();

                if (found.Count > 0) return found;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning("Could not scan for Pavlov installs: {Message}", ex.Message);
        }

        /* Falling back to the single configured base rather than to nothing. An empty list
           would make every cross-install write silently succeed at doing nothing. */
        return [root];
    }

    /// <summary>The whitelist file inside an install.</summary>
    public static string WhitelistPath(string installRoot) =>
        Path.Combine(installRoot, "Pavlov", "Saved", "Config", "whitelist.txt");

    /// <summary>
    /// The server's own <c>Game.ini</c>.
    /// </summary>
    /// <remarks>
    /// Under <c>LinuxServer</c>, which is the platform directory the game reads on this box - a
    /// <c>WindowsServer</c> copy beside it would be ignored, so the platform is part of the path
    /// rather than something to guess at.
    /// </remarks>
    public static string GameIniPath(string installRoot) =>
        Path.Combine(installRoot, "Pavlov", "Saved", "Config", "LinuxServer", "Game.ini");
}
