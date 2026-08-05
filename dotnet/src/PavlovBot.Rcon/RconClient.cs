using PavlovBot.Rcon.Protocol;
using System.Collections.Concurrent;

namespace PavlovBot.Rcon;

/// <summary>
/// The RCON surface the rest of the bot uses. One instance per server.
/// </summary>
/// <remarks>
/// Adds three things over a bare socket, each earned from the Node bot:
///
///   PERSISTENT SESSION   The Node client opened a fresh TCP connection and MD5
///                        handshake per command, which the Pavlov docs explicitly advise
///                        against. Pavlov services RCON on the game thread, so every
///                        connection is work taken from the tick.
///
///   READ COALESCING      Six independent timers want RefreshList within the same
///                        second. Concurrent callers share one in-flight request, and a
///                        completed read is reused briefly. Measured on the Node side:
///                        6 connections per 30s cycle down to 2.
///
///   JITTERED RETRY       Fixed backoff makes every caller retry in lockstep after a
///                        blip - a synchronised burst exactly when the server can least
///                        serve one.
/// </remarks>
public sealed class RconClient : IAsyncDisposable
{
    /// <summary>
    /// Commands that only read state. Anything absent is assumed to mutate, which is the
    /// safe default: caching a Kick would be a correctness bug, whereas failing to cache
    /// a read is merely a wasted round trip.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "RefreshList", "ServerInfo", "ItemList", "MapList", "Banlist",
            "ModeratorList", "InspectAll", "UGCModList",
        };

    /// <summary>
    /// Every verb the bot is known to send, reads and mutations alike.
    /// </summary>
    /// <remarks>
    /// EXISTS FOR METRIC LABELS, not for validation - nothing here rejects a command. The
    /// verb was being used verbatim as a Prometheus label, and /manual sends whatever an
    /// owner types, so every typo minted a new time series. MetricsRegistry caps the total at
    /// MaxSeries and then silently drops NEW series for the life of the process, which means
    /// a few minutes of fat-fingering at the console could permanently blind the metrics that
    /// matter, with nothing anywhere saying why.
    /// </remarks>
    private static readonly HashSet<string> KnownVerbs =
        new(ReadOnlyCommands, StringComparer.OrdinalIgnoreCase)
        {
            "Ban", "Unban", "Kick", "Notify", "SetPin", "RemovePin", "RotateMap",
            "SwitchMap", "GiveItem", "GiveCash", "GiveVehicle", "SetPlayerSkin", "Slap",
        };

    /// <summary>The verb of a command, or "other" when it is not one the bot issues.</summary>
    /// <remarks>Bounded by construction, so it is safe to use as a metric label.</remarks>
    public static string MetricVerb(string command) =>
        KnownVerbs.Contains(Verb(command)) ? Verb(command) : "other";

    private readonly RconOptions _options;
    private readonly RconConnection _connection;
    private readonly ConcurrentDictionary<string, CacheEntry> _readCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _lazyInFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Commands this client issued recently, so log-derived audit can tell its own traffic apart.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _issued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan IssuedRetention = TimeSpan.FromMinutes(2);

    /// <summary>Size above which the map is worth sweeping at all.</summary>
    private const int IssuedSoftCap = 200;

    /// <summary>UTC ticks of the last sweep. Interlocked, so it is a long rather than a DateTimeOffset.</summary>
    private long _lastSweepTicks;

    private sealed record CacheEntry(string Value, DateTimeOffset At);

    public RconClient(RconOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connection = new RconConnection(options);
    }

    public string Name => _options.Name;

    internal static string Verb(string command)
    {
        var span = command.AsSpan().Trim();
        var i = span.IndexOf(' ');
        return (i < 0 ? span : span[..i]).ToString();
    }

    private static bool IsReadOnly(string command) => ReadOnlyCommands.Contains(Verb(command));

    /// <summary>Send a command, coalescing it with concurrent identical reads where safe.</summary>
    public Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        NoteIssued(command);

        if (_options.ReadCacheDuration <= TimeSpan.Zero || !IsReadOnly(command))
            return SendUncachedAsync(command, ct);

        var key = command.Trim();
        if (_readCache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < _options.ReadCacheDuration)
            return Task.FromResult(hit.Value);

        return Coalesced(key, ct);
    }

    /// <summary>
    /// Join the in-flight read for this command, or start it.
    /// </summary>
    /// <remarks>
    /// THE SHARED READ IS NOT BOUND TO ONE CALLER'S CANCELLATION. It used to be
    /// <c>GetOrAdd(key, k =&gt; StartRead(k, ct))</c>, which captured whichever caller
    /// happened to lose the race, and every joiner then inherited that caller's token: the
    /// first caller going away - a cancelled slash command, a stopping service - cancelled
    /// the read that four other callers were waiting on, and they saw a cancellation they
    /// never asked for. The shared work runs under <see cref="CancellationToken.None"/> and
    /// each caller waits on it under its OWN token instead.
    ///
    /// The old comment here claimed the task was "built eagerly so the loser of the race is
    /// discarded". It was not - the factory was a lambda, so a lost race started a SECOND
    /// exchange that ran to completion anyway. Built eagerly now, so that is true.
    /// </remarks>
    private async Task<string> Coalesced(string key, CancellationToken ct)
    {
        Task<string> shared;

        var mine = new Lazy<Task<string>>(() => StartRead(key), LazyThreadSafetyMode.ExecutionAndPublication);
        var winner = _lazyInFlight.GetOrAdd(key, mine);
        shared = winner.Value;

        /* The caller's own cancellation detaches it from the shared read; it does not cancel
           the read for everybody else. A read nobody is waiting on any more still completes
           and still populates the cache, which is what the next caller wanted anyway. */
        if (!ct.CanBeCanceled) return await shared.ConfigureAwait(false);

        var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => cancelled.TrySetCanceled(ct));

        return await (await Task.WhenAny(shared, cancelled.Task).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task<string> StartRead(string command)
    {
        try
        {
            var value = await SendUncachedAsync(command, CancellationToken.None).ConfigureAwait(false);
            // Only a SUCCESS is cached. A failure must never be replayed as an answer.
            _readCache[command] = new CacheEntry(value, DateTimeOffset.UtcNow);
            return value;
        }
        finally
        {
            _lazyInFlight.TryRemove(command, out _);
        }
    }

    private async Task<string> SendUncachedAsync(string command, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < _options.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_options.CommandTimeout);
                var result = await _connection.SendAsync(command, cts.Token).ConfigureAwait(false);

                /* THE REPLY HAS TO BE THE ONE WE ASKED FOR. Seen in production: `BanList`
                   answered with a ServerInfo document, and the same command answered with a
                   RefreshList document half an hour later - both "Successful": true, and both
                   the reply to background traffic on the shared connection.

                   The gate serialises the socket and an unsettled exchange drops it, so this
                   should be unreachable from our side. It fires anyway because "our transport
                   is correct" is not the same claim as "the answer is ours", and the second is
                   the only one a caller cares about. A moderator reading a roster and
                   believing it is a ban list is a worse failure than a refused command.

                   The exchange itself looked perfect - complete, well formed, successful - so
                   the connection layer had no reason to drop the socket and did not. If the
                   server's replies are offset then every later command on this session is
                   wrong too, so the session is reset here before the retry. */
                if (Mismatch(command, result) is { } mismatch)
                {
                    await _connection.ResetAsync(ct).ConfigureAwait(false);
                    throw mismatch;
                }

                /* Anything that is not a pure read may change who is on the server, so drop
                   the cached reads once it lands. Otherwise a kick could be followed by a
                   roster that still lists the player. */
                if (!IsReadOnly(command)) _readCache.Clear();
                return result;
            }
            catch (RconAuthException)
            {
                throw;   // a wrong password will still be wrong on the third attempt
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // the CALLER cancelled - not a transport failure
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == _options.MaxAttempts - 1) break;
                await Task.Delay(Backoff.Delay(attempt), ct).ConfigureAwait(false);
            }
        }
        /* The REASON, not just the count. "failed after 3 attempts" is the shape of every
           RCON failure and tells nobody which one happened; the last exception's message is
           the part that distinguishes a refused password from a closed port from a reply that
           belonged to another command. */
        throw new RconException(
            $"{_options.Name}: \"{Verb(command)}\" failed after {_options.MaxAttempts} attempt(s): {last?.Message}",
            last!);
    }

    /// <summary>
    /// The reply's own account of which command it answers, or null when it does not say.
    /// </summary>
    /// <remarks>
    /// ABSENT IS NOT A MISMATCH. Plenty of Pavlov replies carry no Command field, and some are
    /// not JSON at all - failures come back as bare text. Refusing those would refuse most of
    /// the command surface to catch a fault that, by definition, cannot be detected in them.
    /// Only a reply that names a DIFFERENT command is rejected.
    /// </remarks>
    private static MismatchedReplyException? Mismatch(string command, string reply)
    {
        if (!RconReply.TryParse(reply, out var document) || document is null) return null;

        using (document)
        {
            if (RconReply.Text(document.RootElement, "Command") is not { } answered) return null;

            var asked = Verb(command);
            return string.Equals(asked, answered, StringComparison.OrdinalIgnoreCase)
                ? null
                : new MismatchedReplyException(asked, answered);
        }
    }

    /// <summary>Drop cached reads. Exposed so a caller that knows state changed can force a refresh.</summary>
    public void InvalidateReads() => _readCache.Clear();

    /// <remarks>
    /// THE SWEEP IS RATE LIMITED, not run per send. It used to fire on every call once the
    /// map passed 200 entries, so a busy server paid a full O(n) scan on the RCON hot path
    /// for every command - and the entries a scan removes are exactly the ones the next scan
    /// a minute later would have removed, so the extra passes bought nothing.
    ///
    /// The CompareExchange is what keeps it to one sweeper: several threads can pass the
    /// elapsed check together, and only the one that wins the exchange does the work.
    /// </remarks>
    private void NoteIssued(string command)
    {
        var now = DateTimeOffset.UtcNow;
        _issued[command.Trim()] = now;

        if (_issued.Count <= IssuedSoftCap) return;

        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < IssuedRetention.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last) return;

        foreach (var (k, at) in _issued)
            if (now - at > IssuedRetention) _issued.TryRemove(k, out _);
    }

    /// <summary>
    /// True when this client sent that exact command recently. Pavlov logs the bot's own
    /// RCON traffic alongside everyone else's, so the log-derived audit needs a way to
    /// tell them apart. Deliberately does not consume the entry: one command fanned out
    /// to several servers appears in the log more than once.
    /// </summary>
    public bool WasIssuedByBot(string command)
    {
        if (!_issued.TryGetValue(command.Trim(), out var at)) return false;
        if (DateTimeOffset.UtcNow - at > IssuedRetention) { _issued.TryRemove(command.Trim(), out _); return false; }
        return true;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

/// <summary>Exponential backoff with full jitter.</summary>
public static class Backoff
{
    public static TimeSpan Delay(int attempt, int baseMs = 500, int maxMs = 30_000)
    {
        var ceiling = Math.Min(maxMs, baseMs * (1 << Math.Min(attempt, 16)));
        // 50-100% of the ceiling: spreads callers that would otherwise return in lockstep.
        return TimeSpan.FromMilliseconds(ceiling * (0.5 + Random.Shared.NextDouble() * 0.5));
    }
}
