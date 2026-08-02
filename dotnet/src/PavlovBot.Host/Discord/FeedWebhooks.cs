using System.Collections.Concurrent;
using Discord;
using Discord.Webhook;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Observability;

namespace PavlovBot.Host.Discord;

/// <summary>
/// The plain-text feeds: joins, leaves, kills and money.
/// </summary>
/// <remarks>
/// PLAIN TEXT, NOT EMBEDS, deliberately. These are high-volume append-only logs that people
/// scroll and search. An embed per line turns a hundred joins into a wall nobody can read,
/// and Discord's search does not index embed bodies - so a moderator asking "when did this
/// player last connect" would get nothing.
///
/// THE FIRST POST OF EACH FEED IS CONFIRMED IN THE APPLICATION LOG. A webhook that silently
/// does nothing - deleted channel, revoked URL, typo in the .env - is indistinguishable
/// from a quiet server, and that mistake cost real time before the confirmation existed.
///
/// A webhook URL is a CREDENTIAL: anyone holding it can post as the bot in that channel.
/// They live in .env, never in the database, and never in anything the bot prints.
/// </remarks>
public sealed class FeedWebhooks : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DiscordWebhookClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _urls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _confirmed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _status = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);
    private readonly ILogger<FeedWebhooks> _logger;
    private readonly MetricsRegistry _metrics;
    private readonly TimeProvider _time;

    /// <summary>How long to wait before trying to open a failed webhook again.</summary>
    private static readonly TimeSpan ReopenAfter = TimeSpan.FromMinutes(1);

    public FeedWebhooks(ILogger<FeedWebhooks> logger, MetricsRegistry metrics, TimeProvider? time = null)
    {
        _logger = logger;
        _metrics = metrics;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Register a feed. An unset URL disables it silently - that is a choice, not a fault.
    /// </summary>
    /// <remarks>
    /// NO NETWORK HERE, and that is the fix for a feed that stayed dead until a restart.
    /// <c>new DiscordWebhookClient(url)</c> performs a BLOCKING HTTP call and throws if the
    /// webhook cannot be fetched - so a webhook that had been rotated or deleted, or simply
    /// a moment where Discord was unreachable as the bot booted, permanently disabled that
    /// feed for the lifetime of the process. It logged once, at startup, and every later
    /// post returned silently.
    ///
    /// The URL is stored and the client opened on first use, retried on a timer. A feed
    /// therefore recovers on its own once the webhook is reachable again.
    /// </remarks>
    public void Register(string label, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _status[label] = "not configured";
            _logger.LogDebug("{Label} webhook is not configured - that feed is off", label);
            return;
        }

        /* Syntax IS checked now, because a typo in .env is a configuration error the owner
           can fix immediately, and it costs nothing to say so at startup. */
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) ||
            !parsed.AbsolutePath.Contains("/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            _status[label] = "invalid URL - not a Discord webhook address";
            _logger.LogError(
                "{Label} webhook URL does not look like a Discord webhook (expected .../api/webhooks/<id>/<token>). " +
                "That feed will do nothing", label);
            return;
        }

        _urls[label] = url.Trim();
        _status[label] = "configured, nothing posted yet";
    }

    public IReadOnlyDictionary<string, string> Status => _status;

    /// <summary>True when a URL is set for this feed, whether or not it is currently open.</summary>
    public bool IsConfigured(string label) => _urls.ContainsKey(label);

    /// <summary>
    /// The open client for a feed, opening it if this is the first use.
    /// </summary>
    /// <remarks>
    /// Returns null when the webhook cannot be opened, and does not retry more than once a
    /// minute - the call is a blocking round trip, and a dead webhook on a busy feed would
    /// otherwise add most of a second to every join.
    /// </remarks>
    private DiscordWebhookClient? Client(string label)
    {
        if (_clients.TryGetValue(label, out var open)) return open;
        if (!_urls.TryGetValue(label, out var url)) return null;

        var now = _time.GetUtcNow();
        if (_retryAfter.TryGetValue(label, out var until) && now < until) return null;

        try
        {
            var client = new DiscordWebhookClient(url);
            _clients[label] = client;
            _retryAfter.TryRemove(label, out _);
            _status[label] = "open, nothing posted yet";
            return client;
        }
        catch (Exception ex)
        {
            _retryAfter[label] = now + ReopenAfter;

            /* Named, because the two causes need different fixes and look identical from
               here: a webhook that was deleted or rotated needs a new URL in .env, and one
               that is merely unreachable fixes itself. */
            var advice = ex is InvalidOperationException
                ? "the webhook no longer exists - it was probably deleted, or the channel was. Set a new URL in .env"
                : "could not reach Discord - will try again shortly";

            _status[label] = $"not delivering: {advice}";
            _logger.LogError("{Label} webhook could not be opened: {Message}. {Advice}", label, ex.Message, advice);
            _metrics.Increment("feed_errors_total", MetricLabels.Of("feed", label), help: "Feed posts that failed");
            return null;
        }
    }

    /// <summary>Post one line. Never throws - a feed failure must not fail the thing being logged.</summary>
    public async Task PostAsync(string label, string content, CancellationToken ct = default)
    {
        if (content.Length == 0 || Client(label) is not { } client) return;

        try
        {
            // Discord's hard message limit. Truncating beats the whole post being rejected.
            var text = content.Length > 1900 ? content[..1900] + " …" : content;

            await client.SendMessageAsync(text,
                // Nothing in a feed should ever ping. Player names are attacker-controlled,
                // and one containing @everyone would notify the server from inside a log line.
                allowedMentions: AllowedMentions.None).ConfigureAwait(false);

            _metrics.Increment("feed_posts_total", MetricLabels.Of("feed", label), help: "Feed lines posted");

            if (_confirmed.TryAdd(label, true))
            {
                _status[label] = "delivering";
                _logger.LogInformation("{Label} feed confirmed - first line delivered", label);
            }
        }
        catch (Exception ex)
        {
            Failed(label, ex);
        }
    }

    /// <summary>
    /// Record a failed post, and close the client so the next attempt reopens it.
    /// </summary>
    /// <remarks>
    /// A webhook deleted while the bot is running leaves an open client that fails every
    /// post forever. Dropping it means the retry timer applies and the feed recovers by
    /// itself if the URL is put back.
    /// </remarks>
    private void Failed(string label, Exception ex)
    {
        _status[label] = $"failing: {ex.Message}";
        _metrics.Increment("feed_errors_total", MetricLabels.Of("feed", label), help: "Feed posts that failed");
        _logger.LogWarning("{Label} feed post failed: {Message}", label, ex.Message);

        if (_clients.TryRemove(label, out var dead))
        {
            dead.Dispose();
            _retryAfter[label] = _time.GetUtcNow() + ReopenAfter;
        }
    }

    /// <summary>
    /// Post an embed. Only the connect feed uses this - see <see cref="ConnectCard"/>.
    /// </summary>
    public async Task PostEmbedAsync(string label, Embed embed, CancellationToken ct = default)
    {
        if (Client(label) is not { } client) return;

        try
        {
            await client.SendMessageAsync(embeds: [embed], allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            _metrics.Increment("feed_posts_total", MetricLabels.Of("feed", label), help: "Feed lines posted");

            if (_confirmed.TryAdd(label, true))
            {
                _status[label] = "delivering";
                _logger.LogInformation("{Label} feed confirmed - first line delivered", label);
            }
        }
        catch (Exception ex)
        {
            Failed(label, ex);
        }

        _ = ct;
    }

    // ---- the feeds ----

    /// <summary>
    /// The plain join/leave log. NO ADDRESSES, NO ACCOUNT IDS - safe in a public channel.
    /// </summary>
    public const string Join = "join";

    /// <summary>
    /// The connection feed, WITH ADDRESSES. A private admin channel only.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="Join"/>, and the separation is the point. An address is
    /// personal data that locates somebody's home; the join log is something servers put in
    /// a public channel. Collapsing the two - which this bot did during the port, by reading
    /// CONNECT_WEBHOOK_URL into the join feed - either publishes addresses to a public
    /// channel or silences the public log, depending on which URL the owner set.
    /// </remarks>
    public const string Connect = "connect";

    public const string Kill = "kill";
    public const string Money = "money";

    private static string Stamp(DateTimeOffset at) => EasternTime.Stamp(at);

    /* The line builders are separate from the posting so they can be tested for what they
       DO NOT contain. "The public feed never carries an address" is the property the whole
       Join/Connect split exists to guarantee, and it is not observable through PostAsync -
       which silently does nothing when no webhook is configured. */

    /// <summary>
    /// The public line. Deliberately carries nothing but a name, a server and a time.
    /// </summary>
    /// <remarks>
    /// The wording is the Node bot's, to the character: "[stamp] Name joined Server 1". This
    /// is the feed people actually read, and the port had changed it to "[stamp] JOIN name" -
    /// which searches differently, sorts differently by eye, and dropped which server the
    /// player was on entirely.
    /// </remarks>
    public static string JoinLine(string name, string? server, DateTimeOffset at) =>
        $"[{Stamp(at)}] {Sanitize.Message(name)} joined {Where(server)}";

    public static string LeaveLine(string name, string? server, DateTimeOffset at) =>
        $"[{Stamp(at)}] {Sanitize.Message(name)} left {Where(server)}";

    /// <summary>An unknown server is named unspecifically, never guessed at.</summary>
    private static string Where(string? server) =>
        string.IsNullOrWhiteSpace(server) ? "the server" : Sanitize.Message(server);

    public static string ConnectLine(string name, string? ip, string? location, string? verdict, DateTimeOffset at)
    {
        var parts = new List<string> { $"[{Stamp(at)}] JOIN  {Sanitize.Message(name)}" };
        if (ip is { Length: > 0 }) parts.Add($"ip={ip}");
        if (location is { Length: > 0 }) parts.Add(location);
        if (verdict is { Length: > 0 }) parts.Add($"vpn={verdict}");
        return string.Join("  |  ", parts);
    }

    public Task PostJoinAsync(string name, string? server, DateTimeOffset at, CancellationToken ct = default) =>
        PostAsync(Join, JoinLine(name, server, at), ct);

    /// <param name="verdict">The VPN verdict word, when one is known.</param>
    public Task PostConnectAsync(string name, string? ip, string? location, string? verdict, DateTimeOffset at, CancellationToken ct = default) =>
        PostAsync(Connect, ConnectLine(name, ip, location, verdict, at), ct);

    public Task PostLeaveAsync(string name, string? server, DateTimeOffset at, CancellationToken ct = default) =>
        PostAsync(Join, LeaveLine(name, server, at), ct);

    public Task PostKillAsync(string? killer, string killed, string? weapon, DateTimeOffset at, CancellationToken ct = default)
    {
        // A kill with no killer is the world - fall damage, an unowned explosion. Naming it
        // "unknown" reads as a bug; naming it "the world" reads as what happened.
        var by = string.IsNullOrEmpty(killer) ? "the world" : Sanitize.Message(killer);
        var line = $"[{Stamp(at)}] KILL  {by} → {Sanitize.Message(killed)}";
        if (weapon is { Length: > 0 }) line += $"  ({Sanitize.Message(weapon)})";
        return PostAsync(Kill, line, ct);
    }

    /// <param name="changes">Player -> delta. Only players whose balance actually moved.</param>
    public Task PostMoneyAsync(IReadOnlyCollection<(string Player, long Delta)> changes, DateTimeOffset at, CancellationToken ct = default)
    {
        if (changes.Count == 0) return Task.CompletedTask;

        var lines = changes.Select(c =>
            $"{(c.Delta > 0 ? "+" : "-")}{Math.Abs(c.Delta).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"))} to {Sanitize.Message(c.Player)}");

        return PostAsync(Money, $"[{Stamp(at)}]\n{string.Join("\n", lines)}", ct);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
        return ValueTask.CompletedTask;
    }
}
