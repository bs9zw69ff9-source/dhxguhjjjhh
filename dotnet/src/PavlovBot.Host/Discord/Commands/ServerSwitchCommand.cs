using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/serverswitch</c> - start, stop or restart one game server through systemd.
/// </summary>
/// <remarks>
/// The per-server control panel, where <c>/rotatemap</c> is the all-at-once convenience.
/// Both go through <see cref="ServiceControl"/> so they cannot drift apart about what a
/// server is called or how systemctl is reached.
///
/// STOP IS THE DANGEROUS ONE, and it is the reason this is a separate command rather than
/// another option on <c>/rotatemap</c>. A restart ends with the server back; a stop ends
/// with it down until somebody remembers to start it. Nothing in the bot will bring it back
/// on its own, so the reply says so in as many words.
///
/// A WARNING GOES OUT FIRST for anything that drops players, and only for those. Stopping
/// and restarting disconnect everyone, so they get a broadcast and a grace period; starting
/// a server has nobody to warn and no RCON session to warn them through.
/// </remarks>
public sealed class ServerSwitchCommand(
    ServiceControl services,
    RconRegistry rcon,
    Access access,
    AuditLog audit,
    ILogger<ServerSwitchCommand> logger) : ISlashCommand
{
    public string Name => "serverswitch";

    /// <summary>Ephemeral: this is operator business, not channel business.</summary>
    public bool Ephemeral => true;

    /// <summary>What players are told before the server goes away under them.</summary>
    internal static string WarningFor(UnitAction action) => action switch
    {
        UnitAction.Stop => "Server shutting down...",
        _ => "Server restarting...",
    };

    /// <summary>
    /// How long players get between the warning and the process dying.
    /// </summary>
    /// <remarks>The same five seconds <c>/rotatemap</c> gives, for the same reason.</remarks>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    public ApplicationCommandProperties Build()
    {
        var server = new SlashCommandOptionBuilder()
            .WithName("server")
            .WithDescription("Which server")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(true);

        /* Choices, not free text. The number picks a CONFIGURED unit by index, so nothing a
           caller types ever reaches the command line of a privileged process. */
        for (var i = 1; i <= services.Units.Count; i++)
            server.AddChoice($"Server {i} ({services.Units[i - 1]})", i);

        var action = new SlashCommandOptionBuilder()
            .WithName("action")
            .WithDescription("What to do")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(true)
            .AddChoice("Restart", (int)UnitAction.Restart)
            .AddChoice("Shut down", (int)UnitAction.Stop)
            .AddChoice("Start", (int)UnitAction.Start);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Start, shut down or restart a game server")
            .AddOption(server)
            .AddOption(action)
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        /* ADMIN. Taking a server down is ban-wave weight, not roster-reading weight.
           Owners pass this gate automatically. */
        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var number = (int)(command.Data.Options.FirstOrDefault(o => o.Name == "server")?.Value as long? ?? 0);
        var raw = (int)(command.Data.Options.FirstOrDefault(o => o.Name == "action")?.Value as long? ?? -1);

        /* Validated against the enum rather than cast into it. A value Discord did not offer
           should not become a systemctl verb by falling through a cast. */
        if (!Enum.IsDefined(typeof(UnitAction), raw))
        {
            await Reply(command, Theme.Failure("Unknown action", "Pick one of the offered actions.")).ConfigureAwait(false);
            return;
        }

        var action = (UnitAction)raw;

        if (services.UnitFor(number) is not { } unit)
        {
            await Reply(command, Theme.Failure("No such server",
                $"Only {services.Units.Count} unit(s) are configured. Set `PAVLOV_UNITS` if that is wrong."))
                .ConfigureAwait(false);
            return;
        }

        var who = command.User.Username;
        logger.LogWarning("SERVERSWITCH by {User} | Server {Number} ({Unit}) | {Action}",
            who, number, unit, action);

        // ---- warn, for the actions that drop players ----

        var warned = false;
        if (action is UnitAction.Stop or UnitAction.Restart)
        {
            warned = await WarnAsync(number, WarningFor(action), ct).ConfigureAwait(false);

            await Reply(command, Theme.Notice($"{Theme.Warn} {Verb(action)} Server {number}",
                $"`{unit}` — {(warned ? $"broadcast `{WarningFor(action)}`" : "could not warn players (the server is not answering RCON)")}." +
                $"\n\nGoing ahead in {Grace.TotalSeconds:0}s…")).ConfigureAwait(false);

            await Task.Delay(Grace, ct).ConfigureAwait(false);
        }

        // ---- act ----

        var result = await services.RunAsync(unit, action, ct).ConfigureAwait(false);

        /* A server that has just been stopped, started or restarted has a roster that no
           longer describes anything. Dropping it beats letting /players list people who are
           provably not there. */
        rcon.InvalidateRosters();

        await audit.RecordAsync("serverswitch", who, unit,
            $"{Verb(action).ToLowerInvariant()} - {(result.Ok ? "ok" : result.Detail)}", ct).ConfigureAwait(false);

        await Reply(command, Report(number, unit, action, result, warned, services.Advice([result]))).ConfigureAwait(false);
    }

    private static string Verb(UnitAction action) => action switch
    {
        UnitAction.Start => "Starting",
        UnitAction.Stop => "Shutting down",
        _ => "Restarting",
    };

    /// <summary>
    /// Broadcast to the one server being acted on. True when it was accepted.
    /// </summary>
    /// <remarks>
    /// Never throws, and a failure never blocks the action. A server that will not take a
    /// warning is very often exactly the server somebody is trying to stop.
    /// </remarks>
    private async Task<bool> WarnAsync(int serverNumber, string message, CancellationToken ct)
    {
        var names = rcon.Servers.ToList();
        if (serverNumber < 1 || serverNumber > names.Count) return false;

        var server = names[serverNumber - 1];
        try
        {
            // Sanitize.Message even for a constant: the RCON protocol is line oriented, and
            // every string that reaches it goes through the same door.
            await rcon.SendAsync(server, $"Notify {Sanitize.Message(message)}", ct).ConfigureAwait(false);
            logger.LogInformation("Warned {Server}: {Message}", server, message);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not warn {Server}: {Message}", server, ex.Message);
            return false;
        }
    }

    private static EmbedBuilder Report(
        int number, string unit, UnitAction action, UnitResult result, bool warned, string? advice)
    {
        var title = $"Server {number} — {Verb(action).ToLowerInvariant()}";

        var embed = result.Ok
            ? Theme.Success($"{title} ✓", $"`{Sanitize.Code(unit)}` is {Past(action)}.")
            : Theme.Failure($"{title} failed", $"`{Sanitize.Code(unit)}` did not respond to `systemctl {ServiceControl.Verb(action)}`.");

        if (result.Ok && action is UnitAction.Stop)
        {
            /* Said explicitly, because it is the one outcome here with no self-correcting
               end. Nothing in the bot restarts a stopped server, and a server quietly down
               overnight is the expensive version of this command working perfectly. */
            embed.AddField($"{Theme.Warn} It will stay down",
                "Nothing brings it back on its own — run `/serverswitch` again with **Start** when you want it up.");
        }

        if (action is UnitAction.Stop or UnitAction.Restart)
            embed.AddField("Players warned", warned ? "Yes" : "No — the server was not answering RCON", inline: true);

        if (!result.Ok) embed.AddField("systemd said", $"```\n{Sanitize.Code(Truncate(result.Detail))}\n```");

        // The fix, not just the failure. A permission error is the expected outcome on a bot
        // that is not root, and without this it presents as the command being broken.
        if (advice is not null) embed.AddField($"{Theme.Warn} How to fix this", advice);

        return embed;
    }

    private static string Past(UnitAction action) => action switch
    {
        UnitAction.Start => "started",
        UnitAction.Stop => "stopped",
        _ => "back up",
    };

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "\n…";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
