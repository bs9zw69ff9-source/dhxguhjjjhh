using Discord;
using Discord.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Discord;

/// <summary>
/// <see cref="IChannelRenamer"/> over the live gateway.
/// </summary>
/// <remarks>
/// Thin on purpose, like <see cref="GatewayAutoPostTarget"/>: everything worth testing about
/// the player-count channels - the names, and the decision not to write an unchanged one -
/// lives behind this interface so it tests without a connection.
///
/// THE GATEWAY IS RESOLVED ON USE, NOT INJECTED, and that is not a style choice.
/// <see cref="DiscordGateway"/> takes every <see cref="ISlashCommand"/> and
/// <see cref="IComponentHandler"/> in its constructor, so ANY command that transitively
/// needs the gateway closes a cycle the container refuses to build:
///
///   CountsCommand -> PlayerCountChannels -> IChannelRenamer -> DiscordGateway -> commands
///
/// which fails the whole process at startup with "a circular dependency was detected", not
/// at the point of use. Nothing here needs the gateway until a channel is actually being
/// renamed - long after startup - so resolving it then breaks the cycle without pretending
/// the dependency is not real.
/// </remarks>
public sealed class GatewayChannelRenamer(IServiceProvider services, ILogger<GatewayChannelRenamer> logger) : IChannelRenamer
{
    private DiscordGateway? _gateway;

    private DiscordGateway Gateway => _gateway ??= services.GetRequiredService<DiscordGateway>();

    public async Task<string?> NameOfAsync(ulong channelId, CancellationToken ct)
    {
        try
        {
            return await Gateway.GetChannelAsync(channelId).ConfigureAwait(false) is IChannel channel
                ? channel.Name
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the name of channel {Channel}", channelId);
            return null;
        }
    }

    public async Task<RenameOutcome> RenameAsync(ulong channelId, string name, CancellationToken ct)
    {
        try
        {
            if (await Gateway.GetChannelAsync(channelId).ConfigureAwait(false) is not IGuildChannel channel)
            {
                logger.LogWarning("Channel {Channel} is not a server channel - it cannot be renamed", channelId);
                return RenameOutcome.NotVisible;
            }

            /* BOTH OF THESE ARE LOAD-BEARING, and their absence is what made /counts hang
               forever on a spinner.

               Discord allows two channel renames per ten minutes per channel. Discord.Net's
               default behaviour on hitting that is to WAIT OUT the limit rather than throw -
               so a third rename inside the window blocks for up to ten minutes.

               CancelToken, because without it that wait ignores cancellation entirely. The
               command budget fired, cancelled its token, and nothing was listening: the
               handler never returned, so the dispatcher's "this took too long" reply never
               ran either, and the interaction sat spinning until Discord expired it.

               AlwaysFail, because even cancelling correctly would only replace a ten-minute
               hang with a five-minute one. A rename that cannot happen now should say so
               immediately - the next tick will pick the count up anyway, so there is nothing
               to gain by waiting and a command that answers is worth more than one that is
               eventually right. */
            await channel.ModifyAsync(c => c.Name = name, new RequestOptions
            {
                CancelToken = ct,
                RetryMode = RetryMode.AlwaysFail,
            }).ConfigureAwait(false);

            return RenameOutcome.Renamed;
        }
        catch (RateLimitedException)
        {
            /* Expected, routine, and NOT an error. The background tick spends both renames in
               the window whenever a count is moving, so a manual /counts landing in the same
               window is the normal case rather than a fault. */
            logger.LogInformation(
                "Channel {Channel} is rate limited - Discord allows two renames per ten minutes per " +
                "channel and they are spent. The next tick will apply it", channelId);
            return RenameOutcome.RateLimited;
        }
        catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            /* The single most likely failure, and one nothing else would explain: renaming a
               channel needs Manage Channels, which a bot invited for messaging alone does
               not have. Named rather than logged as a generic error. */
            logger.LogWarning(
                "Cannot rename channel {Channel} - the bot needs the Manage Channels permission on it", channelId);
            return RenameOutcome.Forbidden;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not rename channel {Channel}: {Message}", channelId, ex.Message);
            return RenameOutcome.Failed;
        }
    }
}
