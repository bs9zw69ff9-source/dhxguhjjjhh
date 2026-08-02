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
/// ONE USERNAME PER LINE, written exactly as given. An earlier version resolved the name to
/// the account id the bot had recorded for it, on the assumption that the file was keyed the
/// way the ban list is. It is not, and that would have written an id into a file matched on
/// name - accepted by everything that touches it, and matching nobody.
/// </remarks>
public sealed class TesterCommand(
    WhitelistFile whitelist,
    Access access,
    AuditLog audit,
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
            .WithDescription("Admin - Whitelist a tester on every server")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("add").WithDescription("Give a player whitelist access on every server")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove").WithDescription("Take whitelist access away on every server")
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
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Admin))).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        var action = sub?.Name ?? "list";

        if (action == "list")
        {
            await Reply(command, ListEmbed()).ConfigureAwait(false);
            return;
        }

        /* The username, VERBATIM apart from what cannot go in a line of this file. The file
           is matched against the player's actual in-game name, so anything else written
           here matches nobody. */
        var entry = WhitelistFile.Entry(sub?.Options.FirstOrDefault(o => o.Name == "playerid")?.Value as string);
        if (entry.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        var results = new List<WhitelistResult>();
        foreach (var install in installs)
        {
            var path = PavlovInstalls.WhitelistPath(install);
            results.Add(action == "add"
                ? await whitelist.AddAsync(path, entry, ct).ConfigureAwait(false)
                : await whitelist.RemoveAsync(path, entry, ct).ConfigureAwait(false));
        }

        await audit.RecordAsync($"tester-{action}", command.User.Username, entry, entry, ct).ConfigureAwait(false);
        logger.LogInformation("tester {Action} | \"{Entry}\" | {Ok}/{Total} install(s)",
            action, entry, results.Count(r => r.Ok), results.Count);

        await Reply(command, Summarise(action, entry, results)).ConfigureAwait(false);
    }

    private static EmbedBuilder Summarise(string action, string entry, IReadOnlyList<WhitelistResult> results)
    {
        var failed = results.Where(r => !r.Ok).ToList();
        var changed = results.Count(r => r.Changed);
        var verb = action == "add" ? "Whitelisted" : "Removed from the whitelist";

        var lines = results.Select(r =>
            $"{(r.Ok ? Theme.Ok : Theme.Bad)} `{r.Install}` — " +
            (r.Ok ? (r.Changed ? "written" : "already correct") : r.Error ?? "failed"));

        /* Partial failure is called out at the top rather than left to be spotted in the
           list. Two installs out of three is the state that gets somebody told their access
           works when one server will still reject them. */
        var embed = failed.Count > 0
            ? Theme.Failure($"{verb} on {results.Count - failed.Count}/{results.Count} server(s)",
                $"`{Sanitize.Code(entry)}`\n\n" + string.Join("\n", lines))
            : Theme.Success($"{verb} — {Sanitize.Code(entry)}",
                $"On **{results.Count}** server(s)" +
                (changed == 0 ? " *(nothing to change)*" : "") + "\n\n" + string.Join("\n", lines));

        if (action == "add")
        {
            /* The name has to match EXACTLY, and the person running this is typing it from
               memory or from a Discord handle that may not be the in-game one. */
            embed.AddField("Note",
                "Written exactly as typed — it must match their in-game name character for character. " +
                "The whitelist also only applies when the server has it enabled.");
        }

        return embed;
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
