namespace PavlovBot.Host.Storage;

/// <summary>
/// The bot does not write files the game server rewrites from memory, and never creates a
/// directory inside a Pavlov install.
/// </summary>
/// <remarks>
/// THE RULE, and it is narrower than it once was:
///
///   NO PLAYER LEDGER FILES. The server holds a connected player's balance in memory and
///   rewrites <c>modsave/&lt;player&gt;.txt</c> from it on the next save, so a bot write to a
///   live player is destroyed with no error anywhere. This is a demonstrated failure and it
///   is what the whole rule was built for.
///
///   NEVER CREATE A DIRECTORY inside an install. A missing one means the configured path is
///   wrong, and building it is what produced a second ModSave tree beside the real one - a
///   write that reported success against a file the game server never read.
///
/// IT USED TO COVER whitelist.txt TOO, and that was an over-application. The ledger rule
/// rests on the server rewriting the file from memory; whitelist.txt is a config INPUT that
/// the server reads and never writes back. It sits in the same directory as the faction
/// roster .txt files, which the bot has always written, is the same format, and is read the
/// same way - so refusing one and allowing the others was a distinction with nothing behind
/// it. The protections that actually matter there are the ones the rosters already use: an
/// atomic write, a pre-write backup, a guard against a write that deletes implausibly much,
/// and never creating the directory.
///
/// READING IS UNAFFECTED AND ALWAYS WAS, for every file. A read cannot corrupt a file the
/// game is also using.
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
