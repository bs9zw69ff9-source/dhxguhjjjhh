using System.Collections.Concurrent;
using Discord;
using Microsoft.Extensions.Logging;
using PavlovBot.Host.Discord;
using PavlovBot.Core.Vpn;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Vpn;

namespace PavlovBot.Host.Logs;

/// <summary>
/// Connects what the log reader SEES to what the feeds SAY.
/// </summary>
/// <remarks>
/// This is the wire that was missing. <see cref="IpTrackingService"/> raised
/// <c>Joined</c>, <c>Confirmed</c> and <c>Kill</c> for every line it parsed, and nothing
/// subscribed - so the connection feed, the join log and the kill log were complete, tested,
/// and never called by anything.
///
/// LEAVES COME FROM THE DISCONNECT LINE. An earlier version of this class derived them by
/// diffing the RCON roster between sweeps, on the belief that Pavlov's log had no usable
/// disconnect line. It does: the close line carries the address and the account id together,
/// which is the same line <see cref="IpTrackingService.Confirmed"/> is raised for and the
/// same one the Node bot has always used. The roster diff produced no leaves at all on a
/// live server - an empty or stale roster is indistinguishable from an empty one, so the
/// sweep correctly declined to conclude anything, forever.
///
/// THE CONNECTION CARD IS POSTED FROM THE SAME LINE, and that is the point of it. On the
/// login line the address is a timing correlation with a nearby log entry; on the close line
/// the address and the account are printed together and the pairing is certain. A card is
/// worth reading precisely because the address on it is not a guess.
/// </remarks>
public sealed class FeedBridge
{
    private readonly FeedWebhooks _feeds;
    private readonly VpnScreeningService? _vpn;
    private readonly IpTrackingService _tracking;
    private readonly IMasterNames? _masters;
    private readonly VpnResponder? _vpnBans;
    private readonly ServerLabels _servers;
    private readonly ILogger<FeedBridge> _logger;

    /// <summary>
    /// One report per account per disconnect.
    /// </summary>
    /// <remarks>
    /// A single disconnect writes SEVERAL close lines milliseconds apart, so without this
    /// every player leaving would produce three or four identical leave lines and as many
    /// connection cards. Twenty seconds is the Node bot's window and is comfortably longer
    /// than the burst, while still letting somebody who rejoins and drops again be reported.
    /// </remarks>
    private static readonly TimeSpan ConfirmDebounce = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Joins already announced. Pavlov logs a player's login SEVERAL TIMES per connection.
    /// </summary>
    /// <remarks>
    /// The Node bot has always debounced this and the port did not, which is why the join
    /// log shows the same player arriving two and three times in a row with no leave
    /// between - it is one connection, logged repeatedly. Keyed by NAME rather than account
    /// id, because that is what the reader is comparing.
    /// </remarks>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _announced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Logged once, so "no leaves" can be told apart from "no disconnect lines".</summary>
    private bool _sawDisconnect;

    /// <summary>Logged once, so a confirm skipped for want of a name is not silent.</summary>
    private bool _warnedNameless;

    /// <summary>Logged once, so "no connect webhook" is not mistaken for "cards are broken".</summary>
    private bool _warnedNoConnectFeed;

    public FeedBridge(
        IpTrackingService tracking,
        FeedWebhooks feeds,
        ServerLabels servers,
        ILogger<FeedBridge> logger,
        VpnScreeningService? vpn = null,
        IMasterNames? masters = null,
        VpnResponder? vpnBans = null)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        _tracking = tracking;
        _masters = masters;
        _vpnBans = vpnBans;
        _feeds = feeds;
        _servers = servers;
        _vpn = vpn;
        _logger = logger;

