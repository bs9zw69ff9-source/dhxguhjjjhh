using Discord;
using Discord.WebSocket;

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
public sealed class GatewayAutoPostTarget(DiscordGateway gateway) : IAutoPostTarget
{
    public async Task<bool> TryEditAsync(ulong channelId, ulong messageId, Embed embed, CancellationToken ct)
    {
        try
        {
            if (await gateway.GetChannelAsync(channelId).ConfigureAwait(false) is not IMessageChannel channel) return false;
            if (await channel.GetMessageAsync(messageId).ConfigureAwait(false) is not IUserMessage message) return false;

            await message.ModifyAsync(m =>
            {
                m.Embed = embed;
                m.AllowedMentions = AllowedMentions.None;
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

    public async Task<ulong?> SendAsync(ulong channelId, Embed embed, CancellationToken ct)
    {
        try
        {
            if (await gateway.GetChannelAsync(channelId).ConfigureAwait(false) is not IMessageChannel channel) return null;

            var message = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            return message.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
