using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Text;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// Self-serve RCON menu access: a button, a name, and the menu your roles earn you.
/// </summary>
/// <remarks>
/// The tier comes from the claimer's DISCORD ROLES, not from anything they type. High Staff
/// outranks Staff; the blacklist role bars them outright. That is what makes this safe to
/// leave open in a channel - the member chooses their name, never their level of access.
///
/// The binding rule is <see cref="MenuLink"/>'s and is shared with <c>/givemenu</c>: one
/// Discord account to one in-game name, permanently, and releasing a menu frees the access
/// but never the name. Re-entering your OWN name while holding a menu strips it, which is
/// how somebody redoes a broken grant without an admin.
/// </remarks>
public sealed class MenuPanel(
    RconRegistry rcon,
    SerializedStore store,
    FeatureOptions features,
    AuditLog audit,
    ILogger<MenuPanel> logger) : IComponentHandler
{
    public const string Id = "menu";

    public string Prefix => Id;

    public bool Enabled => features.MenuPanelChannel is not null;

    public Embed BuildPanel() =>
        Theme.Success("RCON menu access",
                "Tools for trusted staff.\n\n" +
                "Press **Get menu** and enter your **exact** Pavlov in-game name. " +
                "The bot grants the menu matching your highest staff role — no admin needed.\n\n" +
                "One in-game name per Discord account, permanently. Enter **your own name again** " +
                "at any time to remove your menu and redo it.")
            .Brand()
            .Build();

    public static MessageComponent PanelButton() =>
        new ComponentBuilder()
            .WithButton("Get menu", ComponentId.Encode(Id, "start"), ButtonStyle.Success)
            .Build();

    private Dictionary<string, MenuBinding> Bindings() =>
        store.Read(Datasets.MenuLinks, new Dictionary<string, MenuBinding>(StringComparer.Ordinal));

    private Dictionary<string, string> Grants() =>
        store.Read(Datasets.MenuGrants, new Dictionary<string, string>(StringComparer.Ordinal));

    public async Task HandleAsync(SocketInteraction interaction, ComponentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(id);

        switch (id.Argument(0))
        {
            case "start":
                // No defer: a modal cannot be shown on an acknowledged interaction.
                await interaction.RespondWithModalAsync(new ModalBuilder()
                    .WithTitle("Get RCON menu access")
                    .WithCustomId(ComponentId.Encode(Id, "submit"))
                    .AddTextInput("Your exact Pavlov in-game name", "name", TextInputStyle.Short,
                        required: true, maxLength: 64)
                    .Build()).ConfigureAwait(false);
                return;

            case "submit" when interaction is SocketModal modal:
                await OnSubmittedAsync(modal, ct).ConfigureAwait(false);
                return;

            default:
                await interaction.RespondAsync("That control is no longer supported.", ephemeral: true).ConfigureAwait(false);
                return;
        }
    }

    private async Task OnSubmittedAsync(SocketModal modal, CancellationToken ct)
    {
        await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);

        var member = modal.User as IGuildUser;
        var tier = TierOf(member);

        if (tier is null)
        {
            await Followup(modal, Theme.Denied("Not eligible",
                "Your roles do not grant menu access. Ask an admin if you think that is wrong."))
                .ConfigureAwait(false);
            return;
        }

        if (tier == "blacklist")
        {
            // Said plainly rather than pretending they simply do not qualify - somebody
            // whose access was revoked should know it was revoked.
            logger.LogInformation("menu claim blocked by blacklist role | member={Member}", modal.User.Username);
            await Followup(modal, Theme.Denied("Menu access revoked",
                "Your self-serve menu access has been revoked. Contact an admin if you believe this is a mistake."))
                .ConfigureAwait(false);
            return;
        }

        var requested = Sanitize.Id(NameField(modal));
        if (requested.Length == 0)
        {
            await Followup(modal, Theme.Failure("Enter your exact in-game name")).ConfigureAwait(false);
            return;
        }

        var selfId = modal.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bindings = Bindings();

        var boundName = bindings.GetValueOrDefault(selfId)?.InGameName;
        var owner = bindings.Values
            .FirstOrDefault(b => string.Equals(b.InGameName, requested, StringComparison.OrdinalIgnoreCase))?.DiscordId;

        // The same decision /givemenu uses. Two entry points must not disagree about who
        // owns a name.
        var decision = MenuLink.Decide(boundName, requested, owner, selfId, Grants().ContainsKey(selfId));

        switch (decision.Action)
        {
            case MenuClaimAction.Locked:
                await Followup(modal, Theme.Denied("Name locked",
                    $"Your account is linked to `{Sanitize.Code(decision.BoundName!)}`, and that link is permanent.\n\n" +
                    "If you genuinely changed your Pavlov name, ask an admin to unlink you.")).ConfigureAwait(false);
                return;

            case MenuClaimAction.Taken:
                await Followup(modal, Theme.Denied("Name taken",
                    $"`{Sanitize.Code(requested)}` is already linked to another Discord account. " +
                    "If that is your name, ask an admin to sort it out.")).ConfigureAwait(false);
                return;

            case MenuClaimAction.Release:
                await ReleaseAsync(modal, selfId, requested, ct).ConfigureAwait(false);
                return;
        }

        await GrantAsync(modal, selfId, requested, tier, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Which menu their roles earn. Null means none at all.
    /// </summary>
    /// <remarks>
    /// HIGHEST FIRST, and blacklist above everything: somebody holding both the blacklist
    /// role and a staff role has had their access revoked, so the revocation has to win.
    /// </remarks>
    private string? TierOf(IGuildUser? member)
    {
        if (member is null) return null;

        if (features.MenuRoleBlacklist is { } barred && member.RoleIds.Contains(barred)) return "blacklist";
        if (features.MenuRoleHighStaff is { } high && member.RoleIds.Contains(high)) return "highstaff";
        if (features.MenuRoleStaff is { } staff && member.RoleIds.Contains(staff)) return "staff";
        return null;
    }

    private async Task GrantAsync(SocketModal modal, string selfId, string name, string tier, CancellationToken ct)
    {
        var delivered = 0;
        foreach (var server in rcon.Servers)
        {
            if (await TrySend(server, $"GiveMenu {name}", ct).ConfigureAwait(false)) delivered++;

            /* High staff also get Mod and Access Manager, matching the Node bot. These are
               best-effort on purpose: the MENU is the grant, and a server that rejected the
               extras should not make the whole claim read as failed. */
            if (tier == "highstaff")
            {
                await TrySend(server, $"GiveMod {name}", ct).ConfigureAwait(false);
                await TrySend(server, $"GiveAccessManager {name}", ct).ConfigureAwait(false);
            }
        }

        if (delivered == 0)
        {
            /* Nothing recorded when nothing landed. A grant the server never received leaves
               the bot believing they have access they do not - and the NEXT claim then reads
               as a release, silently stripping the menu they never got. */
            await Followup(modal, Theme.Failure("Not granted",
                "No server accepted the command. Nothing was recorded — try again shortly.")).ConfigureAwait(false);
            return;
        }

        await store.UpdateAsync(Datasets.MenuLinks,
            new Dictionary<string, MenuBinding>(StringComparer.Ordinal),
            links => { links[selfId] = new MenuBinding(selfId, name, DateTimeOffset.UtcNow); return links; }, ct).ConfigureAwait(false);

        await store.UpdateAsync(Datasets.MenuGrants,
            new Dictionary<string, string>(StringComparer.Ordinal),
            grants => { grants[selfId] = name; return grants; }, ct).ConfigureAwait(false);

        await audit.RecordAsync("menu-claim", modal.User.Username, name, tier, ct).ConfigureAwait(false);
        logger.LogInformation("menu claimed | member={Member} | player=\"{Player}\" | tier={Tier} | {Servers} server(s)",
            modal.User.Username, name, tier, delivered);

        await Followup(modal, Theme.Success("Menu granted",
            $"`{Sanitize.Code(name)}` now has the **{tier}** menu on **{delivered}** server(s).\n\n" +
            "Your account is permanently linked to that name. Enter it again here to remove the menu.")).ConfigureAwait(false);
    }

    private async Task ReleaseAsync(SocketModal modal, string selfId, string name, CancellationToken ct)
    {
        foreach (var server in rcon.Servers)
        {
            await TrySend(server, $"StripMenu {name}", ct).ConfigureAwait(false);
            await TrySend(server, $"RemoveMod {name}", ct).ConfigureAwait(false);
            await TrySend(server, $"RemoveAccessManager {name}", ct).ConfigureAwait(false);
        }

        /* The GRANT goes, the BINDING stays. Dropping the binding here is exactly what would
           let somebody cycle their menu onto an alt: release, then re-claim under a
           different name. */
        await store.UpdateAsync(Datasets.MenuGrants,
            new Dictionary<string, string>(StringComparer.Ordinal),
            grants => { grants.Remove(selfId); return grants; }, ct).ConfigureAwait(false);

        await audit.RecordAsync("menu-release", modal.User.Username, name, "self-service", ct).ConfigureAwait(false);
        logger.LogInformation("menu released | member={Member} | player=\"{Player}\"", modal.User.Username, name);

        await Followup(modal, Theme.Success("Menu removed",
            $"Removed the menu from `{Sanitize.Code(name)}`. Press **Get menu** again to re-claim it — " +
            "your account stays linked to that name.")).ConfigureAwait(false);
    }

    private async Task<bool> TrySend(string server, string command, CancellationToken ct)
    {
        try
        {
            await rcon.SendAsync(server, command, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Server} rejected {Command}", server, command);
            return false;
        }
    }

    private static Task Followup(SocketInteraction interaction, EmbedBuilder embed) =>
        interaction.FollowupAsync(embed: embed.Brand().Build(), ephemeral: true);

    /// <summary>
    /// The typed name, accepting the Node bot's field id as well as ours.
    /// </summary>
    /// <remarks>
    /// A modal opened by the Node bot's panel just before the cutover submits with
    /// <c>menu_name</c>. Reading only our own id would return empty and answer "no name
    /// given" to somebody who typed one.
    /// </remarks>
    private static string NameField(SocketModal modal) =>
        modal.Data.Components.FirstOrDefault(c => c.CustomId is "name" or "menu_name")?.Value ?? "";

}