        tracking.Joined += OnJoinedAsync;
        tracking.Confirmed += OnConfirmedAsync;
        tracking.Kill += OnKillAsync;
    }

    /// <summary>The public join line. No address, no account id - safe in a public channel.</summary>
    private Task OnJoinedAsync(PlayerJoined join)
    {
        var name = join.Name ?? join.AccountId;

        // One line per arrival, not one per login line. See _announced.
        if (!Report(_announced, name, join.At)) return Task.CompletedTask;

        return Safe(() => _feeds.PostJoinAsync(name, _servers.Of(join.File), join.At));
    }

    /// <summary>
    /// A disconnect: the public leave line, and the connection card with the certain address.
    /// </summary>
    private async Task OnConfirmedAsync(PlayerConfirmed confirmed)
    {
        if (!_sawDisconnect)
        {
            _sawDisconnect = true;
            _logger.LogInformation(
                "First disconnect line seen - leave lines and the connection feed are live");
        }

        /* An account with no recorded name is a partial connection: somebody who was
           dropped before a login line named them. "unknown left Server 1" is noise, and the
           connection card would have nobody on it. The Node bot skips these too. */
        if (confirmed.Name is not { Length: > 0 } name)
        {
            /* Said ONCE, because it is the difference between "leaves are broken" and
               "leaves are working and this account is genuinely anonymous" - and from the
               channel those look identical. */
            if (!_warnedNameless)
            {
                _warnedNameless = true;
                _logger.LogInformation(
                    "Disconnect for {Account} skipped - no name recorded for it yet. This is normal for a " +
                    "player who was already connected when the bot started; if EVERY leave is missing, it " +
                    "means no login line has ever been matched to this account id", confirmed.AccountId);
            }
            return;
        }

        if (!ShouldReport(confirmed.AccountId, confirmed.At)) return;

        var server = _servers.Of(confirmed.File);
        await Safe(() => _feeds.PostLeaveAsync(name, server, confirmed.At)).ConfigureAwait(false);

        if (!_feeds.IsConfigured(FeedWebhooks.Connect))
        {
            /* SAID OUT LOUD, once. "The ip logs don't work" and "no CONNECT_WEBHOOK_URL is
               set" are the same thing from the channel, and this branch returning silently
               is what made them indistinguishable. */
            if (!_warnedNoConnectFeed)
            {
                _warnedNoConnectFeed = true;
                _logger.LogWarning(
                    "A player disconnected and the connection card was not posted: CONNECT_WEBHOOK_URL is not " +
                    "set (or is not a valid webhook address), so the connect feed is off. Leave lines are " +
                    "unaffected - they go to JOIN_WEBHOOK_URL. Run /feeds to see every feed's state");
            }
            return;
        }

        VpnRecord? screening = null;

        // Cached per address, so this is one lookup the first time an address is seen and
        // free afterwards. The address is certain here, so there is nothing to gate on.
        if (_vpn is not null)
        {
            try
            {
                screening = await _vpn.CheckAsync(confirmed.Ip).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The address is the point of this feed; the verdict is decoration.
                _logger.LogDebug(ex, "Could not screen {Address} for the connect feed", confirmed.Ip);
            }
        }

        var flags = _tracking.LoadFlags();
        var flagged = flags.Ips.Contains(confirmed.Ip) ||
                      flags.Ids.Contains(confirmed.AccountId) ||
                      flags.Names.Contains(name);

        /* BUILDING the card is inside the guard, not just posting it. EmbedBuilder.Build()
           validates eagerly and throws, and this call used to sit outside every try in the
           method - so a card Discord would reject took out the whole handler, and because
           this runs inline in the log poll it took the REST OF THAT POLL'S LINES with it:
           the tailer advances its offset before handing the batch over, so they are gone.

           No input is known to make ConnectCard throw, and it now caps itself so none
           should. This is the second of the two braces, and it is here because of the cost
           asymmetry rather than a reproduced failure - a caught exception costs one card,
           an uncaught one costs every line after it in that poll. */
        Embed card;
        try
        {
            card = ConnectCard.Build(
                name, confirmed.AccountId, confirmed.Ip, confidentIp: true,
                server: server,
                account: _tracking.Account(confirmed.AccountId),
                alts: _tracking.AltsOf(confirmed.AccountId),
                vpn: screening,
                flagged: flagged,
                master: _masters?.IsMaster(name) ?? false,
                at: confirmed.At);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not build the connection card for {Player} - Discord would have rejected it. " +
                "The leave line was still posted", name);
            return;
        }

        await Safe(() => _feeds.PostEmbedAsync(FeedWebhooks.Connect, card)).ConfigureAwait(false);

        /* ACT on the verdict, after the card is posted. The screening had no consumer at
           all - it computed a ban decision on every connection and nothing read it - so a
           VPN user connected and played while the log said a ban had been decided on.
           Posted first deliberately: if the ban throws, staff still get the evidence. */
        if (_vpnBans is not null && screening is not null)
            await Safe(() => _vpnBans.RespondAsync(name, screening)).ConfigureAwait(false);
    }

    private bool ShouldReport(string accountId, DateTimeOffset at) => Report(_reported, accountId, at);

    /// <summary>
    /// False while this account is still inside its debounce window; true otherwise, and
    /// then the window starts again.
    /// </summary>
    /// <remarks>
    /// PURE, and extracted for one reason: a debounce that is slightly wrong is invisible.
    /// Too tight and every disconnect posts four identical lines; too loose and a player who
    /// rejoins and drops again is silently never reported. Neither shows up in a code read.
    /// </remarks>
    internal static bool Report(IDictionary<string, DateTimeOffset> reported, string accountId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(reported);

        if (reported.TryGetValue(accountId, out var last) && at - last < ConfirmDebounce) return false;
        reported[accountId] = at;

        // Bounded: without this, a long-running bot accumulates an entry per account seen.
        if (reported.Count > 1000)
        {
            foreach (var stale in reported.Where(e => at - e.Value > ConfirmDebounce).Select(e => e.Key).ToList())
                reported.Remove(stale);
        }

        return true;
    }

    private Task OnKillAsync(PavlovBot.Core.Logs.KillEvent kill)
    {
        // A kill line is assembled from fragments that can arrive across several log lines,
        // so it carries no timestamp of its own. Now is within a poll interval of the truth.
        if (string.IsNullOrEmpty(kill.Killed)) return Task.CompletedTask;

        return Safe(() => _feeds.PostKillAsync(kill.Killer, kill.Killed, kill.KilledBy, DateTimeOffset.UtcNow));
    }

    /// <summary>A feed failure must never propagate into the thing being logged.</summary>
    private async Task Safe(Func<Task> post)
    {
        try { await post().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Feed post failed"); }
    }
}
