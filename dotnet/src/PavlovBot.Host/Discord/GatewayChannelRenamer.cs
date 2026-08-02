using Discord;
using Discord.Net;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Discord;

/// <summary>
/// <see cref="IChannelRenamer"/> over the live gateway.
/// </summary>
/// <remarks>
/// Thin on purpose, like <see cref="GatewayAutoPostTarget"/>: everything worth testing about
/// the player-count channels - the names, and the decision not to write an unchanged one -
/// lives behind this interface so it tests without a connection.
/// </remarks>
public sealed class GatewayChannelRenamer(DiscordGateway gateway, ILogger<GatewayChannelRenamer> logger) : IChannelRenamer
{
    public async Task<string?> NameOfAsync(ulong channelId, CancellationToken ct)
    {
        try
        {
            return await gateway.GetChannelAsync(channelId).ConfigureAwait(false) is IChannel channel
                ? channel.Name
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the name of channel {Channel}", channelId);
            return null;
        }
    }

    public async Task<bool> RenameAsync(ulong channelId, string name, CancellationToken ct)
    {
        try
        {
            if (await gateway.GetChannelAsync(channelId).ConfigureAwait(false) is not IGuildChannel channel)
            {
                logger.LogWarning("Channel {Channel} is not a server channel - it cannot be renamed", channelId);
                return false;
            }

            await channel.ModifyAsync(c => c.Name = name).ConfigureAwait(false);
            return true;
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            /* The single most likely failure, and one nothing else would explain: renaming a
               channel needs Manage Channels, which a bot invited for messaging alone does
               not have. Named rather than logged as a generic error. */
            logger.LogWarning(
                "Cannot rename channel {Channel} - the bot needs the Manage Channels permission on it", channelId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Includes the rename rate limit, which Discord.Net waits out rather than
               throwing - so reaching here usually means something else. */
            logger.LogWarning("Could not rename channel {Channel}: {Message}", channelId, ex.Message);
            return false;
        }
    }
}
