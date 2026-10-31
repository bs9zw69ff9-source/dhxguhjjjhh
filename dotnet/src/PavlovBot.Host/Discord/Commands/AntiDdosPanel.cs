using System.Globalization;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>One text box an anti-DDoS action asks for.</summary>
/// <param name="Name">The option name it becomes - the same name the slash command uses.</param>
/// <param name="Number">Passed on as an integer, as Discord passes integer options.</param>
internal sealed record PanelInput(string Name, string Label, string Placeholder, bool Required = true, bool Number = false, int MaxLength = 100);

/// <summary>One entry of the Anti-DDoS menu.</summary>
/// <param name="Run">The <c>/antiddos</c> subcommand it runs, or a <c>tools:</c> packet-tools action.</param>
/// <param name="Fixed">Options it always passes (a fixed window, a fixed mode).</param>
/// <param name="Confirm">The word to type back, for the actions that let attackers back in.</param>
internal sealed record DdosAction(
    string Key,
    string Label,
    string Emoji,
    string Description,
    string Run,
    IReadOnlyList<PanelInput>? Inputs = null,
    IReadOnlyDictionary<string, object>? Fixed = null,
    string? Confirm = null);

/// <summary>
/// The Anti-DDoS menu inside <c>/configure</c>: every anti-DDoS and packet-tools action, for the owner.
/// </summary>
/// <remarks>
/// A SECOND MENU, NOT MORE OPTIONS. Discord caps one select menu at 25 entries and the owner panel
/// already uses most of them, so the anti-DDoS actions get a row of their own in the same panel.
///
/// THE SAME CODE AS THE SLASH COMMANDS. Every entry runs <see cref="AntiDdosCommand.RunAsync"/> or
/// a <see cref="PacketToolsCommand"/> action with the options a modal collected, so validation,
/// auditing and the replies are exactly those of <c>/antiddos</c> and <c>/packettools</c>, which
/// stay for anybody who prefers typing and for the Admin-level views.
///
/// OWNER ONLY, RE-CHECKED ON EVERY CLICK, like the rest of the panel: it is a persistent message,
/// and access can be revoked between opening it and using it.
/// </remarks>
public sealed class AntiDdosPanel(Access access, AntiDdosCommand antiddos, PacketToolsCommand tools, ILogger<AntiDdosPanel> logger)
    : IComponentHandler
{
    public const string Id = "ddos";

    public string Prefix => Id;

    private static readonly PanelInput Ip = new("ip", "IP address or network", "203.0.113.5 or 203.0.113.0/24", MaxLength: 64);

    private static Dictionary<string, object> Fixed(params (string Key, object Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    internal static readonly IReadOnlyList<DdosAction> Actions =
    [
        // ── look ──
        new("status", "Anti-DDoS status", "🛡️", "Firewall, logging, last minute, CrowdSec, storage", "status"),
        new("traffic", "Traffic, last hour", "📈", "Packet and byte rates, busiest sources", "traffic", Fixed: Fixed(("minutes", 60L))),
        new("top", "Busiest sources, last 24h", "🔝", "The highest-volume addresses", "top", Fixed: Fixed(("minutes", 1440L))),
        new("lookup", "Look up an IP", "🔎", "Everything recorded about one address (attached)", "history",
            [Ip, new("minutes", "How many minutes back (default 1440)", "1440", Required: false, Number: true, MaxLength: 5)]),
        new("detections", "Recent detections", "🚨", "What was detected and what was done", "detections"),
        new("bans", "Everything blocked now", "⛔", "Firewall blocks and CrowdSec bans", "bans"),
        new("crowdsec", "CrowdSec", "🧱", "Decisions, alerts in 24h, bouncer and parser health", "crowdsec"),
        new("storage", "Log storage", "💽", "Sizes, oldest records, disk space, rotation", "storage"),
        new("trustlist", "Trusted list", "📋", "Addresses that skip the limits", "allow list"),
        new("thresholds", "Thresholds", "🎚️", "Every threshold, its value and its range", "threshold list"),

        // ── change ──
        new("block", "Ban an IP or network", "🚫", "Through CrowdSec; at most a /24 or /48", "block",
            [Ip, new("duration", "Duration: 30m, 4h, 7d or permanent", "4h", MaxLength: 16), new("reason", "Reason", "kept with the ban", MaxLength: 200)]),
        new("unban", "Unban an IP or network", "✅", "Lift every ban on it, CrowdSec's and the firewall's", "unban", [Ip]),
        new("trust", "Trust an IP or network", "🤝", "Skip the limits and CrowdSec bans; ufw still applies", "allow add",
            [Ip, new("comment", "Whose it is", "owner's home", MaxLength: 100)]),
        new("untrust", "Stop trusting an IP", "↩️", "Take it off the trusted list", "allow remove", [Ip]),
        new("threshold", "Change a threshold", "🎛️", "Kept across restarts; see Thresholds for names", "threshold set",
            [new("name", "Threshold name", "per_ip_pps", MaxLength: 32), new("value", "New value", "1000", Number: true, MaxLength: 9)]),
        new("incident", "Incident logging", "🔴", "Detailed packet logging for a while, then back", "logging",
            [new("minutes", "For how many minutes (5-1440)", "30", Required: false, Number: true, MaxLength: 4)], Fixed(("mode", "incident"))),
        new("elevated", "Elevated logging", "🟠", "More logging for a while, then back", "logging",
            [new("minutes", "For how many minutes (5-1440)", "360", Required: false, Number: true, MaxLength: 4)], Fixed(("mode", "elevated"))),
        new("normal", "Logging back to normal", "🟢", "End incident or elevated logging now", "logging", Fixed: Fixed(("mode", "normal"))),
        new("export", "Export records", "📦", "Gzip JSON Lines, attached when small enough", "export",
            [new("minutes", "How many minutes back (default 1440)", "1440", Required: false, Number: true, MaxLength: 5),
             Ip with { Required = false }]),
        new("unblockall", "Unblock every firewall block", "🔓", "Let every address the filter blocked back in", "tools:unblock", Confirm: "UNBLOCK"),

        // ── packet tools ──
        new("tools", "Packet tools status", "🧰", "tshark, Wireshark, iftop, nftables, capture", "tools:status"),
        new("install", "Install packet tools", "⬇️", "apt install wireshark tshark iftop nftables", "tools:install"),
        new("analyze", "Analyze latest attack capture", "🔬", "Wireshark statistics of the newest pcap", "tools:analyze"),
    ];

    internal static DdosAction? Find(string? key) => Actions.FirstOrDefault(a => a.Key == key);

    /// <summary>The menu row the owner panel shows under its own.</summary>
    public SelectMenuBuilder Menu()
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId(ComponentId.Encode(Id, "menu"))
            .WithPlaceholder("Anti-DDoS: firewall, bans, logs, CrowdSec...")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var a in Actions) menu.AddOption(a.Label, a.Key, a.Description, new Emoji(a.Emoji));
        return menu;
    }

    public async Task HandleAsync(SocketInteraction interaction, ComponentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(id);

        if (!access.Allows(RequiredAccess.Owner, interaction))
        {
            await interaction.RespondAsync(embed: Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, interaction)).Brand().Build(),
                ephemeral: true).ConfigureAwait(false);
            return;
        }

        switch (id.Argument(0))
        {
            case "menu" when interaction is SocketMessageComponent component && Find(component.Data.Values.FirstOrDefault()) is { } chosen:
                await OnChosenAsync(component, chosen, ct).ConfigureAwait(false);
                return;
            case "submit" when interaction is SocketModal modal && Find(id.Argument(1)) is { } submitted:
                await OnSubmittedAsync(modal, submitted, ct).ConfigureAwait(false);
                return;
            default:
                await interaction.RespondAsync(embed: Theme.Failure("Unknown action", "That control is no longer supported.").Brand().Build(),
                    ephemeral: true).ConfigureAwait(false);
                return;
        }
    }

    private async Task OnChosenAsync(SocketMessageComponent component, DdosAction action, CancellationToken ct)
    {
        // A modal cannot be shown on an acknowledged interaction, so this must not defer first.
        if (Modal(action) is { } modal)
        {
            await component.RespondWithModalAsync(modal).ConfigureAwait(false);
            return;
        }

        await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
        await RunAsync(component, action, new Dictionary<string, object>(StringComparer.Ordinal), ct).ConfigureAwait(false);
    }

    /// <summary>The modal for an action that needs input or a confirmation; null when it runs at once.</summary>
    internal static Modal? Modal(DdosAction action)
    {
        if (action.Inputs is not { Count: > 0 } && action.Confirm is null) return null;

        var modal = new ModalBuilder().WithTitle(Truncate(action.Label, 45)).WithCustomId(ComponentId.Encode(Id, "submit", action.Key));
        foreach (var input in action.Inputs ?? [])
            modal.AddTextInput(Truncate(input.Label, 45), input.Name, TextInputStyle.Short, input.Placeholder, required: input.Required, maxLength: input.MaxLength);
        if (action.Confirm is { } word)
            modal.AddTextInput($"Type {word} to confirm", "confirm", TextInputStyle.Short, word, required: true, maxLength: 32);
        return modal.Build();
    }

    private async Task OnSubmittedAsync(SocketModal modal, DdosAction action, CancellationToken ct)
    {
        var fields = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? "", StringComparer.Ordinal);

        if (action.Confirm is { } word && !string.Equals(fields.GetValueOrDefault("confirm", "").Trim(), word, StringComparison.OrdinalIgnoreCase))
        {
            await modal.RespondAsync(embed: Theme.Warning("Cancelled", $"You have to type **{word}** exactly. Nothing was changed.").Brand().Build(),
                ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (Arguments(action, fields, out var args) is { } problem)
        {
            await modal.RespondAsync(embed: Theme.Failure(action.Label, problem).Brand().Build(), ephemeral: true).ConfigureAwait(false);
            return;
        }

        await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);
        await RunAsync(modal, action, args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The options a modal's text becomes, typed as Discord types slash-command options. The
    /// problem, or null.
    /// </summary>
    internal static string? Arguments(DdosAction action, IReadOnlyDictionary<string, string> fields, out Dictionary<string, object> args)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(fields);
        args = new Dictionary<string, object>(action.Fixed ?? new Dictionary<string, object>(), StringComparer.Ordinal);

        foreach (var input in action.Inputs ?? [])
        {
            var text = fields.GetValueOrDefault(input.Name, "").Trim();
            if (text.Length == 0)
            {
                if (input.Required) return $"{input.Label} is required.";
                continue;
            }

            if (!input.Number)
            {
                args[input.Name] = text;
                continue;
            }
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                return $"{input.Label}: `{Core.Text.Sanitize.Code(text)}` is not a whole number.";
            args[input.Name] = number;
        }
        return null;
    }

    private async Task RunAsync(SocketInteraction interaction, DdosAction action, IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var actor = interaction.User?.Username ?? "unknown";
        RestFollowupMessage? sent = null;

        // The first reply is a follow-up; any later one (the install's result) edits it.
        async Task Reply(AntiDdosReply reply)
        {
            var embed = reply.Embed.Brand().Build();
            if (sent is not null)
            {
                await sent.ModifyAsync(m => m.Embed = embed).ConfigureAwait(false);
                return;
            }

            if (reply.File is { } bytes)
            {
                using var stream = new MemoryStream(bytes);
                sent = await interaction.FollowupWithFileAsync(stream, reply.FileName, embed: embed, ephemeral: true,
                    allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            }
            else
            {
                sent = await interaction.FollowupAsync(embed: embed, ephemeral: true, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            }
        }

        try
        {
            switch (action.Run)
            {
                case "tools:status":
                    await Reply(await tools.StatusAsync("Packet tools", ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case "tools:install":
                    await tools.InstallAsync(Reply, actor, ct).ConfigureAwait(false);
                    break;
                case "tools:analyze":
                    await tools.AnalyzeAsync(Reply, ct).ConfigureAwait(false);
                    break;
                case "tools:unblock":
                    await tools.UnblockAsync(Reply, actor, ct).ConfigureAwait(false);
                    break;
                default:
                    await Reply(await antiddos.RunAsync(action.Run, args, actor, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Anti-DDoS panel action {Action} failed", action.Key);
            await Reply(Theme.Failure(action.Label, "That failed. The error has been logged.")).ConfigureAwait(false);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
