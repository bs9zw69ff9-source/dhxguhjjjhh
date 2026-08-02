using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Logs;
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
/// WHAT PAVLOV MATCHES ON: the whitelist is keyed by UNIQUE ID, not by display name. So a
/// name is resolved to the account id the bot has recorded for it, and when there is no
/// recorded id the entry is still written but the reply says plainly that it may not take
/// effect - a silently ineffective grant is the whole failure mode worth preventing here.
/// </remarks>
public sealed class TesterCommand(
    WhitelistFile whitelist,
    IpTrackingService tracking,
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
                .WithName("playerid").WithDescription("Player name or unique ID")
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

        var typed = Sanitize.Id(sub?.Options.FirstOrDefault(o => o.Name == "playerid")?.Value as string ?? "");
        if (typed.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        var (entry, resolved) = Resolve(typed);

        var results = new List<WhitelistResult>();
        foreach (var install in installs)
        {
            var path = PavlovInstalls.WhitelistPath(install);
            results.Add(action == "add"
                ? await whitelist.AddAsync(path, entry, ct).ConfigureAwait(false)
                : await whitelist.RemoveAsync(path, entry, ct).ConfigureAwait(false));
        }

        await audit.RecordAsync($"tester-{action}", command.User.Username, typed, entry, ct).ConfigureAwait(false);
        logger.LogInformation("tester {Action} | {Player} -> {Entry} | {Ok}/{Total} install(s)",
            action, typed, entry, results.Count(r => r.Ok), results.Count);

        await Reply(command, Summarise(action, typed, entry, resolved, results)).ConfigureAwait(false);
    }

    /// <summary>
    /// What to actually write: the recorded account id when there is one.
    /// </summary>
    /// <remarks>
    /// PURE apart from the lookup, and separated because getting it wrong is invisible - a
    /// display name in a file keyed by unique id is accepted by every tool that touches it
    /// and simply never matches a player.
    /// </remarks>
    private (string Entry, bool Resolved) Resolve(string typed)
    {
        // Already an id: EOS ids and Steam ids are long and have no spaces. Passing one
        // through untouched means an operator can always bypass the lookup.
        if (LooksLikeId(typed)) return (typed, true);

        return tracking.AccountByName(typed) is { Id.Length: > 0 } account
            ? (account.Id, true)
            : (typed, false);
    }

    /// <summary>A unique id, rather than a display name.</summary>
    internal static bool LooksLikeId(string value) =>
        value.Length >= 16 && !value.Contains(' ', StringComparison.Ordinal) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private EmbedBuilder Summarise(
        string action, string typed, string entry, bool resolved, IReadOnlyList<WhitelistResult> results)
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
                $"**{Sanitize.Code(typed)}** — `{Sanitize.Code(entry)}`\n\n" + string.Join("\n", lines))
            : Theme.Success($"{verb} — {Sanitize.Code(typed)}",
                $"`{Sanitize.Code(entry)}` on **{results.Count}** server(s)" +
                (changed == 0 ? " *(nothing to change)*" : "") + "\n\n" + string.Join("\n", lines));

        if (!resolved && action == "add")
        {
            /* The silently-ineffective case, said out loud. */
            embed.AddField($"{Theme.Warn} This may not work",
                "Pavlov matches the whitelist on **unique ID**, and no ID has been recorded for that name yet. " +
                "The line was written as typed. Have them connect once so the bot learns their ID, then run " +
                "`/tester add` again — or pass the ID directly.");
        }

        if (action == "add")
        {
            embed.AddField("Note",
                "The whitelist only applies when the server has it enabled, and Pavlov reads the file at map " +
                "change — so this usually takes effect on the next rotation rather than immediately.");
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
