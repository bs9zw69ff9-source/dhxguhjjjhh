using Discord;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Moderation;

namespace PavlovBot.Host.Discord;

/// <summary>
/// Staff actions posted into a Discord channel, by id.
/// </summary>
/// <remarks>
/// The channel counterpart to <c>STAFF_WEBHOOK_URL</c>. Both can be configured at once and
/// they are not redundant: a webhook posts under its own name and works without the gateway
/// being connected, and a channel id posts as the bot and can be moved without reissuing a
/// URL. Whichever an operator already has set up is the one they should be able to use.
///
/// BANS GO SOMEWHERE OF THEIR OWN. <c>BAN_LOG_CHANNEL</c> and <c>MOD_LOG_CHANNEL</c> are
/// separate settings even when they point at the same channel, because the question "what
/// bans have we issued" is asked far more often than "what has staff done", and separating
/// them later should be a config change rather than a code change.
/// </remarks>
public sealed class ChannelStaffLog(
    IAutoPostTarget target,
    ulong? modLogChannel,
    ulong? banLogChannel,
    ILogger<ChannelStaffLog> logger) : IStaffLogSink
{
    /// <summary>True when at least one channel is configured.</summary>
    public bool Enabled => modLogChannel is not null || banLogChannel is not null;

    /// <summary>
    /// Whether an action belongs in the ban log rather than the general mod log.
    /// </summary>
    /// <remarks>
    /// By SUBSTRING rather than a fixed list, so a ban variant added later routes correctly
    /// without anybody remembering to extend this. Today that covers permban, tempban,
    /// unban, autoban and vpnban; nothing else the bot records contains the word.
    ///
    /// The cost of being wrong is a line in the wrong channel, which is why a substring is
    /// an acceptable rule here and would not be if it gated a permission.
    /// </remarks>
    internal static bool IsBanAction(string action) =>
        action.Contains("ban", StringComparison.OrdinalIgnoreCase);

    /// <summary>Which channel an action goes to, or null when neither is configured.</summary>
    internal static ulong? ChannelFor(string action, ulong? modLog, ulong? banLog)
    {
        /* FALLS BACK TO THE OTHER ONE rather than dropping the line. Setting only
           MOD_LOG_CHANNEL is a reasonable way to say "everything in one place", and a ban
           silently going nowhere because the ban channel was left unset is the opposite of
           what that operator asked for. */
        var preferred = IsBanAction(action) ? banLog : modLog;
        return preferred ?? (IsBanAction(action) ? modLog : banLog);
    }

    public async Task PostAsync(ModAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (ChannelFor(action.Action, modLogChannel, banLogChannel) is not { } channelId) return;

        try
        {
            await target.SendAsync(channelId, Build(action), components: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* NEVER RETHROWN. The audit record is already written and cannot be un-written;
               turning a delivery problem into a lost ban would be the worst trade available.
               Logged rather than swallowed, so a channel the bot cannot post in is findable. */
            logger.LogWarning(ex, "Could not post {Action} to the staff log channel {Channel}",
                action.Action, channelId);
        }
    }

    /// <summary>
    /// The embed one action becomes.
    /// </summary>
    /// <remarks>
    /// PURE and internal so the shape is pinned by a test. This is the record somebody
    /// scrolls back through during an argument about a ban, and every field in it is
    /// attacker-influenced somewhere: a player name, a reason typed by staff, an action name
    /// assembled from a config panel key.
    /// </remarks>
    internal static Embed Build(ModAction action)
    {
        var kind = Sanitize.Message(action.Action).ToUpperInvariant();

        var embed = IsBanAction(action.Action)
            ? action.Action.StartsWith("un", StringComparison.OrdinalIgnoreCase)
                ? Theme.Success($"{Theme.Ok} {kind}")
                : Theme.Punishment($"{Theme.Deny} {kind}")
            : Theme.Notice($"{Theme.Dot} {kind}");

        /* Inline code around the names so a player called "everyone" cannot be mistaken for
           a mention, and AllowedMentions.None on the send so it could not ping even if it
           were. */
        embed.AddField("Target", $"`{Sanitize.Code(action.Player)}`", inline: true);
        embed.AddField("Staff", $"`{Sanitize.Code(action.Moderator)}`", inline: true);

        if (action.Reason is { Length: > 0 } reason)
            embed.AddField("Reason", Sanitize.Message(reason));

        return embed.Brand($"{EasternTime.Stamp(action.At)} Eastern").Build();
    }
}
