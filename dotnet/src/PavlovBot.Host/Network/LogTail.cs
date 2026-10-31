using System.Text;

namespace PavlovBot.Host.Network;

/// <summary>The newest line of a system log, read from its end.</summary>
public static class LogTail
{
    /// <summary>The most it reads back from the end: a syslog line is far shorter.</summary>
    private const int TailBytes = 8192;

    /// <summary>
    /// The last non-empty line of a log under <c>/var/log</c>, or null. Only there: the path comes
    /// from cscli's output, and the bot runs as root.
    /// </summary>
    public static string? LastLine(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Path.IsPathFullyQualified(path)) return null;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith("/var/log/", StringComparison.Ordinal)) return null;
        return LastLineOf(full);
    }

    /// <summary>The last non-empty line of any file; for <see cref="LastLine"/> and tests.</summary>
    internal static string? LastLineOf(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = file.Length;
            var take = (int)Math.Min(length, TailBytes);
            if (take == 0) return null;
            file.Seek(length - take, SeekOrigin.Begin);
            var buffer = new byte[take];
            file.ReadExactly(buffer);
            var lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // The first piece may be cut mid-line; it is only used when it is the whole tail.
            return lines.Length == 0 ? null : lines[^1];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
