using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Services;

namespace PavlovBot.Host.Discord.Commands;

/// <summary><c>/kick</c> - remove a player without a ban record.</summary>
public sealed class KickCommand(RconRegistry rcon, BanService bans, AuditLog audit, Access access, ILogger<KickCommand> logger) : ISlashCommand
{
    public string Name => "kick";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Kick a player")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player ID or username", isRequired: true, isAutocomplete: true)
            .AddOption("reason", ApplicationCommandOptionType.String, "Why", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Mod))).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(command.Data.Options.First(o => o.Name == "playerid").Value as string ?? "");
        var reason = Sanitize.Message(command.Data.Options.FirstOrDefault(o => o.Name == "reason")?.Value as string ?? "");

        /* Prefer the UniqueId from the live roster. Kick against a display name is a silent
           no-op - the server accepts the command and does nothing - which is how a kick
           "works" and the player stays in the game. */
        var online = rcon.Servers
            .SelectMany(s => rcon.Roster(s).Players)
            .FirstOrDefault(p => string.Equals(p.Name, player, StringComparison.OrdinalIgnoreCase));

        // ban: false - a kick is not a ban, and issuing one here would leave a native ban
        // nothing in the bot knows to lift.
        var result = await bans.HardEnforceAsync(player, online.UniqueId is { Length: > 0 } id ? id : null,
            ban: false, kick: true, ct).ConfigureAwait(false);

        await audit.RecordAsync("kick", command.User.Username, player, reason, ct).ConfigureAwait(false);

        logger.LogInformation("kick | player=\"{Player}\" | by={By} | accepted={Accepted} | {Reason}",
            player, command.User.Username, result.Servers, reason);

        await Reply(command, result.Landed
            ? Theme.Success("Kicked", $"**{Sanitize.Code(player)}**{(reason.Length > 0 ? $" — {Sanitize.Code(reason)}" : "")}")
                .AddField("By", command.User.Username, true)
            : Theme.Failure("Not kicked",
                $"No server accepted the command. {(online.UniqueId.Length == 0 ? "They may not be online." : "")}")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/announce</c> - broadcast an RCON notice.</summary>
public sealed class AnnounceCommand(RconRegistry rcon, Access access, ILogger<AnnounceCommand> logger) : ISlashCommand
{
    public string Name => "announce";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Broadcast a message to the server")
            .AddOption("message", ApplicationCommandOptionType.String, "What to say", isRequired: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Mod))).ConfigureAwait(false);
            return;
        }

        /* Sanitize.Message, not Sanitize.Id. The protocol is line-oriented, so an embedded
           newline splits one command into two and the second half is whatever the author
           typed - a command injection through a broadcast box. */
        var message = Sanitize.Message(command.Data.Options.First().Value as string ?? "");
        if (message.Length == 0)
        {
            await Reply(command, Theme.Failure("Nothing to say", "The message was empty after sanitising.")).ConfigureAwait(false);
            return;
        }

        var delivered = 0;
        foreach (var server in rcon.Servers)
        {
            try
            {
                await rcon.SendAsync(server, $"Notify {message}", ct).ConfigureAwait(false);
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Announce failed on {Server}: {Message}", server, ex.Message);
            }
        }

        logger.LogInformation("announce | by={By} | servers={Delivered} | {Message}", command.User.Username, delivered, message);

        await Reply(command, delivered > 0
            ? Theme.Success("Broadcast sent", $"> {Sanitize.Code(message)}").AddField("Servers", delivered.ToString(System.Globalization.CultureInfo.InvariantCulture), true)
            : Theme.Failure("Not broadcast", "No server accepted the command.")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/health</c> - the bot's own state, from the same registry the endpoint uses.</summary>
public sealed class HealthCommand(HealthRegistry health, ServiceRegistry services, Access access) : ISlashCommand
{
    public string Name => "health";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder().WithName(Name).WithDescription("Owner - Bot health and background services").Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Owner))).ConfigureAwait(false);
            return;
        }

        // The SAME registry /health serves. Two health views that can disagree is worse
        // than one, because then you have to work out which one is lying.
        var report = await health.ReportAsync(ct).ConfigureAwait(false);

        var colour = report.Status switch
        {
            HealthStatus.Healthy => Theme.Green,
            HealthStatus.Degraded => Theme.Amber,
            _ => Theme.BanRed,
        };

        var components = report.Components.Values
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => $"{Glyph(c.Status)} **{c.Name}** — {c.Detail ?? c.Status.ToString().ToLowerInvariant()}");

        var embed = new EmbedBuilder()
            .WithColor(colour)
            .WithTitle($"Health — {report.Status.ToString().ToLowerInvariant()}")
            .WithDescription(string.Join("\n", components));

        var status = services.Status();
        if (status.Count > 0)
        {
            var lines = status.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s =>
                $"{(s.State == ServiceState.Running ? Theme.Up : Theme.Down)} `{s.Name}` — " +
                $"{s.Ticks} ticks, {s.Failures} failed{(s.Overruns > 0 ? $", {s.Overruns} overran" : "")}");
            embed.AddField($"Services ({services.Running}/{services.Count})", string.Join("\n", lines));
        }

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static string Glyph(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => Theme.Ok,
        HealthStatus.Degraded => Theme.Warn,
        _ => Theme.Bad,
    };

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/help</c> - what you can run, at your access level.</summary>
public sealed class HelpCommand(CommandCatalog catalog, Access access) : ISlashCommand
{
    public string Name => "help";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder().WithName(Name).WithDescription("Show the available commands and your access level").Build();

    public Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var level = access.DescribeAccess(command.User as IGuildUser);

        /* Every command is listed, not only the ones this member may run. Hiding them makes
           the bot look broken to somebody who was told a command exists - and the
           permission check still refuses them, so nothing is gained by the secrecy. The
           description carries the tier, so it is obvious why one is greyed out in practice. */
        var lines = catalog.Commands.Select(c => $"`/{c.Name}`\n{c.Description}");

        var pages = Theme.Paginate(lines);

        var embed = Theme.Notice($"{Theme.Info} Commands",
            $"Your access: **{level}**\n\n{pages[0]}")
            .Brand(pages.Count > 1 ? $"Page 1 of {pages.Count}" : null);

        return command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Build();
            m.AllowedMentions = AllowedMentions.None;
        });
    }
}
