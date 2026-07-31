namespace PavlovBot.Core.Logs;

/// <param name="Ip">The address, or null when nothing correlated.</param>
/// <param name="Confident">
/// True only when EXACTLY ONE connection was pending. With two people connecting at once
/// there is no way to tell which address belongs to which login, and a coin flip here
/// attributes a stranger's address to an account.
/// </param>
public readonly record struct JoinGuess(string? Ip, bool Confident)
{
    public static JoinGuess None => new(null, false);
}

/// <summary>
/// Correlating an accepted connection with the login that follows it.
/// </summary>
/// <remarks>
/// Pavlov logs the address at accept time and the account at login time, on different
/// lines, with no shared identifier between them. The only way to link them is proximity.
///
/// THE RESULT IS A GUESS AND IS LABELLED AS ONE. It is kept for display - "they probably
/// joined from here" is useful to a moderator - but it must NEVER flag an address or
/// trigger a ban. Only a <see cref="ConfirmedPairing"/>, where both appear on one line,
/// can do that.
///
/// Two rules make the guess honest rather than merely plausible:
///
///   A WINDOW. An accept more than a few seconds before the login is not related to it;
///   without the window, a quiet server correlates a login to an accept from minutes ago.
///
///   AMBIGUITY IS TRACKED, NOT RESOLVED. Every distinct address accepted since the last
///   login is remembered. More than one means "we do not know", and the caller is told so
///   rather than being handed the most recent as though it were the answer.
///
/// Per log file, because two servers writing two logs have independent connection streams.
/// </remarks>
public sealed class JoinCorrelator
{
    private sealed class FileState
    {
        public string? LatestIp;
        public DateTimeOffset LatestAt;
        public readonly HashSet<string> PendingIps = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    /// <param name="window">
    /// How long an accepted connection stays eligible to be matched to a login. Ten seconds
    /// is generous for a handshake and short enough that an idle connection does not linger
    /// into somebody else's join.
    /// </param>
    public JoinCorrelator(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(10);

    private FileState State(string file)
    {
        if (!_files.TryGetValue(file, out var state)) _files[file] = state = new FileState();
        return state;
    }

    /// <summary>Record an accepted connection.</summary>
    public void Accepted(string file, string ip, DateTimeOffset at)
    {
        var state = State(file);

        /* Expire the ambiguity set before adding. Without this, addresses from minutes ago
           keep the set above one forever and every join is reported as ambiguous - which
           looks like the feature is broken rather than cautious. */
        if (at - state.LatestAt > _window) state.PendingIps.Clear();

        state.LatestIp = ip;
        state.LatestAt = at;
        state.PendingIps.Add(ip);
    }

    /// <summary>
    /// Resolve the address for a login, and CLEAR the pending state.
    /// </summary>
    /// <param name="isPlaceholderId">
    /// True for a pre-authentication or self-connection id. The pending state is NOT
    /// cleared for one: the real login for that same connection is still coming, and
    /// clearing here would leave it with nothing to correlate against.
    /// </param>
    public JoinGuess Login(string file, DateTimeOffset at, bool isPlaceholderId = false)
    {
        var state = State(file);
        var within = at - state.LatestAt <= _window;

        var guess = within
            ? new JoinGuess(state.LatestIp, state.PendingIps.Count == 1)
            : JoinGuess.None;

        if (!isPlaceholderId)
        {
            state.LatestIp = null;
            state.PendingIps.Clear();
        }
        return guess;
    }

    /// <summary>How many distinct connections are currently unattributed. Diagnostic.</summary>
    public int PendingCount(string file) => State(file).PendingIps.Count;

    /// <summary>Forget everything. Used when a log is truncated or rotated underneath us.</summary>
    public void Reset(string? file = null)
    {
        if (file is null) _files.Clear();
        else _files.Remove(file);
    }
}
