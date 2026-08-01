using Microsoft.Extensions.Logging;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Rcon;
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
        VpnScreeningService? vpn = null)
    {
        ArgumentNullException.ThrowIfNull(tracking);
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

        string? location = null, verdict = null;

        /* Screening is cached per address, so this is one lookup the first time an address
           is seen and free afterwards. A guessed address is NOT screened: acting on a
           correlation that might belong to the player who connected a second earlier is how
           the wrong person gets a VPN verdict attached to their name. */
        if (_vpn is not null && join.Confident && join.Ip is { Length: > 0 } address)
        {
            try
            {
                if (await _vpn.CheckAsync(address).ConfigureAwait(false) is { } record)
                {
                    var place = new[] { record.City, record.Country }.Where(p => !string.IsNullOrEmpty(p)).ToList();
                    location = place.Count > 0 ? string.Join(", ", place) : null;
                    verdict = record.Decision.Label;
                }
            }
            catch (Exception ex)
            {
                // The address is the point of this feed; the verdict is decoration.
                _logger.LogDebug(ex, "Could not screen {Address} for the connect feed", address);
            }
        }

        await Safe(() => _feeds.PostConnectAsync(
            name,
            // An unconfident address is marked rather than hidden. Presenting a guess as
            // fact is what turns a correlation into an accusation.
            join.Confident ? join.Ip : join.Ip is { Length: > 0 } ? $"{join.Ip}?" : null,
            location, verdict, join.At)).ConfigureAwait(false);
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

        var next = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in current)
            next[player] = _online.TryGetValue(player, out var since) ? since : now;

        if (_primed)
        {
            foreach (var (player, since) in _online)
            {
                if (next.ContainsKey(player)) continue;
                await Safe(() => _feeds.PostLeaveAsync(player, now - since, now)).ConfigureAwait(false);
            }
        }

        _online = next;
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
