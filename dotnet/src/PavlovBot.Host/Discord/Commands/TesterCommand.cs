using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/tester</c> - grant or revoke access on Pavlov's own whitelist, on every install.
/// </summary>
/// <remarks>
/// The manual version of this is <c>nano</c> on three files in turn, and the failure it
/// removes is doing two of them: a tester whitelisted on servers 1 and 2 and rejected by
/// server 3 looks, from their side, like the server being broken.
///
/// READ-ONLY. whitelist.txt belongs to the game server, and the bot does not write files the
/// server owns. So this shows what is on each install's whitelist and gives back the exact
/// line to paste - which is most of the value, because the two things that actually go wrong
/// by hand are editing only two installs out of three and mistyping the name.
///
/// ONE USERNAME PER LINE, exactly as it appears in game. An earlier version resolved the
/// name to the account id the bot had recorded, on the assumption the file was keyed the way
/// the ban list is. It is not - that would have put an id into a file matched on name,
/// accepted by everything that touches it and matching nobody.
/// </remarks>
public sealed class TesterCommand(
    WhitelistFile whitelist,
    Access access,
    IReadOnlyList<string> installs,
    ILogger<TesterCommand> logger) : ISlashCommand
{
    public string Name => "tester";

    /// <summary>Ephemeral: who is being trialled is not usually channel business.</summary>
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build()
    {
        static SlashCommandOptionBuilder Player() =>
            new SlashCommandOptionBuilder()
                .WithName("playerid").WithDescription("In-game username, exactly as it appears")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Check the whitelist and get the line to add")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("check").WithDescription("Is this player whitelisted, and what line adds them")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list").WithDescription("Who is whitelisted, per server")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        /* ADMIN, not Mod. This grants access to a server somebody is otherwise locked out
           of, which is a trust decision rather than a moderation one. */
        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();

        if ((sub?.Name ?? "list") == "list")
        {
            await Reply(command, ListEmbed()).ConfigureAwait(false);
            return;
        }

        var entry = WhitelistFile.Entry(sub?.Options.FirstOrDefault(o => o.Name == "playerid")?.Value as string);
        if (entry.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        /* PER INSTALL, because the failure this exists to catch is a whitelist edited on two
           servers out of three - from the player's side that is indistinguishable from the
           third server being broken. */
        var listed = installs
            .Select(i => (Install: Path.GetFileName(i), Path: PavlovInstalls.WhitelistPath(i)))
            .Select(x => (x.Install, x.Path,
                Has: whitelist.Read(x.Path).Contains(entry, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var missing = listed.Count(x => !x.Has);

        var embed = missing == 0
            ? Theme.Success($"{Sanitize.Code(entry)} is whitelisted everywhere",
                string.Join("\n", listed.Select(x => $"{Theme.Ok} `{x.Install}` — whitelisted")))
            : Theme.Warning($"{Sanitize.Code(entry)} is missing from {missing} server(s)",
                string.Join("\n", listed.Select(x =>
                    $"{(x.Has ? Theme.Ok : Theme.Bad)} `{x.Install}` — {(x.Has ? "whitelisted" : "NOT whitelisted")}")));

        if (missing > 0)
        {
            embed.AddField("Add them by hand",
                "The bot does not write files the game server owns. Append this line to each " +
                $"whitelist below that is missing it:\n```\n{Sanitize.Code(entry)}\n```");

            foreach (var x in listed.Where(x => !x.Has))
                embed.AddField(x.Install, $"`{x.Path}`");
        }

        logger.LogInformation("tester check | \"{Entry}\" | missing on {Missing}/{Total}",
            entry, missing, installs.Count);

        await Reply(command, embed).ConfigureAwait(false);
    }

    private EmbedBuilder ListEmbed()
    {
        var sections = installs.Select(install =>
        {
            var path = PavlovInstalls.WhitelistPath(install);
            var entries = whitelist.Read(path);

            var body = entries.Count == 0
                ? "*empty*"
                : string.Join(" ", entries.Take(40).Select(e => $"`{Sanitize.Code(e)}`")) +
                  (entries.Count > 40 ? $" *and {entries.Count - 40} more*" : "");

            return $"**{Path.GetFileName(install)}** — {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}\n{body}";
        });

        return Theme.Notice("Whitelist", string.Join("\n\n", sections));
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
