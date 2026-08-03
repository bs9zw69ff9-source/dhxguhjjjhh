using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/rotatemap</c> - warn everyone, then restart the game server through systemd.
/// </summary>
/// <remarks>
/// A SERVICE RESTART, NOT AN RCON MAP CHANGE, and that is the whole point of the command.
/// RCON's own rotate switches map inside a process that keeps running; this replaces the
/// process. They are not interchangeable - restarting is what clears a server that has gone
/// bad in a way a map change does not fix, which is the state somebody reaches for this in.
///
/// THE WARNING GOES OUT FIRST. It has to: after the restart the server is gone and everybody
/// on it is already disconnected, so a notice sent afterwards reaches nobody. There is a
/// short grace period between the two so the message renders in-game rather than arriving in
/// the same instant the process dies.
///
/// The warning is sent over RCON to the servers being rotated and its failure is NOT fatal -
/// an unreachable server is often exactly why somebody is restarting it, and refusing to
/// restart because the warning could not be delivered would make the command useless in the
/// one case it exists for.
/// </remarks>
public sealed class RotateMapCommand(
    ServiceControl services,
    RconRegistry rcon,
    Access access,
    AuditLog audit,
    ILogger<RotateMapCommand> logger) : ISlashCommand
{
    public string Name => "rotatemap";

    /// <summary>The exact line broadcast before a restart.</summary>
    public const string Warning = "All Server Rotating...";

    /// <summary>
    /// How long players get between the warning and the process dying.
    /// </summary>
    /// <remarks>
    /// Long enough for the notice to render and be read; short enough that nobody assumes
    /// the command did nothing and runs it again. A restart that begins in the same instant
    /// as its own warning is a restart with no warning.
    /// </remarks>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    public ApplicationCommandProperties Build()
    {
        var server = new SlashCommandOptionBuilder()
            .WithName("server")
            .WithDescription("Which server to restart. Defaults to all of them.")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .AddChoice("All servers", 0);

        /* Choices, not free text. The number picks a CONFIGURED unit by index, so nothing a
           caller types ever reaches the command line of a privileged process. */
        for (var i = 1; i <= services.Units.Count; i++)
            server.AddChoice($"Server {i}", i);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Warn players, then restart the game server(s) via systemd")
            .AddOption(server)
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        /* ADMIN. This disconnects every player on the server and takes it down for as long
           as it takes to come back - the same weight as a ban wave, not the same weight as
           reading a roster. Owners pass this gate automatically. */
        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var choice = (int)(command.Data.Options.FirstOrDefault(o => o.Name == "server")?.Value as long? ?? 0);

        var targets = choice == 0
            ? services.Units.Select((unit, index) => (Number: index + 1, Unit: unit)).ToList()
            : services.UnitFor(choice) is { } one ? [(Number: choice, Unit: one)] : [];

        if (targets.Count == 0)
        {
            await Reply(command, Theme.Failure("No such server",
                $"Only {services.Units.Count} unit(s) are configured. Set `PAVLOV_UNITS` if that is wrong."))
                .ConfigureAwait(false);
            return;
        }

        var who = command.User.Username;
        logger.LogWarning("ROTATEMAP by {User} | {Units}", who, string.Join(", ", targets.Select(t => t.Unit)));

        // ---- 1. warn, before anything goes down ----

        var warned = await WarnAsync(targets.Select(t => t.Number).ToList(), ct).ConfigureAwait(false);

        await Reply(command, Theme.Notice($"{Theme.Warn} Rotating {targets.Count} server(s)",
            $"Broadcast `{Warning}` to {warned} server(s). Restarting in {Grace.TotalSeconds:0}s…\n\n" +
            "This message will update when every unit has finished.")).ConfigureAwait(false);

        await Task.Delay(Grace, ct).ConfigureAwait(false);

        // ---- 2. restart, one at a time ----

        /* SEQUENTIALLY. Restarting three Pavlov servers at once means three simultaneous
           map loads on one box, and staggering them also means a failure on the first is
           visible before the rest are torn down. */
        var results = new List<UnitResult>();
        foreach (var target in targets)
            results.Add(await services.RestartAsync(target.Unit, ct).ConfigureAwait(false));

        // A restarted server has nobody on it; a roster from before the restart is fiction.
        rcon.InvalidateRosters();

        await audit.RecordAsync("rotatemap", who,
            string.Join(", ", targets.Select(t => t.Unit)),
            $"{results.Count(r => r.Ok)}/{results.Count} restarted", ct).ConfigureAwait(false);

        await Reply(command, Report(results, warned, services.Advice(results))).ConfigureAwait(false);
    }

    /// <summary>
    /// Broadcast the warning. Returns how many servers accepted it.
    /// </summary>
    /// <remarks>
    /// Never throws. A server that will not take the warning is very often the server being
    /// restarted BECAUSE it will not take anything, and letting that abort the restart would
    /// disable the command exactly when it is needed.
    /// </remarks>
    private async Task<int> WarnAsync(IReadOnlyList<int> serverNumbers, CancellationToken ct)
    {
        var names = rcon.Servers.ToList();
        var delivered = 0;

        foreach (var number in serverNumbers)
        {
            if (number < 1 || number > names.Count) continue;
            var server = names[number - 1];

            try
            {
                // Sanitize.Message even though this is a constant: the RCON protocol is line
                // oriented, and every string that reaches it goes through the same door.
                await rcon.SendAsync(server, $"Notify {Sanitize.Message(Warning)}", ct).ConfigureAwait(false);
                delivered++;
                logger.LogInformation("Rotation warning delivered to {Server}", server);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Could not warn {Server} before restart: {Message}", server, ex.Message);
            }
        }

        return delivered;
    }

    private static EmbedBuilder Report(IReadOnlyList<UnitResult> results, int warned, string? advice)
    {
        var ok = results.Count(r => r.Ok);

        var embed = ok == results.Count
            ? Theme.Success($"Rotated {ok} server(s)", $"Warned {warned}, restarted {ok}.")
            : Theme.Failure($"Rotated {ok} of {results.Count} server(s)",
                "The units below did not restart. **Players on them may be disconnected with nothing to come back to** — " +
                "check `systemctl status` on the box.");

        foreach (var result in results)
        {
            embed.AddField(
                $"{(result.Ok ? Theme.Ok : Theme.Bad)} {Sanitize.Message(result.Unit)}",
                result.Ok ? "restarted" : $"```\n{Sanitize.Code(Truncate(result.Detail))}\n```");
        }

        /* The fix, not just the failure. A permission error is the EXPECTED outcome on a bot
           that does not run as root, and without this it presents as the command being
           broken. */
        if (advice is not null) embed.AddField($"{Theme.Warn} How to fix this", advice);

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
