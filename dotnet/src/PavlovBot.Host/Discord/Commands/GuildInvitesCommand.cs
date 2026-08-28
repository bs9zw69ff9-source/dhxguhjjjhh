using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Security;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/guildinvites</c> - DM the super owner an invite to every Discord server the bot is in.
/// </summary>
/// <remarks>
/// NAMED "GUILD", NOT "SERVER", and that is not pedantry: everywhere else in this bot a server is
/// a PAVLOV server, with commands like <c>/serverswitch</c> and <c>/serverinfo</c> to match.
/// Calling this one <c>/serverinvites</c> would put "invite people to a Pavlov server" next to
/// them in the picker, which is not what it does.
///
/// THE RECIPIENT IS <see cref="OwnerGuard.SuperOwnerId"/>, not an option. The whole point is that
/// it goes to the account that owns the bot, and a recipient somebody could type would make this
/// a way to mail a working invite to every guild the bot is in to anyone at all - so it is the
/// compiled-in id, which OwnerGuard already fingerprints and refuses to start without.
///
/// The invites are FRESH, single-use and short-lived. They are the keys to servers other people
/// administer, so what leaves here expires on its own rather than becoming a permanent link
/// sitting in a DM.
/// </remarks>
public sealed class GuildInvitesCommand(
    Access access,
    AuditLog audit,
    IServiceProvider services,
    ILogger<GuildInvitesCommand> logger) : ISlashCommand
{
    public string Name => "guildinvites";

    /// <summary>Ephemeral: it carries working invites, and is the fallback when the DM fails.</summary>
    public bool Ephemeral => true;

    /// <summary>Long enough to act on, short enough that a leaked DM goes stale.</summary>
    internal static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(24);

    /// <summary>One use each: these admit somebody to a server that is not ours.</summary>
    internal const int InviteUses = 1;

    /// <summary>Resolved on use - see <see cref="GatewayGuildDirectory"/> for why.</summary>
    private IGuildDirectory? _directory;
    private IGuildDirectory Directory => _directory ??= services.GetRequiredService<IGuildDirectory>();

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - DM the super owner an invite to every Discord server this bot is in")
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var invites = await Directory.InviteToEveryGuildAsync(InviteLifetime, InviteUses, ct).ConfigureAwait(false);

        if (invites.Count == 0)
        {
            await Reply(command, Theme.Notice("Not in any Discord servers",
                "The gateway reports no guilds, so there is nothing to invite anyone to.")).ConfigureAwait(false);
            return;
        }

        await audit.RecordAsync("guildinvites", command.User.Username,
            OwnerGuard.SuperOwnerId.ToString(CultureInfo.InvariantCulture),
            $"{invites.Count} guild(s)", ct).ConfigureAwait(false);
        logger.LogWarning("GUILDINVITES by {User} | {Count} guild(s) | DM to {Recipient}",
            command.User.Username, invites.Count, OwnerGuard.SuperOwnerId);

        var pages = Pages(invites, InviteLifetime);
        var delivered = 0;

        foreach (var page in pages)
        {
            var embed = Theme.Notice($"Discord servers this bot is in ({invites.Count})", page).Brand().Build();
            if (await Directory.SendDirectMessageAsync(OwnerGuard.SuperOwnerId, embed, ct).ConfigureAwait(false))
                delivered++;
        }

        if (delivered == pages.Count)
        {
            await Reply(command, Theme.Success($"Sent {invites.Count} server(s)",
                $"DM'd to <@{OwnerGuard.SuperOwnerId}>. The invites are single-use and expire in " +
                $"{InviteLifetime.TotalHours:0} hours.")
                .AddField("Made an invite for", $"{invites.Count(i => i.Url is not null)} of {invites.Count}", inline: true))
                .ConfigureAwait(false);
            return;
        }

        /* THE DM DID NOT LAND, so the list goes in this reply instead. Without a fallback the
           command would have created invites to every guild and then thrown them away - and the
           usual reason is simply that the recipient has DMs from non-friends closed, which is a
           setting rather than a fault. This reply is ephemeral, and whoever ran it is an owner. */
        await Reply(command, Theme.Warning("Could not DM the super owner",
            $"<@{OwnerGuard.SuperOwnerId}> did not receive it - DMs from server members are most likely closed. " +
            $"Here is the list instead:\n\n{pages[0]}")).ConfigureAwait(false);
    }

    /// <summary>
    /// The list, split into embed-sized pages.
    /// </summary>
    /// <remarks>
    /// Pure, so the rendering is pinned by tests rather than read off a live DM. Guilds WITHOUT an
    /// invite are still listed, with the reason - the bot is in them whether or not it may invite
    /// anyone, and dropping them would understate where it actually is.
    /// </remarks>
    internal static IReadOnlyList<string> Pages(IReadOnlyList<GuildInvite> invites, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(invites);

        var lines = invites.Select(i =>
        {
            var name = Sanitize.Code(i.GuildName);
            var members = i.MemberCount.ToString("N0", CultureInfo.InvariantCulture);

            return i.Url is { Length: > 0 }
                ? $"{Theme.Ok} **{name}** ({members}) — {i.Url}"
                : $"{Theme.Bad} **{name}** ({members}) — {Sanitize.Code(i.Problem ?? "no invite")}";
        });

        var pages = Theme.Paginate(lines);

        // Said on the first page only: repeating it on every page of a long list is noise.
        return [$"Single-use, expire in {lifetime.TotalHours:0}h.\n\n{pages[0]}", .. pages.Skip(1)];
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
