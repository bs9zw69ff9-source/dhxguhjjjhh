using PavlovBot.Host.Logs;

namespace PavlovBot.Host.Monitoring;

/// <summary>
/// When a server's Pavlov log last advanced, from the log file's modification time.
/// </summary>
/// <remarks>
/// A PROXY, and an honest one: the file's mtime is when it was last written, which is exactly
/// "when the server last logged anything". It maps a server to its log through the same
/// <see cref="ServerLabels"/> the rest of the bot uses, so a server whose RCON name matches its log
/// label ("Server 1") gets log-inactivity monitoring for free. A server that cannot be matched to a
/// file returns null - the monitor then simply does not run the log check for it, rather than
/// inventing a silence it cannot see.
/// </remarks>
public sealed class ServerLogActivity(ServerLabels labels, IReadOnlyList<string> logPaths)
{
    public DateTimeOffset? For(string server)
    {
        foreach (var path in logPaths)
        {
            if (!string.Equals(labels.Of(path), server, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                return File.Exists(path)
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                    : null;
            }
            catch (Exception)
            {
                return null;   // a log we cannot stat is "unknown", never a crash
            }
        }
        return null;
    }
}
