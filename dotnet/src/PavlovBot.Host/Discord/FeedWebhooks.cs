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
    private readonly ConcurrentDictionary<string, bool> _confirmed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _status = new(StringComparer.Ordinal);
    private readonly ILogger<FeedWebhooks> _logger;
    private readonly MetricsRegistry _metrics;

    public FeedWebhooks(ILogger<FeedWebhooks> logger, MetricsRegistry metrics)
    {
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Register a feed. An unset URL disables it silently - that is a choice, not a fault.
    /// </summary>
    public void Register(string label, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _status[label] = "not configured";
            _logger.LogDebug("{Label} webhook is not configured - that feed is off", label);
            return;
        }

        try
        {
            _clients[label] = new DiscordWebhookClient(url);
            _status[label] = "configured, nothing posted yet";
        }
        catch (Exception ex)
        {
            /* A malformed URL is a CONFIGURATION error and has to be loud. The silent
               version of this bug is a feed that simply never appears. */
            _status[label] = $"invalid URL: {ex.Message}";
            _logger.LogError("{Label} webhook URL is not usable: {Message}. That feed will do nothing", label, ex.Message);
        }
    }

    public IReadOnlyDictionary<string, string> Status => _status;

    public bool IsConfigured(string label) => _clients.ContainsKey(label);

    /// <summary>Post one line. Never throws - a feed failure must not fail the thing being logged.</summary>
    public async Task PostAsync(string label, string content, CancellationToken ct = default)
    {
        if (!_clients.TryGetValue(label, out var client) || content.Length == 0) return;

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
            _status[label] = $"failing: {ex.Message}";
            _metrics.Increment("feed_errors_total", MetricLabels.Of("feed", label), help: "Feed posts that failed");
            _logger.LogWarning("{Label} feed post failed: {Message}", label, ex.Message);
        }
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

    /// <summary>The public line. Deliberately carries nothing but a name and a time.</summary>
    public static string JoinLine(string name, DateTimeOffset at) =>
        $"[{Stamp(at)}] JOIN  {Sanitize.Message(name)}";

    public static string ConnectLine(string name, string? ip, string? location, string? verdict, DateTimeOffset at)
    {
        var parts = new List<string> { $"[{Stamp(at)}] JOIN  {Sanitize.Message(name)}" };
        if (ip is { Length: > 0 }) parts.Add($"ip={ip}");
        if (location is { Length: > 0 }) parts.Add(location);
        if (verdict is { Length: > 0 }) parts.Add($"vpn={verdict}");
        return string.Join("  |  ", parts);
    }

    public Task PostJoinAsync(string name, DateTimeOffset at, CancellationToken ct = default) =>
        PostAsync(Join, JoinLine(name, at), ct);

    /// <param name="verdict">The VPN verdict word, when one is known.</param>
    public Task PostConnectAsync(string name, string? ip, string? location, string? verdict, DateTimeOffset at, CancellationToken ct = default) =>
        PostAsync(Connect, ConnectLine(name, ip, location, verdict, at), ct);

    public Task PostLeaveAsync(string name, TimeSpan? session, DateTimeOffset at, CancellationToken ct = default)
    {
        var line = $"[{Stamp(at)}] LEAVE {Sanitize.Message(name)}";
        if (session is { } played) line += $"  |  played {Format(played)}";
        return PostAsync(Join, line, ct);
    }

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m";

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
