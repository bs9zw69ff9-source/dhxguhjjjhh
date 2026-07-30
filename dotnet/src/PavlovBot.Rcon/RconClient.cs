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

    private readonly RconOptions _options;
    private readonly RconConnection _connection;
    private readonly ConcurrentDictionary<string, CacheEntry> _readCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<string>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Commands this client issued recently, so log-derived audit can tell its own traffic apart.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _issued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan IssuedRetention = TimeSpan.FromMinutes(2);

    private sealed record CacheEntry(string Value, DateTimeOffset At);

    public RconClient(RconOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connection = new RconConnection(options);
    }

    public string Name => _options.Name;

    private static string Verb(string command)
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

        /* GetOrAdd can invoke the factory more than once under contention, so the task is
           built eagerly and the loser of the race is discarded. Without this two callers
           could each open an exchange for the same read. */
        return _inFlight.GetOrAdd(key, k => StartRead(k, ct));
    }

    private async Task<string> StartRead(string command, CancellationToken ct)
    {
        try
        {
            var value = await SendUncachedAsync(command, ct).ConfigureAwait(false);
            // Only a SUCCESS is cached. A failure must never be replayed as an answer.
            _readCache[command] = new CacheEntry(value, DateTimeOffset.UtcNow);
            return value;
        }
        finally
        {
            _inFlight.TryRemove(command, out _);
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
        throw new RconException($"{_options.Name}: \"{Verb(command)}\" failed after {_options.MaxAttempts} attempts", last!);
    }

    /// <summary>Drop cached reads. Exposed so a caller that knows state changed can force a refresh.</summary>
    public void InvalidateReads() => _readCache.Clear();

    private void NoteIssued(string command)
    {
        var now = DateTimeOffset.UtcNow;
        _issued[command.Trim()] = now;
        if (_issued.Count > 200)
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
