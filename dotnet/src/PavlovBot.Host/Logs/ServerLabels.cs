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
        ArgumentNullException.ThrowIfNull(paths);

        for (var index = 0; index < paths.Count; index++)
        {
            if (string.Equals(paths[index], file, StringComparison.OrdinalIgnoreCase))
                return $"Server {index + 1}";
        }

        return "the server";
    }
}
