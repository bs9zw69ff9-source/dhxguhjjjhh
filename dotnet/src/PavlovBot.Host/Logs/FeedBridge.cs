using Microsoft.Extensions.Logging;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Rcon;
using PavlovBot.Core.Evasion;
using PavlovBot.Core.Vpn;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Vpn;

namespace PavlovBot.Host.Logs;

/// <summary>
/// Connects what the log reader SEES to what the feeds SAY.
/// </summary>
/// <remarks>
/// This is the wire that was missing. <see cref="IpTrackingService"/> raised
/// <c>Joined</c> and <c>Kill</c> for every line it parsed, and nothing subscribed - so the
/// connection feed, the join log and the kill log were complete, tested, and never called
/// by anything. The bot looked healthy: the log tailer ran, the registry filled up, and the
/// three webhooks sat silent because no code path reached them.
///
/// LEAVES COME FROM THE ROSTER, not the log. Pavlov's log has no disconnect line this bot
/// can rely on, so a player who vanishes between two RCON sweeps is a leave. That makes
/// leave times accurate only to the sweep interval, which is the honest limit of what the
/// server tells us.
/// </remarks>
public sealed class FeedBridge
{
    private readonly FeedWebhooks _feeds;
    private readonly RconRegistry _rcon;
    private readonly VpnScreeningService? _vpn;
    private readonly IpTrackingService _tracking;
    private readonly IMasterNames? _masters;
    private readonly ILogger<FeedBridge> _logger;

    /// <summary>Who was online at the last sweep, and when we first saw them.</summary>
    private Dictionary<string, DateTimeOffset> _online = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False until the first sweep has completed.</summary>
    private bool _primed;

    public FeedBridge(
        IpTrackingService tracking,
        FeedWebhooks feeds,
        RconRegistry rcon,
        ILogger<FeedBridge> logger,
        VpnScreeningService? vpn = null,
        IMasterNames? masters = null)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        _tracking = tracking;
        _masters = masters;
        _feeds = feeds;
        _rcon = rcon;
        _vpn = vpn;
        _logger = logger;

