using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Discord;

/// <summary>
/// <see cref="IAutoPostTarget"/> over the live gateway.
/// </summary>
/// <remarks>
/// Thin on purpose. Everything worth testing about auto-posting - the edit-in-place logic,
/// the serialisation, the re-post after a delete - lives in <see cref="AutoPost"/> behind
/// this interface, so it tests without a gateway connection. What is left here is only the
/// Discord API calls, which cannot be tested without one anyway.
/// </remarks>
public sealed class GatewayAutoPostTarget(DiscordGateway gateway, ILogger<GatewayAutoPostTarget> logger) : IAutoPostTarget
{
    public async Task<bool> TryEditAsync(ulong channelId, ulong messageId, Embed embed, MessageComponent? components, CancellationToken ct)
    {
        try
        {
            if (await gateway.GetChannelAsync(channelId).ConfigureAwait(false) is not IMessageChannel channel) return false;
            if (await channel.GetMessageAsync(messageId).ConfigureAwait(false) is not IUserMessage message) return false;

            await message.ModifyAsync(m =>
            {
                m.Embed = embed;
                m.AllowedMentions = AllowedMentions.None;
                // Re-applied every edit. Leaving this unset strips the buttons, so the
                // verification panel would lose its Verify button on the first refresh.
                if (components is not null) m.Components = components;
            }).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deleted, or the bot lost access. Either way the caller posts a fresh one -
            // which is the recovery, not a failure.
            return false;
        }
    }

    public async Task<ulong?> SendAsync(ulong channelId, Embed embed, MessageComponent? components, CancellationToken ct)
    {
        /* EVERY failure here says why. This used to return null silently, so the caller
           could only log "could not be posted" - which is the same message for a wrong
           channel id, a missing Send Messages permission, and a gateway that has not
           finished connecting. Three different fixes, one indistinguishable symptom. */
        try
        {
            if (await gateway.GetChannelAsync(channelId).ConfigureAwait(false) is not IMessageChannel channel)
            {
                logger.LogWarning(
                    "Channel {Channel} is not visible to the bot. Either the id is wrong, the bot is not in " +
                    "that server, or it cannot view the channel", channelId);
                return null;
            }

            var message = await channel.SendMessageAsync(embed: embed, components: components,
                allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            return message.Id;
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            logger.LogWarning(
                "Missing permission to post in channel {Channel}. The bot needs View Channel, " +
                "Send Messages and Embed Links there", channelId);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not post to channel {Channel}", channelId);
            return null;
        }
    }
}
