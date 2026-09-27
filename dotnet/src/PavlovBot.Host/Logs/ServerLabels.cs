namespace PavlovBot.Host.Logs;

/// <summary>
/// Which server a log line came from, named the way the feeds say it: "Server 1".
/// </summary>
/// <remarks>
/// BY DISCOVERY ORDER, not by folder name. A two-install box may have its second server in
/// <c>pavlovserver2</c>, <c>pavlov-ttt</c> or anything else, and naming the feed after the
/// directory would print a path fragment at people. The order the logs were discovered in
/// is the order the installs are configured in, which is what an owner means by "server 2".
///
/// The Node bot numbers them the same way, and the join log is the one feed people read
/// every day - a line that suddenly says <c>Pavlov.log</c> where it used to say
/// <c>Server 1</c> reads as a broken bot even when nothing else changed.
/// </remarks>
public sealed class ServerLabels
{
    private IReadOnlyList<string> _paths = [];

    /// <summary>Called once, with the discovered logs in order.</summary>
    public void Assign(IReadOnlyList<string> logPaths)
    {
        ArgumentNullException.ThrowIfNull(logPaths);
        _paths = logPaths.ToList();
    }

    public string Of(string file) => Label(_paths, file);

    /// <summary>The 1-based server number of a log file, or null when it is not a discovered log.</summary>
    public int? NumberOf(string file) => NumberOf(_paths, file);

    /// <summary>The pure part of <see cref="NumberOf(string)"/>.</summary>
    public static int? NumberOf(IReadOnlyList<string> paths, string file)
    {
        ArgumentNullException.ThrowIfNull(paths);

        for (var index = 0; index < paths.Count; index++)
        {
            if (string.Equals(paths[index], file, StringComparison.OrdinalIgnoreCase))
                return index + 1;
        }

        return null;
    }

    /// <summary>
    /// The log file of an RCON server ("server2" is the second discovered log), or null when
    /// that server has no log being tailed.
    /// </summary>
    /// <remarks>
    /// The same numbering as <see cref="Of"/>: "Server 2" in a feed and "server2" in RCON are
    /// the same box. The order logs are discovered in follows <c>PAVLOV_LOGS</c>, else the
    /// install order - which is how the servers are numbered.
    /// </remarks>
    public string? LogFor(string rconServer) => LogFor(_paths, rconServer);

    /// <summary>The pure part of <see cref="LogFor(string)"/>, for tests.</summary>
    public static string? LogFor(IReadOnlyList<string> paths, string rconServer)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (rconServer is null ||
            !rconServer.StartsWith("server", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(rconServer.AsSpan("server".Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) ||
            number < 1 || number > paths.Count)
        {
            return null;
        }
        return paths[number - 1];
    }

    /// <summary>
    /// The label for a log path, given the discovered set.
    /// </summary>
    /// <remarks>
    /// Falls back to "the server" rather than to the file name. An unrecognised path means
    /// the numbering is not known, and a plausible-looking wrong number ("Server 1" for
    /// what is actually server 2) sends staff to the wrong place; "the server" is visibly
    /// unspecific and cannot mislead.
    /// </remarks>
    public static string Label(IReadOnlyList<string> paths, string file)
    {
        return NumberOf(paths, file) is { } number ? $"Server {number}" : "the server";
    }
}
