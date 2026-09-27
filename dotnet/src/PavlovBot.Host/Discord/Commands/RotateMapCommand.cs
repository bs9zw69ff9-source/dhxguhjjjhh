using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/rotatemap</c> - warn everyone, then send <c>RotateMap</c> over RCON.
/// </summary>
/// <remarks>
/// AN RCON MAP CHANGE, NOT A SERVICE RESTART. It used to run <c>systemctl restart</c>; the
/// process-replacing restart now lives only in <c>/serverswitch restart</c>, for a server that
/// has gone bad in a way a map change does not fix.
///
/// THE WARNING GOES OUT FIRST, with a short grace period before the rotation so the message
/// renders in-game rather than arriving in the same instant the map unloads. Its failure is
/// not fatal on its own: the rotation is still attempted and reported per server, so an
/// unreachable server shows up as a failed rotation rather than as a refused command.
/// </remarks>
public sealed class RotateMapCommand(
    ServiceControl services,
    PlayerNotice notice,
    MapRotation rotation,
    Access access,
    AuditLog audit,
    ILogger<RotateMapCommand> logger) : ISlashCommand
{
    public string Name => "rotatemap";

    /// <summary>The exact line broadcast before a rotation.</summary>
    /// <remarks>
    /// The message only. <see cref="PlayerNotice"/> addresses it to every player, so the
    /// leading "All" this used to carry is gone - kept here it would go out twice, and it
    /// was never part of the sentence in the first place.
    /// </remarks>
    public const string Warning = "Server Rotating... Please rejoin after disconnect";

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
            .WithDescription("Admin - Warn players, then rotate the map on the game server(s) via RCON")
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

        // ---- 1. warn, before the map changes ----

        var notices = new List<NoticeResult>();
        foreach (var target in targets)
            notices.Add(await notice.WarnAsync(target, Warning, ct).ConfigureAwait(false));

        var warned = notices.Count(n => n.Delivered);

        await Reply(command, Theme.Notice($"{Theme.Warn} Rotating {targets.Count} server(s)",
            $"Broadcast `{Warning}` to {warned} of {targets.Count} server(s). " +
            $"Rotating in {PlayerNotice.Grace.TotalSeconds:0}s…")).ConfigureAwait(false);

        /* THE PAUSE IS THE POINT. The broadcast has to reach the client and render before
           the map it is warning about unloads - sent and rotated in the same instant, the
           player sees nothing. */
        await PlayerNotice.WaitAsync(ct).ConfigureAwait(false);

        // ---- 2. rotate, one at a time ----

        /* SEQUENTIALLY. Three map loads at once on one box is three times the load spike, and
           staggering them means a failure on the first is visible before the rest go. */
        var results = new List<RotationResult>();
        foreach (var target in targets)
            results.Add(await rotation.RotateAsync(target, ct).ConfigureAwait(false));

        await audit.RecordAsync("rotatemap", who,
            string.Join(", ", targets.Select(ServiceControl.RconNameFor)),
            $"{results.Count(r => r.Ok)}/{results.Count} rotated", ct).ConfigureAwait(false);

        await Reply(command, Report(results, notices)).ConfigureAwait(false);
    }

    private static EmbedBuilder Report(IReadOnlyList<RotationResult> results, IReadOnlyList<NoticeResult> notices)
    {
        var ok = results.Count(r => r.Ok);
        var warned = notices.Count(n => n.Delivered);

        var embed = ok == results.Count
            ? Theme.Success($"Rotated {ok} server(s)",
                $"Warned {warned} of {notices.Count} {PlayerNotice.Grace.TotalSeconds:0}s beforehand, rotated {ok}.")
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

        /* WHY a warning did not land, per server. It used to report only a count, so
           "warned 1 of 3" gave no way to tell an unconfigured RCON slot apart from a server
           that was refusing connections. */
        if (notices.Any(n => !n.Delivered))
        {
            embed.AddField($"{Theme.Warn} Players not warned",
                string.Join("\n", notices
                    .Select((n, i) => (Notice: n, Number: i + 1))
                    .Where(x => !x.Notice.Delivered)
                    .Select(x => $"{Theme.Dot} Server {x.Number} — {Sanitize.Message(x.Notice.Detail)}")));
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
