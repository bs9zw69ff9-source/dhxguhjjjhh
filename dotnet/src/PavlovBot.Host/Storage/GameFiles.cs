namespace PavlovBot.Host.Storage;

/// <summary>
/// Writing into a Pavlov install, without inventing one.
/// </summary>
/// <remarks>
/// THE RULE IS ONE LEVEL. The bot may create the single directory a file lives in, and only
/// when that directory's own parent already exists. It may never build a chain.
///
/// Both halves are load-bearing, and each fixes a real failure:
///
///   CREATING ONE LEVEL is necessary. ModSave is made by the mod, not by the base game, so
///   on a fresh install <c>Config/</c> exists and <c>Config/ModSave/</c> does not - and the
///   Node bot creates it there for exactly this reason. Refusing would break a new server.
///
///   CREATING A CHAIN IS ALWAYS WRONG. A path pointing somewhere that is not an install has
///   no <c>Pavlov/Saved/Config</c> above it, and building the whole tree produces a second
///   config directory beside the real one. Nothing errors, the bot reports success, the game
///   never reads a byte of it, and the only symptom is a directory nobody put there - which
///   is a genuinely confusing thing to find on a server and impossible to attribute later.
///
/// None of this applies to the bot's own data directory, which it owns and should create.
/// </remarks>
internal static class GameFiles
{
    /// <summary>
    /// Why this path cannot be written, or null when it can.
    /// </summary>
    /// <remarks>
    /// The FILE may legitimately not exist - a server with nobody banned has no banlist.txt.
    /// What must exist is the grandparent, so that at most one directory gets created.
    /// </remarks>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "no path configured";

        string? directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"not a usable path: {ex.Message}";
        }

        if (string.IsNullOrEmpty(directory)) return "no directory in the path";
        if (Directory.Exists(directory)) return null;

        var parent = Path.GetDirectoryName(directory);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return null;   // one level, allowed

        return $"{directory} does not exist and neither does its parent - the bot creates at most one " +
               "directory inside a game install, because building a whole tree means the configured path " +
               "is wrong and produces a second config directory the game never reads";
    }

    /// <summary>
    /// Make the containing directory if that is allowed, and report whether writing may go ahead.
    /// </summary>
    public static bool Prepare(string path, out string? problem)
    {
        problem = Problem(path);
        if (problem is not null) return false;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"could not create {directory}: {ex.Message}";
                return false;
            }
        }

        return true;
    }
}
