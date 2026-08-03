using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;
using PavlovBot.Rcon;
using PavlovBot.Rcon.Protocol;

namespace PavlovBot.Host.Rcon;

/// <summary>One player currently on a server, with the time the roster was taken.</summary>
public sealed record RosterSnapshot(string Server, IReadOnlyList<PavlovPlayer> Players, DateTimeOffset TakenAt)
{
    public static RosterSnapshot Empty(string server) => new(server, [], DateTimeOffset.MinValue);
}

/// <summary>
/// The bot's RCON clients, one per configured server, plus the shared roster cache.
/// </summary>
/// <remarks>
/// Owning the clients in one place is what makes the persistent connection worth having:
/// a client created per command would reconnect per command, which is the Node behaviour
/// the port set out to fix.
///
/// The roster cache exists for the same reason as the read coalescing one layer down, at a
/// different timescale. Coalescing collapses simultaneous reads; this holds the answer
/// across the ~60s between sweeps so a command that needs "who is online" does not pay a
/// round trip on the game thread to find out.
/// </remarks>
public sealed class RconRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, RconClient> _clients;
    private readonly ConcurrentDictionary<string, RosterSnapshot> _rosters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _lastError = new(StringComparer.Ordinal);
    private readonly MetricsRegistry _metrics;
    private readonly ILogger<RconRegistry> _logger;

    /// <summary>A roster older than this is stale - reported, not silently served as current.</summary>
    private static readonly TimeSpan RosterFreshness = TimeSpan.FromSeconds(90);

    public RconRegistry(BotOptions options, MetricsRegistry metrics, ILogger<RconRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _metrics = metrics;
        _logger = logger;

        _clients = options.Servers
            .Select(s => s with { IdleTimeout = HoldOpen(s.IdleTimeout, options.RconHealthInterval) })
            .ToDictionary(s => s.Name, s => new RconClient(s), StringComparer.Ordinal);

        var idle = options.Servers.Count > 0
            ? HoldOpen(options.Servers[0].IdleTimeout, options.RconHealthInterval)
            : TimeSpan.Zero;

        _logger.LogInformation(
            "RCON: {Count} server(s), one connection each, held open - probed every {Probe:0}s, " +
            "recycled only after {Idle:0}s idle",
            _clients.Count, options.RconHealthInterval.TotalSeconds, idle.TotalSeconds);
    }

    /// <summary>
    /// An idle timeout that the health probe is guaranteed to land inside.
    /// </summary>
    /// <remarks>
    /// THE CONNECTION IS ONLY PERSISTENT BY ACCIDENT OTHERWISE. Nothing sends on an idle
    /// session, so what actually keeps it open is the health probe touching it every 60s -
    /// comfortably inside the five-minute recycle. Raise RCON_HEALTH_INTERVAL_MS past that
    /// and every probe finds a stale session, tears it down and reconnects: the bot would
    /// still work, and would be opening a fresh authenticated TCP session per sweep forever
    /// while nothing anywhere said so.
    ///
    /// So the relationship is enforced rather than assumed - the recycle is always at least
    /// three probes wide.
    /// </remarks>
    internal static TimeSpan HoldOpen(TimeSpan configured, TimeSpan probeInterval)
    {
        var floor = probeInterval * 3;
        return configured > floor ? configured : floor;
    }

    public IReadOnlyCollection<string> Servers => _clients.Keys;

    public RconClient? Client(string server) => _clients.GetValueOrDefault(server);

    /// <summary>Send a command to one server, timed and counted.</summary>
    public Task<string> SendAsync(string server, string command, CancellationToken ct = default)
    {
        if (!_clients.TryGetValue(server, out var client))
            throw new InvalidOperationException($"unknown server \"{server}\"");

        var verb = command.Split(' ', 2)[0];
        return _metrics.TimeAsync(
            "rcon_command_duration_ms",
            MetricLabels.Of("server", server, "command", verb),
            () => client.SendAsync(command, ct),
            "RCON command duration in milliseconds");
    }

    /// <summary>
    /// The roster this server last reported. Never throws and never blocks - a caller that
    /// needs the truth right now should ask for it; a caller rendering a list wants the
    /// cheap answer plus the timestamp so it can say how old it is.
    /// </summary>
    public RosterSnapshot Roster(string server) => _rosters.GetValueOrDefault(server) ?? RosterSnapshot.Empty(server);

    /// <summary>Every distinct player name across every server.</summary>
    public IReadOnlyList<string> AllOnlinePlayers() =>
        _rosters.Values
            .SelectMany(r => r.Players)
            .Select(p => p.Name)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Why a server's roster is not being updated, or null while it is.
    /// </summary>
    /// <remarks>
    /// Kept because a frozen roster and a quiet server look IDENTICAL from every surface that
    /// reads one. Every failure path below used to be silent - a throw went to Debug, and an
    /// unparseable or unsuccessful reply just moved to the next server - so a refresh that had
    /// stopped working left the last good snapshot in place forever, and <c>/players</c> went
    /// on presenting it as current.
    /// </remarks>
    private readonly ConcurrentDictionary<string, string> _rosterProblem = new(StringComparer.Ordinal);

    /// <summary>What is wrong with this server's roster feed, or null when nothing is.</summary>
    public string? RosterProblem(string server) => _rosterProblem.GetValueOrDefault(server);

    /// <summary>True when the roster is recent enough to be evidence of anything.</summary>
    public bool RosterIsFresh(string server) =>
        Roster(server) is { TakenAt: var at } && at != DateTimeOffset.MinValue &&
        DateTimeOffset.UtcNow - at <= RosterFreshness;

    /// <summary>Refresh the cached roster for every server.</summary>
    public async Task RefreshRostersAsync(CancellationToken ct)
    {
        foreach (var server in _clients.Keys)
        {
            try
            {
                var raw = await SendAsync(server, "RefreshList", ct).ConfigureAwait(false);

                if (!RconReply.TryParse(raw, out var document) || document is null)
                {
                    Problem(server, "the server's reply to RefreshList could not be parsed");
                    continue;
                }

                using (document)
                {
                    /* Only a reply the server called SUCCESSFUL replaces the cache. An
                       unsuccessful RefreshList is not "nobody is online" - treating it that
                       way empties the roster on every hiccup, and anything keyed on the
                       roster then acts as though the server cleared out. */
                    if (RconReply.Successful(document.RootElement) != true)
                    {
                        Problem(server, "the server refused RefreshList");
                        continue;
                    }

                    var players = RefreshList.Players(document.RootElement);
                    _rosters[server] = new RosterSnapshot(server, players, DateTimeOffset.UtcNow);
                    Recovered(server);

                    _metrics.Gauge("players_online", players.Count, MetricLabels.Of("server", server),
                        "Players currently on a server");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Problem(server, ex.Message);
            }
        }
    }

    /// <summary>
    /// Record a refresh failure, and say so ONCE per outage.
    /// </summary>
    /// <remarks>
    /// Once, because this sweep runs every minute or so and a persistent failure would
    /// otherwise be sixty identical warnings an hour - which is how the previous version came
    /// to be at Debug, where it said nothing at all at the default level. Warn on the way in,
    /// inform on the way out, silent in between.
    /// </remarks>
    private void Problem(string server, string reason)
    {
        if (_rosterProblem.TryGetValue(server, out var existing) && existing == reason)
        {
            _logger.LogDebug("Roster refresh still failing for {Server}: {Reason}", server, reason);
            return;
        }

        _rosterProblem[server] = reason;
        _logger.LogWarning(
            "Roster refresh FAILED for {Server}: {Reason}. The cached roster is now frozen - " +
            "/players will show how old it is, and the player-count channel will not be renamed " +
            "from a stale number", server, reason);
    }

    private void Recovered(string server)
    {
        if (_rosterProblem.TryRemove(server, out _))
            _logger.LogInformation("Roster refresh is working again for {Server}", server);
    }

    /// <summary>Probe every server, recording reachability.</summary>
    public async Task ProbeAllAsync(CancellationToken ct)
    {
        foreach (var server in _clients.Keys)
        {
            try
            {
                await SendAsync(server, "ServerInfo", ct).ConfigureAwait(false);
                _lastError[server] = null;
                _metrics.Gauge("rcon_up", 1, MetricLabels.Of("server", server), "1 when a server answered its last probe");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastError[server] = ex.Message;
                _metrics.Gauge("rcon_up", 0, MetricLabels.Of("server", server));
                _logger.LogWarning("RCON probe failed for {Server}: {Message}", server, ex.Message);
            }
        }
    }

    /// <summary>
    /// One verdict across every server.
    /// </summary>
    /// <remarks>
    /// SOME servers down is DEGRADED, all of them is UNHEALTHY. That distinction is the
    /// whole reason for a three-state health model: one of three Pavlov hosts rebooting is
    /// a normal Tuesday, and paging for it trains people to ignore the pager.
    /// </remarks>
    public Task<HealthResult> HealthAsync(CancellationToken ct = default)
    {
        if (_clients.Count == 0) return Task.FromResult(HealthResult.Unhealthy("no RCON servers configured"));

        var down = _lastError.Where(e => e.Value is not null).ToList();
        var probed = _lastError.Count;

        // Nothing probed yet is UNKNOWN, not healthy. Reporting green before the first
        // probe would make a bot that never reaches its servers look fine for a minute.
        if (probed == 0) return Task.FromResult(HealthResult.Degraded("no RCON probe has completed yet"));

        if (down.Count == 0)
        {
            var stale = _clients.Keys
                .Where(s => Roster(s).TakenAt < DateTimeOffset.UtcNow - RosterFreshness)
                .ToList();
            return Task.FromResult(stale.Count == _clients.Count && _rosters.Count > 0
                ? HealthResult.Degraded("every roster is stale")
                : HealthResult.Healthy($"{_clients.Count} server(s) reachable"));
        }

        var detail = string.Join("; ", down.Select(d => $"{d.Key}: {d.Value}"));
        return Task.FromResult(down.Count >= _clients.Count
            ? HealthResult.Unhealthy(detail)
            : HealthResult.Degraded(detail));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values) await client.DisposeAsync().ConfigureAwait(false);
    }
}
