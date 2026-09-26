using System.Text;

namespace PavlovBot.Host.Storage;

/// <summary>
/// Replace a file so a reader sees either the old contents or the new ones, never half.
/// </summary>
/// <remarks>
/// Write a temp file beside the target, flush it to disk, then rename it over the target. The
/// rename is atomic within a directory. Three details that each caused or risked real damage:
///
///   OWNER AND MODE ARE CARRIED OVER. A rename replaces the inode, so the result used to belong
///   to whoever the bot runs as. Game files belong to the user the game runs as, and a game that
///   cannot write its own ledger or ban file loses what it saves. The new file takes the old
///   one's owner and mode; a file that did not exist yet takes its directory's owner.
///
///   FLUSHED BEFORE THE RENAME. Without it a power loss or hard reset can leave a renamed but
///   empty file - a whitelist with nobody on it.
///
///   A UNIQUE TEMP NAME. A fixed <c>.tmp</c> let two concurrent writers to one path clobber each
///   other's half-written temp before either rename. It ends in <c>.tmp</c> so nothing that scans
///   a directory for <c>*.txt</c> mistakes it for the real file.
/// </remarks>
public static class AtomicFile
{
    /// <summary>Replace <paramref name="path"/> with <paramref name="contents"/> (UTF-8, no BOM).</summary>
    public static Task WriteAsync(string path, string contents, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Write(path, contents);
        return Task.CompletedTask;
    }

    /// <summary>Replace <paramref name="path"/> with <paramref name="contents"/> (UTF-8, no BOM).</summary>
    public static void Write(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        Write(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));
    }

    /// <summary>Replace <paramref name="path"/> with <paramref name="contents"/>.</summary>
    public static void Write(string path, byte[] contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);

        var temp = TempPathFor(path);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            CarryMetadata(path, temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the original error matters more */ }
            throw;
        }
    }

    /// <summary>The temp file used for one write of <paramref name="path"/>.</summary>
    internal static string TempPathFor(string path) => $"{path}.bot.{Guid.NewGuid():N}.tmp";

    /// <summary>
    /// Give <paramref name="temp"/> the owner and mode <paramref name="path"/> has, or, if it does
    /// not exist yet, its directory's owner.
    /// </summary>
    private static void CarryMetadata(string path, string temp)
    {
        if (OperatingSystem.IsWindows()) return;

        var exists = File.Exists(path);
        var mode = exists ? File.GetUnixFileMode(path) : File.GetUnixFileMode(temp);

        var reference = exists ? path : Path.GetDirectoryName(Path.GetFullPath(path));
        if (reference is not null &&
            UnixFileOwnership.Get(reference) is { } owner &&
            UnixFileOwnership.Get(temp) is { } mine &&
            Plan(Environment.IsPrivilegedProcess, owner, mine) is { } plan)
        {
            UnixFileOwnership.Set(temp, plan.Uid, plan.Gid);
            if (plan.GroupWrite) mode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        }

        File.SetUnixFileMode(temp, mode);
    }

    /// <summary>The ownership change a replacement needs, or null when it already matches.</summary>
    /// <remarks>
    /// ROOT gives the file straight back to its owner. AN UNPRIVILEGED BOT cannot give a file
    /// away, but it can set the group to one it belongs to - so a bot in the <c>steam</c> group
    /// hands the file to that group and makes it group-writable. Otherwise the new file would be
    /// the bot's alone, and the game, running as steam, could no longer save it.
    /// </remarks>
    internal static (uint Uid, uint Gid, bool GroupWrite)? Plan(
        bool privileged, (uint Uid, uint Gid) reference, (uint Uid, uint Gid) mine)
    {
        if (reference == mine) return null;
        if (privileged) return (reference.Uid, reference.Gid, false);
        return (UnixFileOwnership.Unchanged, reference.Gid, reference.Uid != mine.Uid);
    }
}
