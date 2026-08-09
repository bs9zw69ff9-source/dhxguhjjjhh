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
/// IT WRITES AGAIN. <c>add</c> and <c>remove</c> were turned into a read-only check under a
/// blanket "the bot does not write files the game server owns" rule. That rule is real, but
/// it rests on the server holding a value in memory and rewriting the file from it - which is
/// true of player ledgers and is not true of whitelist.txt, a config input the server reads
/// and never writes back. It sits in the same directory as the faction roster .txt files the
/// bot has always written, in the same format. See <see cref="GameFiles"/>.
///
/// What the rule was really protecting against is handled where it belongs, in
/// <see cref="WhitelistFile"/>: an atomic write, every unrelated line preserved, a pre-write
/// backup, a guard against a write that deletes implausibly much, and never creating a
/// directory inside an install.
///
/// ONE USERNAME PER LINE, exactly as it appears in game. An earlier version resolved the
/// name to the account id the bot had recorded, on the assumption the file was keyed the way
/// the ban list is. It is not - that would have put an id into a file matched on name,
/// accepted by everything that touches it and matching nobody.
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
            .WithDescription("Admin - Manage Pavlov's own whitelist across every install")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("add").WithDescription("Whitelist a player on every server")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove").WithDescription("Take a player off every server's whitelist")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player()))
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

        if (sub!.Name is "add" or "remove")
        {
            await EditAsync(command, entry, removing: sub.Name == "remove", ct).ConfigureAwait(false);
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

    /// <summary>
    /// Add or remove on EVERY install, and report each one separately.
    /// </summary>
    /// <remarks>
    /// PARTIAL SUCCESS IS THE INTERESTING CASE and it gets its own colour. "Whitelisted" when
    /// one of three servers rejected the write is the report that has somebody told their
    /// access works, and then told the server is broken when they land on the third one.
    /// That is the exact failure this command exists to remove, so it must not reintroduce it
    /// by summarising.
    ///
    /// The installs are edited in SEQUENCE rather than in parallel. There are three of them,
    /// the writes are milliseconds, and a serial loop means a failure part-way through leaves
    /// a state somebody can reason about from the log.
    /// </remarks>
    private async Task EditAsync(SocketSlashCommand command, string entry, bool removing, CancellationToken ct)
    {
        var results = new List<WhitelistResult>();
        foreach (var install in installs)
        {
            results.Add(removing
                ? await whitelist.RemoveAsync(PavlovInstalls.WhitelistPath(install), entry, ct).ConfigureAwait(false)
                : await whitelist.AddAsync(PavlovInstalls.WhitelistPath(install), entry, ct).ConfigureAwait(false));
        }

        var failed = results.Where(r => !r.Ok).ToList();
        var changed = results.Count(r => r.Changed);
        var verb = removing ? "removed from" : "whitelisted on";

        var lines = results.Select(r =>
            $"{(r.Ok ? Theme.Ok : Theme.Bad)} `{r.Install}` — " +
            (r.Ok ? r.Changed ? "done" : removing ? "was not on it" : "already on it" : $"FAILED: {r.Error}"));

        var embed = failed.Count == 0
            ? Theme.Success($"{Sanitize.Code(entry)} {verb} {results.Count} server(s)", string.Join("\n", lines))
            : Theme.Warning($"{Sanitize.Code(entry)} — {failed.Count} of {results.Count} server(s) failed",
                string.Join("\n", lines));

        if (failed.Count > 0)
        {
            /* The line and the path, on the failure, so the fallback is right there. The
               commonest cause is a path that does not exist or is not writable by the bot
               user, and neither is fixable from Discord. */
            embed.AddField("Finish these by hand",
                $"Append this line to each failed whitelist:\n```\n{Sanitize.Code(entry)}\n```");

            foreach (var r in failed) embed.AddField(r.Install, $"`{r.Path}`");
        }

        await audit.RecordAsync(removing ? "tester-remove" : "tester-add", command.User.Username, entry,
            $"{changed} of {results.Count} install(s) changed", ct).ConfigureAwait(false);

        logger.LogInformation("tester {Action} | \"{Entry}\" | changed {Changed}/{Total} | failed {Failed} | by={By}",
            removing ? "remove" : "add", entry, changed, results.Count, failed.Count, command.User.Username);

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