        tracking.Joined += OnJoinedAsync;
        tracking.Kill += OnKillAsync;
    }

    private async Task OnJoinedAsync(PlayerJoined join)
    {
        var name = join.Name ?? join.AccountId;

        /* The PUBLIC line first, and unconditionally. It carries no address, so it must not
           be delayed by - or lost to - anything the address lookup does. */
        await Safe(() => _feeds.PostJoinAsync(name, join.At)).ConfigureAwait(false);

        if (!_feeds.IsConfigured(FeedWebhooks.Connect)) return;

        VpnRecord? screening = null;

        /* Screening is cached per address, so this is one lookup the first time an address
           is seen and free afterwards. A guessed address is NOT screened: acting on a
           correlation that might belong to the player who connected a second earlier is how
           the wrong person gets a VPN verdict attached to their name. */
        if (_vpn is not null && join.Confident && join.Ip is { Length: > 0 } address)
        {
            try
            {
                screening = await _vpn.CheckAsync(address).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The address is the point of this feed; the verdict is decoration.
                _logger.LogDebug(ex, "Could not screen {Address} for the connect feed", address);
            }
        }

        var account = _tracking.Account(join.AccountId);
        var flags = _tracking.LoadFlags();
        var flagged = (join.Ip is { Length: > 0 } seen && flags.Ips.Contains(seen)) ||
                      flags.Ids.Contains(join.AccountId) ||
                      (join.Name is { Length: > 0 } who && flags.Names.Contains(who));

        var card = ConnectCard.Build(
            name, join.AccountId, join.Ip, join.Confident,
            server: ServerLabel(join.File),
            account: account,
            alts: _tracking.AltsOf(join.AccountId),
            vpn: screening,
            flagged: flagged,
            master: _masters?.IsMaster(name) ?? false,
            at: join.At);

        await Safe(() => _feeds.PostEmbedAsync(FeedWebhooks.Connect, card)).ConfigureAwait(false);
    }

    /// <summary>Which server a log path belongs to, for the card's Server field.</summary>
    private string ServerLabel(string file)
    {
        /* Matched against the configured RCON servers by name appearing in the path, which
           is how a multi-server install lays its logs out. Falls back to the file name -
           wrong-but-visible beats confidently naming the wrong server. */
        foreach (var server in _rcon.Servers)
            if (file.Contains(server, StringComparison.OrdinalIgnoreCase))
                return server;

        return _rcon.Servers.Count == 1 ? _rcon.Servers.First() : Path.GetFileName(file);
    }

    private Task OnKillAsync(PavlovBot.Core.Logs.KillEvent kill)
    {
        // A kill line is assembled from fragments that can arrive across several log lines,
        // so it carries no timestamp of its own. Now is within a poll interval of the truth.
        if (string.IsNullOrEmpty(kill.Killed)) return Task.CompletedTask;

        return Safe(() => _feeds.PostKillAsync(kill.Killer, kill.Killed, kill.KilledBy, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Emit LEAVE lines for anyone who has dropped off the roster since the last sweep.
    /// </summary>
    /// <remarks>
    /// THE FIRST SWEEP EMITS NOTHING. On startup everyone already online would otherwise
    /// look like a join, and the sweep after a restart would announce a leave for every one
    /// of them - a wall of noise that says nothing true.
    /// </remarks>
    /// <summary>
    /// Who left, given the previous roster and the current one.
    /// </summary>
    /// <remarks>
    /// PURE, and extracted for one reason: the rest of this class needs a live RCON
    /// connection, so the leave logic had no test at all - which is exactly the kind of
    /// place a bug sits unnoticed while joins keep working and nobody can say why.
    /// </remarks>
    /// <param name="primed">False on the first sweep, when nothing has left yet.</param>
    internal static IReadOnlyList<(string Player, TimeSpan Session)> Leavers(
        IReadOnlyDictionary<string, DateTimeOffset> previous,
        IReadOnlyCollection<string> current,
        bool primed,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (!primed) return [];

        var online = current.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return previous
            .Where(entry => !online.Contains(entry.Key))
            .Select(entry => (entry.Key, now - entry.Value))
            .ToList();
    }

    /// <summary>Carry forward each player's first-seen time, so a session length is real.</summary>
    internal static Dictionary<string, DateTimeOffset> NextRoster(
        IReadOnlyDictionary<string, DateTimeOffset> previous,
        IReadOnlyCollection<string> current,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var next = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in current)
            next[player] = previous.TryGetValue(player, out var since) ? since : now;

        return next;
    }

    public async Task TickRosterAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var current = _rcon.AllOnlinePlayers();

        /* An empty roster is ambiguous: an empty server and an unreachable one look
           identical from here. Announcing that everybody left because RCON blipped is worse
           than saying nothing, so an empty result is only believed when at least one
           server's roster was actually refreshed recently. A cache that succeeded once an
           hour ago is not evidence the server is up now. */
        if (current.Count == 0 && !RosterIsFresh())
        {
            _logger.LogDebug("Roster sweep skipped - no recent roster from any server");
            return;
        }

        var leavers = Leavers(_online, current, _primed, now);

        /* Logged at INFORMATION on the first sweep and whenever anybody leaves. "Leaves do
           not work" is otherwise indistinguishable from "the roster is empty", and the two
           have completely different causes - one is this code, the other is RCON. */
        if (!_primed)
        {
            _logger.LogInformation(
                "Leave tracking primed with {Count} player(s) online. Leaves are reported from the " +
                "roster, so an empty roster here means none will ever be reported", current.Count);
        }

        foreach (var (player, session) in leavers)
            await Safe(() => _feeds.PostLeaveAsync(player, session, now)).ConfigureAwait(false);

        if (leavers.Count > 0)
            _logger.LogDebug("Reported {Count} leave(s)", leavers.Count);

        _online = NextRoster(_online, current, now);
        _primed = true;
        _ = ct;
    }

    /// <summary>Generous - it only has to rule out a roster that has gone stale entirely.</summary>
    private static readonly TimeSpan RosterFreshness = TimeSpan.FromMinutes(5);

    private bool RosterIsFresh() =>
        _rcon.Servers.Any(s => DateTimeOffset.UtcNow - _rcon.Roster(s).TakenAt < RosterFreshness);

    /// <summary>A feed failure must never propagate into the thing being logged.</summary>
    private async Task Safe(Func<Task> post)
    {
        try { await post().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Feed post failed"); }
    }
}
