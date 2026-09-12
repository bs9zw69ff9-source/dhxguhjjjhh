using Discord;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;

namespace PavlovBot.Host.Discord;

/// <summary>
/// Security detections, delivered to somebody's direct messages.
/// </summary>
/// <remarks>
/// A DM RATHER THAN A CHANNEL because of what these are: an evader walking back in, a VPN on
/// a fresh account, one person on four accounts. They happen at three in the morning and the
/// answer is usually "look now", which a channel nobody has open does not produce.
///
/// THE CHANNEL FEEDS ARE NOT REPLACED. Every one of these already went to the connect feed
/// and the log, and still does - this is an additional route for the same detection, so a
/// closed DM costs the notification and never the record.
///
/// A CLOSED DM IS THE ORDINARY FAILURE and it is silent by nature: Discord accepts the call
/// and refuses the delivery, so nothing about the sending side looks wrong. Every attempt is
/// therefore counted, a failure is logged at warning naming the recipient, and
/// <c>/testalert</c> exists to prove the whole path end to end rather than hoping.
/// </remarks>
public sealed class DirectMessageAlerts(
    IGuildDirectory directory,
    IReadOnlyList<ulong> recipients,
    MetricsRegistry metrics,
    ILogger<DirectMessageAlerts> logger) : ISecurityAlertSink
{
    /// <summary>Who gets the alerts. Empty means the feature is configured off.</summary>
    public IReadOnlyList<ulong> Recipients { get; } = recipients ?? [];

    public bool Enabled => Recipients.Count > 0;

    public async Task<IReadOnlyList<AlertDelivery>> PostAsync(SecurityAlert alert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (Recipients.Count == 0) return [];

        Embed card;
        try
        {
            card = Card(alert);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Build() validates eagerly. A card Discord would reject must not cost the ban
            // the caller is in the middle of.
            logger.LogError(ex, "Could not build the {Kind} alert for {Player}", alert.Kind, alert.Player);
            return [];
        }

        var results = new List<AlertDelivery>(Recipients.Count);

        foreach (var user in Recipients)
        {
            bool delivered;
            try
            {
                delivered = await directory.SendDirectMessageAsync(user, card, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not DM the {Kind} alert to {User}", alert.Kind, user);
                results.Add(new AlertDelivery(user, false, ex.Message));
                metrics.Increment("security_alert_dms_total", MetricLabels.Of("outcome", "error"),
                    help: "Security alert direct messages by outcome");
                continue;
            }

            results.Add(new AlertDelivery(user, delivered,
                delivered ? null : "Discord would not deliver it - their DMs are probably closed, " +
                                   "or they share no server with the bot"));

            metrics.Increment("security_alert_dms_total",
                MetricLabels.Of("outcome", delivered ? "delivered" : "refused"),
                help: "Security alert direct messages by outcome");

            if (!delivered)
            {
                /* WARNING, not debug. The whole feature is somebody being told; a recipient
                   who never gets told is the feature not working, and it is invisible from
                   the sending side. */
                logger.LogWarning(
                    "{Kind} alert for {Player} was NOT delivered to {User} - their DMs are closed to the bot, " +
                    "or they share no server with it", alert.Kind, alert.Player, user);
            }
        }

        return results;
    }

    /// <summary>
    /// The alert as one card.
    /// </summary>
    /// <remarks>
    /// PURE, so what an alert actually says is testable without a gateway - which matters
    /// more here than usual, because nobody sees these until the night one fires.
    /// </remarks>
    internal static Embed Card(SecurityAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var who = Sanitize.Message(alert.Player);
        var at = alert.At ?? DateTimeOffset.UtcNow;

        var (glyph, title, colour) = alert.Kind switch
        {
            SecurityAlertKind.Evasion => (Theme.Deny, "Ban evasion", Theme.BanRed),
            SecurityAlertKind.Alts => ("👥", "Alt accounts", Theme.Amber),
            _ => ("🛰️", "VPN or proxy", Theme.Amber),
        };

        var embed = new EmbedBuilder()
            .WithColor(colour)
            .WithTitle(EmbedBudget.Truncate($"{glyph} {title} — {who}", EmbedBudget.TitleLimit))
            .WithDescription(EmbedBudget.Truncate(Sanitize.Message(alert.Headline), 2048));

        var budget = new EmbedBudget(embed, 240);

        // WHAT THE BOT DID, first and always. It is the difference between "go and look" and
        // "this is already handled", and it is the reason to read the rest.
        budget.Add("What happened", Sanitize.Message(alert.Action));

        budget.Add("Account ID", alert.AccountId is { Length: > 0 } id ? $"`{Sanitize.Message(id)}`" : "unknown",
            inline: true);
        budget.Add("Address", alert.Ip is { Length: > 0 } ip ? $"`{ip}`" : "unknown", inline: true);
        budget.Add("Server", alert.Server is { Length: > 0 } server ? Sanitize.Message(server) : "unknown",
            inline: true);

        if (alert.Alts is { Count: > 0 } alts)
        {
            budget.Add($"Other accounts on that address ({alts.Count})",
                string.Join("\n", alts.Take(20).Select(a => $"{Theme.Dot} {Sanitize.Markdown(a)}")));
        }

        /* HOW TO ACT ON IT, because a DM is read away from a keyboard and the commands are
           not obvious from a phone. Kept to the two that answer this alert. */
        budget.Add("Next", alert.Kind switch
        {
            SecurityAlertKind.Evasion => "`/checkban <name>` for the record, `/unban <name>` if it is wrong.",
            SecurityAlertKind.Alts => "`/alts <name>` for the full picture, `/ban <name>` if it is evasion.",
            _ => "`/inspect <name>` for the screening, `/unban <name>` if it is a false positive.",
        });

        return embed.Brand($"Security alert — {EasternTime.Stamp(at)} Eastern").Build();
    }
}
