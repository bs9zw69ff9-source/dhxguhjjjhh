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
    /// <summary>
    /// Edit the board, and say which KIND of failure it was if it did not work.
    /// </summary>
    /// <remarks>
    /// THE DUPLICATE-LEADERBOARD BUG LIVED HERE. This returned a bool, and every failure
    /// returned false - which the caller read as "deleted, post a fresh one". A channel that
    /// had not landed in the socket cache yet, a 500, a rate limit and a websocket blip were
    /// all indistinguishable from a message somebody had actually removed, so the board got
    /// re-posted while the original was still sitting in the channel. Two boards, the older
    /// one orphaned and updated by nobody, and no log line anywhere: the catch did not even
    /// log, so the only evidence was the duplicate itself.
    ///
    /// ONLY A RESOLVED CHANNEL CAN REPORT A MISSING MESSAGE. If the channel lookup fails we
    /// know nothing at all about the message, so that is <see cref="AutoPostEdit.Failed"/> -
    /// which is the specific case that was firing on every reconnect.
    ///
    /// A 403 IS ALSO "Failed", NOT "Missing". Re-posting into a channel the bot cannot write
    /// to fails too, so treating it as gone would just add a warning per cycle forever.
    /// </remarks>
    public async Task<AutoPostEdit> EditAsync(ulong channelId, ulong messageId, Embed embed, MessageComponent? components, CancellationToken ct)
    {
        try
        {
            if (await gateway.GetChannelAsync(channelId).ConfigureAwait(false) is not IMessageChannel channel)
            {
                logger.LogWarning(
                    "Channel {Channel} could not be resolved, so the board there was left as it is. " +
                    "If this persists, check the id and that the bot can view the channel", channelId);
                return AutoPostEdit.Failed;
            }

            // The channel resolved and the message is not in it. THIS is the deletion signal.
            if (await channel.GetMessageAsync(messageId).ConfigureAwait(false) is not IUserMessage message)
                return AutoPostEdit.Missing;

            await message.ModifyAsync(m =>
            {
                m.Embed = embed;
                m.AllowedMentions = AllowedMentions.None;
                // Re-applied every edit. Leaving this unset strips the buttons, so the
                // verification panel would lose its Verify button on the first refresh.
                if (components is not null) m.Components = components;
            }).ConfigureAwait(false);
            return AutoPostEdit.Edited;
        }
        catch (HttpException ex) when (
            ex.HttpCode == System.Net.HttpStatusCode.NotFound ||
            ex.DiscordCode == DiscordErrorCode.UnknownMessage)
        {
            // Discord said the message is not there. The only answer that earns a re-post.
            return AutoPostEdit.Missing;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not edit the board message {Message} in channel {Channel} - leaving it in place",
                messageId, channelId);
            return AutoPostEdit.Failed;
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
