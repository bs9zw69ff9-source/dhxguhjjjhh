using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Discord;

/// <summary>
/// <see cref="IGuildDirectory"/> over the live gateway.
/// </summary>
/// <remarks>
/// Thin on purpose, like <see cref="GatewayAutoPostTarget"/> and
/// <see cref="GatewayChannelRenamer"/>: nothing here is worth testing except against a real
/// Discord, so the parts that are - rendering the list, deciding what to do when an invite cannot
/// be made - live on the other side of <see cref="IGuildDirectory"/>.
///
/// THE GATEWAY IS RESOLVED ON USE, NOT INJECTED. <see cref="DiscordGateway"/> is built from every
/// <see cref="ISlashCommand"/>, so anything a command depends on that in turn needs the gateway
/// closes a cycle the container cannot build - and, when the registration is a factory, deadlocks
/// instead of saying so. See <see cref="GatewayChannelRenamer"/>, which documents the same trap.
/// </remarks>
public sealed class GatewayGuildDirectory(IServiceProvider services, ILogger<GatewayGuildDirectory> logger)
    : IGuildDirectory
{
    private DiscordGateway? _gateway;

    private DiscordSocketClient Client => (_gateway ??= services.GetRequiredService<DiscordGateway>()).Client;

    public async Task<IReadOnlyList<GuildInvite>> InviteToEveryGuildAsync(
        TimeSpan maxAge, int maxUses, CancellationToken ct)
    {
        var invites = new List<GuildInvite>();

        foreach (var guild in Client.Guilds.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var (url, problem) = await InviteToAsync(guild, maxAge, maxUses).ConfigureAwait(false);
            invites.Add(new GuildInvite(guild.Id, guild.Name, guild.MemberCount, url, problem));
        }

        return invites;
    }

    /// <summary>An invite to one guild, or the reason there is none.</summary>
    /// <remarks>
    /// A FRESH, BOUNDED, SINGLE-USE INVITE. <c>isUnique</c> forces a new one rather than handing
    /// back an existing permanent link, so what is sent is only ever this request's - it expires
    /// on its own and cannot be passed around afterwards. Creating one needs Create Invite on a
    /// channel the bot can also see, which plenty of guilds do not grant; that is reported rather
    /// than treated as an error.
    /// </remarks>
    private async Task<(string? Url, string? Problem)> InviteToAsync(SocketGuild guild, TimeSpan maxAge, int maxUses)
    {
        var me = guild.CurrentUser;
        if (me is null) return (null, "the bot's own membership is not cached yet - try again in a moment");

        var channel = guild.TextChannels
            .Where(c =>
            {
                var permissions = me.GetPermissions(c);
                return permissions.ViewChannel && permissions.CreateInstantInvite;
            })
            .OrderBy(c => c.Position)
            .FirstOrDefault();

        if (channel is null)
            return (null, "no channel here lets the bot create an invite (needs Create Invite)");

        try
        {
            var invite = await channel.CreateInviteAsync(
                maxAge: (int)maxAge.TotalSeconds,
                maxUses: maxUses,
                isTemporary: false,
                isUnique: true).ConfigureAwait(false);

            return (invite.Url, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not create an invite for {Guild}: {Message}", guild.Id, ex.Message);
            return (null, ex.Message);
        }
    }

    public async Task<bool> SendDirectMessageAsync(ulong userId, Embed embed, CancellationToken ct)
    {
        try
        {
            // Cache first, then REST - the recipient is not necessarily in any guild the bot can
            // see, so GetUser alone would come back empty on a perfectly reachable account.
            var user = Client.GetUser(userId) as IUser
                       ?? await Client.Rest.GetUserAsync(userId).ConfigureAwait(false);

            if (user is null)
            {
                logger.LogWarning("Could not resolve user {User} to send a DM", userId);
                return false;
            }

            await user.SendMessageAsync(embed: embed, options: new RequestOptions { CancelToken = ct })
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Closed DMs are the ordinary case here, not a fault worth an error-level log.
            logger.LogWarning("Could not DM {User}: {Message}", userId, ex.Message);
            return false;
        }
    }
}
