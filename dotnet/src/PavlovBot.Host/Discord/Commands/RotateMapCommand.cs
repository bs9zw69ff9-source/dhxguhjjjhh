using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/rotatemap</c> - send <c>RotateMap</c> over RCON, and nothing else.
/// </summary>
/// <remarks>
/// AN RCON MAP CHANGE, NOT A SERVICE RESTART. It used to run <c>systemctl restart</c>; the
/// process-replacing restart now lives only in <c>/serverswitch restart</c>, for a server that
/// has gone bad in a way a map change does not fix.
///
/// NO WARNING AND NO GRACE PERIOD. The <c>Notify</c> broadcast and the five-second pause
/// belonged to the restart, which dropped everybody; a map change does not, so the rotation
/// goes out immediately. <c>/serverswitch</c> still warns before anything that drops players.
/// </remarks>
public sealed class RotateMapCommand(
    ServiceControl services,
    MapRotation rotation,
    Access access,
    AuditLog audit,
    ILogger<RotateMapCommand> logger) : ISlashCommand
{
    public string Name => "rotatemap";

    public ApplicationCommandProperties Build()
    {
        var server = new SlashCommandOptionBuilder()
            .WithName("server")
            .WithDescription("Which server to rotate. Defaults to all of them.")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .AddChoice("All servers", 0);

        /* Choices, not free text, numbered the same way /serverswitch numbers servers. The
           number picks RCON slot `serverN`; nothing a caller types reaches the wire. */
        for (var i = 1; i <= services.Units.Count; i++)
            server.AddChoice($"Server {i}", i);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Rotate the map on the game server(s) via RCON")
            .AddOption(server)
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        /* ADMIN. This ends the round for every player on the server - the same weight as a
           ban wave, not the same weight as reading a roster. Owners pass this gate
           automatically. */
        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var choice = (int)(command.Data.Options.FirstOrDefault(o => o.Name == "server")?.Value as long? ?? 0);

        List<int> targets = choice == 0
            ? [.. Enumerable.Range(1, services.Units.Count)]
            : choice >= 1 && choice <= services.Units.Count ? [choice] : [];

        if (targets.Count == 0)
        {
            await Reply(command, Theme.Failure("No such server",
                $"Only {services.Units.Count} unit(s) are configured. Set `PAVLOV_UNITS` if that is wrong."))
                .ConfigureAwait(false);
            return;
        }

        var who = command.User.Username;
        logger.LogWarning("ROTATEMAP by {User} | {Units}", who, string.Join(", ", targets.Select(ServiceControl.RconNameFor)));

        await Reply(command, Theme.Notice($"{Theme.Warn} Rotating {targets.Count} server(s)",
            $"Sending `{MapRotation.Command}`…")).ConfigureAwait(false);

        /* SEQUENTIALLY. Three map loads at once on one box is three times the load spike, and
           staggering them means a failure on the first is visible before the rest go. */
        var results = new List<RotationResult>();
        foreach (var target in targets)
            results.Add(await rotation.RotateAsync(target, ct).ConfigureAwait(false));

        await audit.RecordAsync("rotatemap", who,
            string.Join(", ", targets.Select(ServiceControl.RconNameFor)),
            $"{results.Count(r => r.Ok)}/{results.Count} rotated", ct).ConfigureAwait(false);

        await Reply(command, Report(results)).ConfigureAwait(false);
    }

    private static EmbedBuilder Report(IReadOnlyList<RotationResult> results)
    {
        var ok = results.Count(r => r.Ok);

        var embed = ok == results.Count
            ? Theme.Success($"Rotated {ok} server(s)", $"`{MapRotation.Command}` accepted by {ok} server(s).")
            : Theme.Failure($"Rotated {ok} of {results.Count} server(s)",
                "The servers below did not confirm the rotation.");

        foreach (var result in results)
        {
            var mark = result.Outcome switch
            {
                RotationOutcome.Rotated => Theme.Ok,
                RotationOutcome.Unconfirmed => Theme.Warn,
                _ => Theme.Bad,
            };
            embed.AddField($"{mark} Server {result.Number}", Sanitize.Message(Truncate(result.Detail)));
        }

        return embed;
    }

    private static string Truncate(string text) =>
        text.Length <= 400 ? text : text[..400] + "\n…";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
