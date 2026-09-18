using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PavlovBot.Core.Logs;

namespace PavlovBot.Host.Logs;

/// <summary>
/// Confirms an RCON command actually ran, by watching for it in Pavlov.log.
/// </summary>
/// <remarks>
/// THE RCON REPLY IS NOT PROOF. Pavlov accepts an unknown or no-op command and answers, and
/// RCON+ menu verbs answer over a channel whose reply the bot cannot always read - which is
/// how a grant reported "no server accepted the command" while the menu was in fact handed
/// out, and how one reported success while nothing happened. The server itself logs what it
/// RAN: a base verb as <c>Rcon: Ban …</c>, an RCON+ command as
/// <c>Rcon Plus Command Executed: GiveMenu …</c>. That line is the ground truth, and this
/// watches for it.
///
/// It subscribes to the same tail everything else reads - <see cref="IpTrackingService.Rcon"/>,
/// which already parses those lines for the audit feed - and remembers each executed command
/// by the WALL CLOCK it was seen at, not the log's own stamp: the game server's clock and the
/// bot's can differ, and a caller only ever asks "did this run AFTER I sent it", which is a
/// question about the bot's own timeline.
///
/// A caller sends, then <see cref="ConfirmedAsync"/> waits a moment for the line to arrive -
/// the tail polls on its own interval, so the confirmation lands a second or two after the
/// send, not instantly. Bounded and swept, so a busy server cannot grow it without limit.
/// </remarks>
public sealed partial class RconConfirmations
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _executed = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    /// <summary>How long an executed-command record is kept before it is swept.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(2);

    /// <summary>How often the wait re-checks while a confirmation has not yet arrived.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public RconConfirmations(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// Attach to the log tail, so every executed command is recorded.
    /// </summary>
    /// <remarks>
    /// Wired the same way the feed bridge is - the tracker raises this for every RCON line it
    /// parses, and one more subscriber costs nothing. Only the command's own shape is kept, no
    /// player data, so this holds nothing sensitive.
    /// </remarks>
    public void Watch(IpTrackingService tracking)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        tracking.Rcon += (_, _, action) =>
        {
            Note(action.Full);
            return Task.CompletedTask;
        };
    }

    /// <summary>Record that a command was seen executing, now.</summary>
    internal void Note(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        _executed[Normalize(command)] = _time.GetUtcNow();

        // Swept opportunistically rather than on a timer: the map only grows on RCON traffic,
        // so the write path is the right place to bound it, and it stays small.
        if (_executed.Count > 512)
        {
            var cutoff = _time.GetUtcNow() - Retention;
            foreach (var (key, at) in _executed)
                if (at < cutoff) _executed.TryRemove(key, out _);
        }
    }

    /// <summary>Whether this command has been seen executing at or after <paramref name="since"/>.</summary>
    public bool ConfirmedSince(string command, DateTimeOffset since) =>
        !string.IsNullOrWhiteSpace(command)
        && _executed.TryGetValue(Normalize(command), out var at)
        && at >= since;

    /// <summary>
    /// Wait for the log to confirm a command ran, up to <paramref name="within"/>.
    /// </summary>
    /// <param name="command">The command exactly as it was sent to RCON.</param>
    /// <param name="since">The instant the command was sent; an older log entry does not count.</param>
    /// <returns>True once the executed line is seen; false if the window passes without it.</returns>
    public async Task<bool> ConfirmedAsync(string command, DateTimeOffset since, TimeSpan within,
        CancellationToken ct = default)
    {
        var deadline = _time.GetUtcNow() + within;
        while (true)
        {
            if (ConfirmedSince(command, since)) return true;
            if (_time.GetUtcNow() >= deadline) return false;

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return ConfirmedSince(command, since); }
        }
    }

    // The log may render an executed command with different spacing than the bot sent it; a
    // bit code carries an internal space of its own. Collapsing runs of whitespace to one lets
    // "GiveMenu Bob 0010  1000" and "GiveMenu Bob 0010 1000" match without a false miss.
    private static string Normalize(string command) => Whitespace.Replace(command.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace { get; }
}
