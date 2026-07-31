using System.Text;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Logs;

/// <param name="File">Which log the line came from. Two servers write two logs.</param>
/// <param name="Text">The line, without its terminator.</param>
public readonly record struct LogLine(string File, string Text);

/// <summary>
/// Following Pavlov.log as the server writes it.
/// </summary>
/// <remarks>
/// Polling rather than a filesystem watcher, deliberately. The server may be on a network
/// mount or in a container where change notifications are unreliable or absent, and a
/// watcher that silently stops firing looks exactly like a quiet server - the failure mode
/// where ban evasion goes undetected for a week and nobody notices.
///
/// Three situations the naive "read from where I stopped" version gets wrong, each of which
/// loses data silently:
///
///   ROTATION. Pavlov renames the log and starts a new one. The old handle keeps reading a
///   file nobody writes to any more, so the tailer appears healthy and sees nothing. A file
///   SHORTER than the last offset is the signal, and the response is to start over from
///   zero rather than seek past the end of the new file.
///
///   A PARTIAL LINE. A poll can land mid-write. Consuming the fragment means the parser
///   sees half a line and the other half never arrives, so the tail of the buffer is
///   retained until its newline shows up.
///
///   A HUGE FIRST PASS. A server that ran for months has a log measured in gigabytes.
///   Reading all of it at startup would stall the bot and re-announce every join that ever
///   happened, so the first pass reads only the tail.
/// </remarks>
public sealed class LogTailer
{
    private sealed class FileState
    {
        public long Offset;
        public string Carry = "";      // a partial final line, held until its newline arrives
        public bool Primed;            // has the initial positioning happened
    }

    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private readonly long _maxBackfillBytes;

    /// <param name="maxBackfillBytes">
    /// How much of an existing log to read on the first pass. Enough to catch up across a
    /// bot restart, far short of a months-old log.
    /// </param>
    public LogTailer(ILogger logger, long maxBackfillBytes = 50L * 1024 * 1024)
    {
        _logger = logger;
        _maxBackfillBytes = maxBackfillBytes;
    }

    /// <summary>
    /// Read whatever has been appended since the last call.
    /// </summary>
    /// <param name="fromStart">
    /// Read the whole file (up to the backfill cap) rather than only new lines. Used for a
    /// deliberate re-scan; the normal first pass positions at the end so a restart does not
    /// replay history.
    /// </param>
    public IReadOnlyList<LogLine> Poll(string path, bool fromStart = false)
    {
        if (!_files.TryGetValue(path, out var state)) _files[path] = state = new FileState();

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (!state.Primed)
        {
            state.Primed = true;
            /* Position at the END on a normal first pass. Starting at zero would re-announce
               every join in the file - on a restart during an incident, that is thousands of
               feed messages about players who left days ago. */
            state.Offset = fromStart ? Math.Max(0, info.Length - _maxBackfillBytes) : info.Length;
            if (fromStart && info.Length > _maxBackfillBytes)
                _logger.LogInformation("{Path} is {Size}MB - reading only the last {Cap}MB",
                    path, info.Length / 1048576, _maxBackfillBytes / 1048576);
        }

        if (info.Length < state.Offset)
        {
            /* Rotated or truncated. Start over from zero rather than seeking past the end of
               the new file - which is what a naive tailer does, and why it then reads
               nothing forever while appearing perfectly healthy. */
            _logger.LogInformation("{Path} shrank ({Was} -> {Now}) - treating it as rotated and re-reading from the start",
                path, state.Offset, info.Length);
            state.Offset = 0;
            state.Carry = "";
        }

        if (info.Length == state.Offset) return [];

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);   // the server holds it open for writing
            stream.Seek(state.Offset, SeekOrigin.Begin);

            var buffer = new byte[Math.Min(info.Length - state.Offset, 8 * 1024 * 1024)];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return [];

            state.Offset += read;

            var text = state.Carry + Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n');

            /* The last element is whatever followed the final newline. If the file ended
               ON a newline it is empty; otherwise it is a partial line still being written,
               and consuming it would hand the parser half a line whose other half never
               arrives separately. */
            state.Carry = lines[^1];

            return lines[..^1]
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .Select(l => new LogLine(path, l))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not read {Path}: {Message}", path, ex.Message);
            return [];
        }
    }

    /// <summary>Forget a file's position, so the next poll re-primes.</summary>
    public void Reset(string? path = null)
    {
        if (path is null) _files.Clear();
        else _files.Remove(path);
    }

    public long OffsetOf(string path) => _files.TryGetValue(path, out var state) ? state.Offset : 0;

    /// <summary>
    /// Find Pavlov.log under the usual roots.
    /// </summary>
    /// <remarks>
    /// The configured path wins outright. Auto-detection is a convenience for a standard
    /// install, and a WRONG guess is worse than none - a tailer pointed at a stale log from
    /// an old install reads nothing forever - so only paths that exist are returned, and
    /// which ones were found is logged.
    /// </remarks>
    public static IReadOnlyList<string> Discover(string? configured, ILogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var paths = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(File.Exists).ToList();

            if (paths.Count == 0)
                logger?.LogWarning("PAVLOV_LOGS is set to \"{Configured}\" but none of those files exist - " +
                                   "log-derived features (ban evasion, join feed, kill stats) will do nothing", configured);
            return paths;
        }

        var tail = Path.Combine("Pavlov", "Saved", "Logs", "Pavlov.log");
        var candidates = new[]
        {
            Path.Combine("/home/steam/pavlovserver", tail),
            Path.Combine("/home/steam/pavlovserver2", tail),
            Path.Combine("/opt/pavlovserver", tail),
        }.Where(File.Exists).ToList();

        if (candidates.Count > 0) logger?.LogInformation("Found {Count} Pavlov log(s): {Paths}", candidates.Count, string.Join(", ", candidates));
        else logger?.LogWarning("No Pavlov.log found and PAVLOV_LOGS is not set - log-derived features are off");

        return candidates;
    }
}
