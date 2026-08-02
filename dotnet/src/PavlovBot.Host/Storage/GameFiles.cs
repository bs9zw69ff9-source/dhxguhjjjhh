namespace PavlovBot.Host.Storage;

/// <summary>
/// The bot does not write files the game server owns.
/// </summary>
/// <remarks>
/// THE RULE: no player ledger files, no whitelist.txt, and no directory inside a Pavlov
/// install - ever, for any reason.
///
/// This started as a narrower guard after the bot built a second ModSave tree beside the
/// real one, and it is now absolute because the narrow version kept finding new ways to be
/// wrong. Every one of these writes had the same shape of failure: a path that looked
/// plausible, a write that reported success, and a game server that read a different file.
/// Nothing errored, so nothing was noticeable until a directory nobody created turned up on
/// the server or somebody's balance was gone.
///
/// READING IS UNAFFECTED. The bot still reads ledgers for the boards, the whitelist for
/// <c>/tester list</c>, and the ban list for imports - all of which are safe, because a read
/// cannot corrupt a file the game is also using.
///
/// The bot's OWN data - bot.db, the JSON export, faction backups - is not covered by this
/// and is written normally. It owns those.
/// </remarks>
internal static class GameFiles
{
    /// <summary>
    /// Why this file may not be written. Never null: the bot does not write game files.
    /// </summary>
    /// <param name="what">What was being written, for a message somebody can act on.</param>
    public static string Refusal(string what) =>
        $"{what} is a file the game server owns, and the bot does not write those. " +
        "Edit it on the server instead.";

    /// <summary>
    /// Whether this file may be written: only into a directory that ALREADY exists.
    /// </summary>
    /// <remarks>
    /// For the files the bot genuinely owns and the mod merely reads - the ban list, the
    /// faction rosters. It still never CREATES a directory: a missing one means the
    /// configured path is wrong, and building it is what produced a second config tree
    /// beside the real one.
    /// </remarks>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "no path configured";

        string? directory;
        try { directory = Path.GetDirectoryName(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"not a usable path: {ex.Message}";
        }

        if (string.IsNullOrEmpty(directory)) return "no directory in the path";

        return Directory.Exists(directory)
            ? null
            : $"{directory} does not exist - the bot never creates a directory inside a game install, " +
              "because a missing one means the configured path is wrong";
    }

    /// <summary>
    /// True when a path lies inside one of the game installs.
    /// </summary>
    /// <remarks>
    /// Separator-bounded, so "pavlovserver" does not match "pavlovserver-backup" - a prefix
    /// test would quietly cover directories that are not installs at all.
    /// </remarks>
    public static bool InsideInstall(string path, IReadOnlyList<string> installs)
    {
        ArgumentNullException.ThrowIfNull(installs);

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return installs.Any(install =>
        {
            var root = Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar);
            return full.Equals(root, StringComparison.Ordinal) ||
                   full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        });
    }
}
