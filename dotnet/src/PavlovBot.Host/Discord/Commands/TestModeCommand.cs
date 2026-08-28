using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Provisioning;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/testmode</c> - put a server behind its whitelist, or take it back off.
/// </summary>
/// <remarks>
/// Flips <c>bWhitelist</c> in that server's <c>Game.ini</c> and restarts it, because the game
/// reads that file at start and nothing else makes the change take. With it on, only the names
/// in <c>Pavlov/Saved/Config/whitelist.txt</c> can join - which is what makes it a test mode:
/// the server stays up and listed while everyone who is not on the list is turned away.
///
/// THE EMPTY WHITELIST IS THE TRAP, and it is why the roster is counted before anything is
/// written. Turning this on with nothing in the file locks out every single player INCLUDING the
/// person who ran the command, and the only way back is another command against a server nobody
/// can get into. So an empty list is refused rather than obeyed.
///
/// A LINE EDIT, NOT A REWRITE. The file is hand-tuned - map rotations, tick rate - and
/// regenerating it from the provisioning template would quietly discard all of that every time a
/// single flag was flipped.
/// </remarks>
public sealed class TestModeCommand(
    ServiceControl services,
    PlayerNotice notice,
    WhitelistFile whitelist,
    GameFileGuard guard,
    Access access,
    AuditLog audit,
    IReadOnlyList<string> installs,
    ILogger<TestModeCommand> logger) : ISlashCommand
{
    public string Name => "testmode";

    /// <summary>Ephemeral: operator business, and it restarts a server.</summary>
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build()
    {
        var server = new SlashCommandOptionBuilder()
            .WithName("server")
            .WithDescription("Which server")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(true);

        // Choices, not free text - the number picks a CONFIGURED unit by index, exactly as
        // /serverswitch does, so nothing typed here reaches a privileged command line.
        for (var i = 1; i <= services.Units.Count; i++)
            server.AddChoice($"Server {i} ({services.Units[i - 1]})", i);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Whitelist-only test mode: only names in whitelist.txt may join")
            .AddOption(server)
            .AddOption("enabled", ApplicationCommandOptionType.Boolean,
                "True locks the server to the whitelist, False reopens it", isRequired: true)
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        /* ADMIN, the same tier as /serverswitch - this restarts a server, and the restart is the
           more disruptive half of what it does. */
        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var number = (int)(command.Data.Options.FirstOrDefault(o => o.Name == "server")?.Value as long? ?? 0);
        var enabled = command.Data.Options.FirstOrDefault(o => o.Name == "enabled")?.Value as bool? ?? false;

        if (services.UnitFor(number) is not { } unit)
        {
            await Reply(command, Theme.Failure("No such server",
                $"Only {services.Units.Count} unit(s) are configured. Set `PAVLOV_UNITS` if that is wrong.")).ConfigureAwait(false);
            return;
        }

        if (number > installs.Count)
        {
            await Reply(command, Theme.Failure("No install for that server",
                $"Server {number} has a unit but no install path. Set `PAVLOV_BASES` so the two line up.")).ConfigureAwait(false);
            return;
        }

        var install = installs[number - 1];
        var gameIni = PavlovInstalls.GameIniPath(install);
        var whitelistPath = PavlovInstalls.WhitelistPath(install);

        // IGNORE_PATHS applies here as anywhere else: a second bot must not edit the config of an
        // install that is not its own.
        if (guard.Problem(gameIni) is { } refused)
        {
            await Reply(command, Theme.Failure("Refused", refused)).ConfigureAwait(false);
            return;
        }

        var listed = whitelist.Read(whitelistPath);

        /* THE EMPTY-WHITELIST TRAP. Locking a server to a list nobody is on turns away every
           player including whoever ran this, and the way back is another command against a
           server no one can reach. Refused rather than obeyed. */
        if (enabled && listed.Count == 0)
        {
            await Reply(command, Theme.Failure("The whitelist is empty",
                $"Turning test mode on now would lock out **everyone**, yourself included.\n" +
                $"Add names first - `/tester add` writes to this file:\n`{Sanitize.Code(whitelistPath)}`")).ConfigureAwait(false);
            return;
        }

        string updated;
        try
        {
            if (!File.Exists(gameIni))
            {
                await Reply(command, Theme.Failure("No Game.ini",
                    $"`{Sanitize.Code(gameIni)}` does not exist, so there is nothing to change. " +
                    "Start the server once so it writes its config, or provision it with `/provisionserver`.")).ConfigureAwait(false);
                return;
            }

            var existing = await File.ReadAllTextAsync(gameIni, ct).ConfigureAwait(false);
            updated = ProvisionText.SetGameIniValue(existing, "bWhitelist", enabled ? "true" : "false");

            if (string.Equals(existing, updated, StringComparison.Ordinal))
            {
                await Reply(command, Theme.Notice($"Already {(enabled ? "on" : "off")}",
                    $"`bWhitelist` on server {number} is already `{enabled.ToString().ToLowerInvariant()}`. " +
                    "Nothing was changed and the server was not restarted.")).ConfigureAwait(false);
                return;
            }

            await AtomicFile.WriteAsync(gameIni, updated, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not update {Path}", gameIni);
            await Reply(command, Theme.Failure("Could not write Game.ini", Sanitize.Code(ex.Message))).ConfigureAwait(false);
            return;
        }

        var who = command.User.Username;
        await audit.RecordAsync("testmode", who, unit, enabled ? "on" : "off", ct).ConfigureAwait(false);
        logger.LogWarning("TESTMODE {State} by {User} | server {Number} ({Unit}) | {Listed} on the whitelist",
            enabled ? "ON" : "OFF", who, number, unit, listed.Count);

        /* THE RESTART IS THE POINT. Game.ini is read at start, so the file change on its own
           does nothing - and a command that edited the config and left the server running as it
           was would look like it had worked. Players get the same warning and grace period
           /serverswitch gives, because this drops them just as surely. */
        var warned = await notice.WarnAsync(number, enabled ? "Server going whitelist-only..." : "Server restarting...", ct)
            .ConfigureAwait(false);

        await Reply(command, Theme.Notice($"{Theme.Warn} Test mode {(enabled ? "on" : "off")} for server {number}",
            $"`bWhitelist={enabled.ToString().ToLowerInvariant()}` written to `{Sanitize.Code(gameIni)}`.\n" +
            $"Restarting `{unit}` in {PlayerNotice.Grace.TotalSeconds:0}s so the game reads it…")).ConfigureAwait(false);

        await PlayerNotice.WaitAsync(ct).ConfigureAwait(false);

        var result = await services.RunAsync(unit, UnitAction.Restart, ct).ConfigureAwait(false);

        await Reply(command, Report(number, unit, enabled, listed.Count, whitelistPath, warned, result, services.Advice([result])))
            .ConfigureAwait(false);
    }

    private static EmbedBuilder Report(
        int number, string unit, bool enabled, int listed, string whitelistPath,
        NoticeResult warned, UnitResult result, string? advice)
    {
        var state = enabled ? "on" : "off";

        var embed = result.Ok
            ? Theme.Success($"Test mode {state} — server {number}",
                enabled
                    ? $"`{Sanitize.Code(unit)}` is back up and will only admit the **{listed}** name(s) on its whitelist."
                    : $"`{Sanitize.Code(unit)}` is back up and open to everyone again.")
            : Theme.Failure($"Test mode {state} — server {number} did not restart",
                $"`bWhitelist={state switch { "on" => "true", _ => "false" }}` IS written, but `{Sanitize.Code(unit)}` " +
                "did not come back - so the change has NOT taken effect yet.");

        if (enabled)
            embed.AddField("Who may join", $"The names in `{Sanitize.Code(whitelistPath)}` — add more with `/tester add`.", inline: false);

        embed.AddField("Players warned",
            warned.Delivered ? $"Yes, {PlayerNotice.Grace.TotalSeconds:0}s beforehand" : $"No — {warned.Detail}",
            inline: true);

        if (!result.Ok) embed.AddField("systemd said", $"```\n{Sanitize.Code(Truncate(result.Detail))}\n```");
        if (advice is not null) embed.AddField($"{Theme.Warn} How to fix this", advice);

        return embed;
    }

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "\n…";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
