using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Text;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <param name="InGameName">The name this Discord account is permanently bound to.</param>
public sealed record MenuBinding(string DiscordId, string InGameName, DateTimeOffset BoundAt);

/// <summary>
/// <c>/givemenu</c> - grant a player RCON+ menu access, bound to one in-game name.
/// </summary>
/// <remarks>
/// The binding is the point of the whole command. Menu access is real power in the game, so
/// without one, access can be cycled onto alt accounts freely: claim it, release it, claim
/// it again as somebody else. A member binds to exactly one in-game name the first time
/// they claim, and THE BINDING OUTLIVES THE MENU - releasing frees the access, never the
/// name. Only <c>/unlinkname</c> breaks one, for a genuine Pavlov name change.
/// </remarks>
public sealed class GiveMenuCommand(
    RconRegistry rcon, SerializedStore store, Access access, RconConfirmations confirmations,
    ILogger<GiveMenuCommand> logger) : ISlashCommand
{
    public string Name => "givemenu";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Grant RCON menu access to a player")
            .AddOption("member", ApplicationCommandOptionType.User, "Which Discord member", isRequired: true)
            .AddOption("playerid", ApplicationCommandOptionType.String, "Their in-game name", isRequired: true, isAutocomplete: true)
            /* The tier was MISSING entirely, which is why every grant produced a menu with
               nothing in it: without it there is no bit code to send and no way to ask for
               the Mod and Access Manager powers High Staff is defined by. */
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("menu").WithDescription("Which menu to grant")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .AddChoice("Staff", RconMenu.Staff)
                .AddChoice("High Staff", RconMenu.HighStaff))
            .Build();

    private Dictionary<string, MenuBinding> Bindings() =>
        store.Read(Datasets.MenuLinks, new Dictionary<string, MenuBinding>(StringComparer.Ordinal));

    private Dictionary<string, string> Grants() =>
        store.Read(Datasets.MenuGrants, new Dictionary<string, string>(StringComparer.Ordinal));

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var member = command.Data.Options.First(o => o.Name == "member").Value as IUser;
        var requested = Sanitize.Id(command.Data.Options.First(o => o.Name == "playerid").Value as string ?? "");
        var tier = command.Data.Options.FirstOrDefault(o => o.Name == "menu")?.Value as string ?? RconMenu.Staff;

        if (member is null || requested.Length == 0)
        {
            await Reply(command, Theme.Failure("Need a member and a usable in-game name")).ConfigureAwait(false);
            return;
        }

        var selfId = member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bindings = Bindings();
        var grants = Grants();

        var boundName = bindings.GetValueOrDefault(selfId)?.InGameName;
        var owner = bindings.Values
            .FirstOrDefault(b => string.Equals(b.InGameName, requested, StringComparison.OrdinalIgnoreCase))?.DiscordId;
        var holdsMenu = grants.ContainsKey(selfId);

        var decision = MenuLink.Decide(boundName, requested, owner, selfId, holdsMenu);

        switch (decision.Action)
        {
            case MenuClaimAction.Locked:
                await Reply(command, Theme.Denied("Bound to another name",
                    $"{member.Mention} is permanently bound to `{Sanitize.Code(decision.BoundName!)}`. " +
                    "Use `/unlinkname` if they genuinely changed their Pavlov name.")).ConfigureAwait(false);
                return;

            case MenuClaimAction.Taken:
                await Reply(command, Theme.Denied("That name belongs to someone else",
                    $"`{Sanitize.Code(requested)}` is bound to <@{decision.OwnerId}>.")).ConfigureAwait(false);
                return;

            case MenuClaimAction.Release:
                await SetGrantAsync(selfId, null, ct).ConfigureAwait(false);
                foreach (var server in rcon.Servers)
                    foreach (var line in RconMenu.Revoke(requested, wasHighStaff: true))
                        await TrySend(server, line, ct).ConfigureAwait(false);

                logger.LogInformation("stripmenu | member={Member} | player=\"{Player}\" | by={By}",
                    selfId, requested, command.User.Username);

                await Reply(command, Theme.Success("Menu access removed",
                    $"{member.Mention} no longer has menu access. Their name binding is unchanged.")).ConfigureAwait(false);
                return;
        }

        var commands = RconMenu.Grant(requested, tier);

        // For confirming against Pavlov.log if the RCON replies are unreadable - the instant
        // the send began, so an older identical log line does not count as this grant.
        var sentAt = DateTimeOffset.UtcNow;

        var delivered = 0;
        var attempts = new List<(string Server, string? Problem)>();

        foreach (var server in rcon.Servers)
        {
            var ok = false;
            string? problem = null;

            foreach (var line in commands)
            {
                var sent = await TrySend(server, line, ct).ConfigureAwait(false);
                ok |= sent.Ok;
                problem ??= sent.Problem;
            }

            if (ok) delivered++; else attempts.Add((server, problem));
        }

        if (delivered == 0)
        {
            /* THE RCON REPLY IS NOT THE ONLY WITNESS. RCON+ answers over a channel the bot
               cannot always read, so "no server accepted" can be a grant that in fact landed.
               The server LOGS what it ran - `Rcon Plus Command Executed: GiveMenu …` - so give
               the tail a moment to confirm the menu command before calling it a failure. */
            var confirm = $"GiveMenu {requested} {RconMenu.MenuId(tier)}";
            if (await confirmations.ConfirmedAsync(confirm, sentAt, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "givemenu | no RCON ack but Pavlov.log confirms it ran | player=\"{Player}\" | by={By}",
                    requested, command.User.Username);
            }
            else
            {
                /* Nothing is recorded when nothing landed. Recording a grant the server never
                   received leaves the bot believing they have access they do not, and the next
                   claim reads as a release. */
                await Reply(command, RconFailure.Explain("Not granted",
                    "No server accepted the command, and Pavlov.log never showed it run. Nothing was recorded.",
                    rcon, attempts)).ConfigureAwait(false);
                return;
            }
        }

        await SetGrantAsync(selfId, requested, ct).ConfigureAwait(false);

        if (boundName is null)
        {
            await store.UpdateAsync(Datasets.MenuLinks,
                new Dictionary<string, MenuBinding>(StringComparer.Ordinal),
                links =>
                {
                    links[selfId] = new MenuBinding(selfId, requested, DateTimeOffset.UtcNow);
                    return links;
                }, ct).ConfigureAwait(false);
        }

        logger.LogInformation("givemenu | member={Member} | player=\"{Player}\" | by={By} | servers={Servers}",
            selfId, requested, command.User.Username, delivered);

        await Reply(command, Theme.Success($"{(RconMenu.IsHighStaff(tier) ? "High Staff" : "Staff")} menu granted",
            $"{member.Mention} → `{Sanitize.Code(requested)}`")
            .AddField("Binding", boundName is null ? "newly bound to this name" : "already bound", true)).ConfigureAwait(false);
    }

    private Task SetGrantAsync(string discordId, string? player, CancellationToken ct) =>
        store.UpdateAsync(Datasets.MenuGrants,
            new Dictionary<string, string>(StringComparer.Ordinal),
            grants =>
            {
                if (player is null) grants.Remove(discordId);
                else grants[discordId] = player;
                return grants;
            }, ct);

    /// <param name="Problem">Why it failed, for the reply. Null when it worked.</param>
    private readonly record struct SendResult(bool Ok, string? Problem);

    private async Task<SendResult> TrySend(string server, string rconCommand, CancellationToken ct)
    {
        try
        {
            await rcon.SendAsync(server, rconCommand, ct).ConfigureAwait(false);
            return new SendResult(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* CARRIED BACK, not just logged. The reply used to say "no server accepted the
               command" while the reason sat in a log file somebody would have to go and
               find - and the reasons need four different actions. */
            logger.LogWarning("{Command} failed on {Server}: {Message}", rconCommand.Split(' ')[0], server, ex.Message);
            return new SendResult(false, ex.Message);
        }
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary>
/// <c>/unlinkname</c> - break a permanent name binding.
/// </summary>
/// <remarks>
/// Admin-only, and the ONLY way a binding is ever removed. It exists for a genuine Pavlov
/// name change; making it self-service would make the binding decorative, since anyone
/// could unbind and re-claim as an alt.
/// </remarks>
public sealed class UnlinkNameCommand(SerializedStore store, Access access, ILogger<UnlinkNameCommand> logger) : ISlashCommand
{
    public string Name => "unlinkname";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Break a member's permanent in-game name binding")
            .AddOption("member", ApplicationCommandOptionType.User, "Which Discord member", isRequired: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var member = command.Data.Options.First().Value as IUser;
        if (member is null)
        {
            await Reply(command, Theme.Failure("Need a member")).ConfigureAwait(false);
            return;
        }

        var id = member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var existing = store.Read(Datasets.MenuLinks, new Dictionary<string, MenuBinding>(StringComparer.Ordinal))
            .GetValueOrDefault(id);

        if (existing is null)
        {
            await Reply(command, Theme.Notice("Nothing to unlink", $"{member.Mention} is not bound to a name.")).ConfigureAwait(false);
            return;
        }

        await store.UpdateAsync(Datasets.MenuLinks,
            new Dictionary<string, MenuBinding>(StringComparer.Ordinal),
            links => { links.Remove(id); return links; }, ct).ConfigureAwait(false);

        logger.LogWarning("unlinkname | member={Member} | was=\"{Name}\" | by={By}",
            id, existing.InGameName, command.User.Username);

        await Reply(command, Theme.Success("Binding broken",
            $"{member.Mention} was bound to `{Sanitize.Code(existing.InGameName)}` and may now bind to a new name.")
            .AddField($"{Theme.Warn} Note", "This is the only way a binding is removed. It exists for a real name change.")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
