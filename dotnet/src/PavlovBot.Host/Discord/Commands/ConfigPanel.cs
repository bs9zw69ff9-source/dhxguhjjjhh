using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// One panel action: what it is called, and how it is invoked.
/// </summary>
/// <param name="NeedsValue">Prompt for a value first. Null runs immediately.</param>
/// <param name="Confirm">
/// The word that must be typed back. Set for anything that destroys data, and NOT set for
/// anything reversible - a confirmation on a harmless action trains people to type the word
/// without reading it, which is exactly how the destructive ones get confirmed by reflex.
/// </param>
internal sealed record PanelAction(
    string Key,
    string Label,
    string Emoji,
    string Description,
    string? NeedsValue = null,
    string? Confirm = null);

/// <summary>
/// <c>/configure</c> - the owner control panel.
/// </summary>
/// <remarks>
/// A select menu rather than 18 slash commands, matching what this bot has always had. The
/// grouping is carried by emoji and ordering because Discord's select menus have no
/// sections.
///
/// Destructive actions ask for a typed word. A button is one misclick, and "wipe every
/// player's money" has no undo.
/// </remarks>
public sealed class ConfigPanel(
    OwnerActions actions, Access access, AuditLog audit, Paged paged, ILogger<ConfigPanel> logger)
    : ISlashCommand, IComponentHandler
{
    public const string Id = "cfg";

    public string Name => "configure";
    public string Prefix => Id;
    public bool Ephemeral => true;

    private static readonly IReadOnlyList<PanelAction> Actions =
    [
        // ── IP enforcement ──
        new("blacklist", "Blacklist IP / username", "🚫", "Auto-ban anyone matching an address or name", NeedsValue: "Address or username"),
        new("viewbl", "View blacklist", "📋", "Every blacklisted address, name and account"),
        new("alts", "View alt accounts", "🕵️", "Accounts sharing a confirmed address", NeedsValue: "Player name"),
        new("clearip", "Clear a specific address", "🧹", "Un-flag one address", NeedsValue: "Address"),
        new("clearnames", "Clear flagged usernames", "🧹", "Stop all username auto-bans", Confirm: "CLEAR"),
        new("clearall", "Wipe ALL address data", "💥", "Registry and every flag (irreversible)", Confirm: "WIPE"),

        // ── discord access ──
        new("baradd", "Bar a Discord user", "⛔", "Block a Discord user from ALL commands", NeedsValue: "Discord user id"),
        new("barremove", "Un-bar a Discord user", "✅", "Restore a Discord user's access", NeedsValue: "Discord user id"),
        new("barlist", "List barred Discord users", "📋", "Who is blocked from commands"),

        // ── address tracking ──
        new("ignoreadd", "Ignore a username", "🙈", "Stop recording a player's addresses", NeedsValue: "Player name"),
        new("ignoreremove", "Un-ignore a username", "👁️", "Resume recording", NeedsValue: "Player name"),
        new("ignorelist", "List ignored usernames", "📋", "Who is not being tracked"),

        // ── whitelists ──
        new("savewl", "Save whitelists", "💾", "Snapshot every whitelist rank and setting"),
        new("loadwl", "Load whitelists", "♻️", "Restore the snapshot (overwrites current)", Confirm: "RESTORE"),

        // ── bans and menus ──
        new("cleartempbans", "Clear temporary bans", "⏳", "Lift every temp ban, keep permanent ones", Confirm: "CLEAR"),
        new("clearallbans", "Clear ALL ban records", "🔓", "Empty the bot's ban list (irreversible)", Confirm: "WIPE"),
        new("stripmenuall", "Strip every menu grant", "🎫", "Revoke all RCON menu access", Confirm: "STRIP"),
        new("clearflags", "Clear every flag", "🧹", "All address, account and name flags", Confirm: "CLEAR"),

        // ── economy and data ──
        new("wipemoney", "Wipe ALL money", "💰", "Delete every player ledger (irreversible)", Confirm: "WIPE"),
        new("wipeplayers", "Wipe ALL player data", "🧨", "Registry, playtime, last-seen (irreversible)", Confirm: "WIPE"),
    ];

    private static PanelAction? Find(string? key) =>
        Actions.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - The control panel: blacklists, access, tracking, wipes")
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = ct;

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await command.ModifyOriginalResponseAsync(m =>
                m.Embed = Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Owner)).Brand().Build())
                .ConfigureAwait(false);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId(ComponentId.Encode(Id, "menu"))
            .WithPlaceholder("Choose an owner action...")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var action in Actions)
        {
            menu.AddOption(action.Label, action.Key, action.Description, new Emoji(action.Emoji));
        }

        await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = Theme.Notice("Owner Control Panel",
                    "Pick an action. Destructive ones ask you to type a word first.\n\n" +
                    "🚫 **Enforcement** — blacklist, alts, clear flags\n" +
                    "⛔ **Access** — bar / un-bar Discord users\n" +
                    "👁️ **Tracking** — ignore lists\n" +
                    "💾 **Whitelists** — save / restore\n" +
                    "🧨 **Data** — wipe money or player records")
                .Brand("Owner only").Build();
            m.Components = new ComponentBuilder().WithSelectMenu(menu).Build();
        }).ConfigureAwait(false);
    }

    public async Task HandleAsync(SocketInteraction interaction, ComponentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(id);

        /* Re-checked on EVERY interaction, not just when the panel was opened. The panel
           message persists, and permissions can be revoked between opening it and clicking
           - which is precisely when somebody being removed as owner would try to use it. */
        if (!access.Allows(RequiredAccess.Owner, interaction))
        {
            await Respond(interaction, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Owner))).ConfigureAwait(false);
            return;
        }

        switch (id.Argument(0))
        {
            case "menu" when interaction is SocketMessageComponent component:
                await OnChosenAsync(component, ct).ConfigureAwait(false);
                return;

            case "submit" when interaction is SocketModal modal:
                await OnSubmittedAsync(modal, id.Argument(1), ct).ConfigureAwait(false);
                return;

            default:
                await Respond(interaction, Theme.Failure("Unknown action", "That control is no longer supported.")).ConfigureAwait(false);
                return;
        }
    }

    private async Task OnChosenAsync(SocketMessageComponent component, CancellationToken ct)
    {
        var action = Find(component.Data.Values.FirstOrDefault());
        if (action is null)
        {
            await Respond(component, Theme.Failure("Unknown action", "That option is no longer supported.")).ConfigureAwait(false);
            return;
        }

        // A value or a confirmation means a modal, and a modal CANNOT be shown on an
        // interaction that has already been acknowledged - so this must not defer first.
        if (action.NeedsValue is not null || action.Confirm is not null)
        {
            var modal = new ModalBuilder()
                .WithTitle(Truncate(action.Label, 45))
                .WithCustomId(ComponentId.Encode(Id, "submit", action.Key));

            if (action.NeedsValue is { } prompt)
                modal.AddTextInput(Truncate(prompt, 45), "value", TextInputStyle.Short, required: true, maxLength: 100);

            if (action.Confirm is { } word)
            {
                modal.AddTextInput($"Type {word} to confirm", "confirm", TextInputStyle.Short,
                    placeholder: word, required: true, maxLength: 32);
            }

            await component.RespondWithModalAsync(modal.Build()).ConfigureAwait(false);
            return;
        }

        await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
        await RunAsync(component, action, value: null, ct).ConfigureAwait(false);
    }

    private async Task OnSubmittedAsync(SocketModal modal, string? key, CancellationToken ct)
    {
        var action = Find(key);
        if (action is null)
        {
            await Respond(modal, Theme.Failure("Unknown action", "That control is no longer supported.")).ConfigureAwait(false);
            return;
        }

        var fields = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? "", StringComparer.Ordinal);

        if (action.Confirm is { } word &&
            !string.Equals(fields.GetValueOrDefault("confirm", "").Trim(), word, StringComparison.OrdinalIgnoreCase))
        {
            await Respond(modal, Theme.Warning("Cancelled",
                $"You have to type **{word}** exactly. Nothing was changed.")).ConfigureAwait(false);
            return;
        }

        await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);
        await RunAsync(modal, action, fields.GetValueOrDefault("value"), ct).ConfigureAwait(false);
    }

    private async Task RunAsync(SocketInteraction interaction, PanelAction action, string? value, CancellationToken ct)
    {
        OwnerActionResult result;
        try
        {
            result = await ExecuteAsync(action.Key, value ?? "", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The owner is looking at a spinner, and these are the operations where "did
            // that work?" matters most. Never leave it unanswered.
            logger.LogError(ex, "Owner action {Action} failed", action.Key);
            await Followup(interaction, Theme.Failure(action.Label, "That failed. The error has been logged.")).ConfigureAwait(false);
            return;
        }

        if (action.Confirm is not null && result.Ok)
        {
            /* Only the destructive ones are audited. Recording "somebody looked at the
               blacklist" would bury the entries that matter under routine noise. */
            await audit.RecordAsync($"configure-{action.Key}", UserOf(interaction), "(server-wide)",
                Sanitize.Message(result.Detail), ct).ConfigureAwait(false);
            logger.LogWarning("DESTRUCTIVE {Action} | by={By}", action.Key, UserOf(interaction));
        }

        var embed = result.Ok
            ? Theme.Notice($"{action.Emoji} {action.Label}", result.Detail)
            : Theme.Failure(action.Label, result.Detail);

        if (result.Lines.Count == 0)
        {
            await Followup(interaction, embed).ConfigureAwait(false);
            return;
        }

        // Long lists page properly rather than being cut off at the embed limit.
        var pages = Theme.Paginate(result.Lines);
        await Followup(interaction, Theme.Notice($"{action.Emoji} {action.Label}",
            $"{result.Detail}\n\n{pages[0]}").Brand(
                pages.Count > 1 ? $"Page 1 of {pages.Count} — narrow it down to see the rest" : null))
            .ConfigureAwait(false);
        _ = paged;
    }

    private Task<OwnerActionResult> ExecuteAsync(string key, string value, CancellationToken ct) => key switch
    {
        "blacklist" => actions.BlacklistAsync(value, ct),
        "viewbl" => Task.FromResult(actions.ViewBlacklist()),
        "alts" => Task.FromResult(actions.Alts(value)),
        "clearip" => actions.ClearAddressAsync(value, ct),
        "clearnames" => actions.ClearFlaggedNamesAsync(ct),
        "clearall" => actions.WipeAllIpDataAsync(ct),

        "baradd" => actions.BarUserAsync(value, ct),
        "barremove" => actions.UnbarUserAsync(value, ct),
        "barlist" => Task.FromResult(actions.BarredUsers()),

        "ignoreadd" => actions.IgnoreAsync(value, ct),
        "ignoreremove" => actions.UnignoreAsync(value, ct),
        "ignorelist" => Task.FromResult(actions.IgnoredNames()),

        "savewl" => actions.SaveWhitelistsAsync(ct),
        "loadwl" => actions.LoadWhitelistsAsync(ct),

        "cleartempbans" => actions.ClearTempBansAsync(ct),
        "clearallbans" => actions.ClearAllBansAsync(ct),
        "stripmenuall" => actions.StripAllMenusAsync(ct),
        "clearflags" => actions.ClearAllFlagsAsync(ct),

        "wipemoney" => Task.FromResult(actions.WipeMoney()),
        "wipeplayers" => actions.WipePlayerDataAsync(ct),

        _ => Task.FromResult(OwnerActionResult.Refused("That action is not implemented.")),
    };

    private static string UserOf(SocketInteraction interaction) => interaction.User?.Username ?? "unknown";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private static Task Respond(SocketInteraction interaction, EmbedBuilder embed) =>
        interaction.RespondAsync(embed: embed.Brand().Build(), ephemeral: true);

    private static Task Followup(SocketInteraction interaction, EmbedBuilder embed) =>
        interaction.FollowupAsync(embed: embed.Brand().Build(), ephemeral: true);
}
