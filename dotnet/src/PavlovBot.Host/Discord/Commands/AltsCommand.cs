using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Evasion;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Logs;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/alts</c> - the accounts that share an address with this one.
/// </summary>
/// <remarks>
/// THE DATA WAS ALREADY THERE. <see cref="IpTrackingService.AltsOf"/> has always computed
/// this - the connection card shows a "Possible alts" line built from it - but there was no
/// way to ASK. Staff could see the answer for whoever happened to be joining and never for
/// the player they were actually investigating.
///
/// EVIDENCE, NOT PROOF, and the wording says so on every reply. Households, phone tethering,
/// student halls and shared VPN exits all put unrelated people behind one address. Alt
/// detection here is a reason to look closer; it is not grounds on its own, and a command
/// that reads like a verdict would be used as one.
///
/// MOD-GATED. It does not print addresses - those stay in <c>/iplookup</c> - but "these two
/// accounts are the same person" is exactly as sensitive as the address that proves it.
/// </remarks>
public sealed class AltsCommand(IpTrackingService tracking, Access access) : ISlashCommand
{
    public string Name => "alts";

    /// <summary>Enough to act on, short of pasting a whole neighbourhood into a channel.</summary>
    private const int MaxRows = 15;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Moderation - Accounts that share an address with this player")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("playerid").WithDescription("Player ID or username")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true))
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var query = Sanitize.Id(command.Data.Options.First().Value as string ?? "");
        if (query.Length == 0)
        {
            await Reply(command, Theme.Failure("Nothing to look up")).ConfigureAwait(false);
            return;
        }

        // By id first, then by name - staff type whichever they have in front of them, and
        // an account id looks nothing like a name so there is no ambiguity to resolve.
        var subject = tracking.Account(query) ?? tracking.AccountByName(query);

        if (subject is null)
        {
            await Reply(command, Theme.Notice("Never seen",
                $"No account on record for **{Sanitize.Code(query)}**.\n" +
                "The bot only knows players it has watched connect.")).ConfigureAwait(false);
            return;
        }

        await Reply(command, Build(subject, tracking.AltsOf(subject.Id))).ConfigureAwait(false);
    }

    private static EmbedBuilder Build(AccountRecord subject, IReadOnlyList<AccountRecord> alts)
    {
        var name = subject.Names.Count > 0 ? subject.Names[0] : subject.Id;

        var header =
            $"**{Sanitize.Code(name)}**\n" +
            $"`{Sanitize.Code(subject.Id)}`";

        if (alts.Count == 0)
        {
            return Theme.Success($"{Theme.Ok} No shared addresses", header)
                .AddField("Confirmed addresses", subject.ConfirmedIps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), true)
                .AddField("Known names", Names(subject), true)
                .AddField("Seen", Seen(subject), true)
                .Brand("No other account has connected from a confirmed address of theirs");
        }

        var shared = subject.ConfirmedIps.ToHashSet(StringComparer.Ordinal);

        var rows = alts
            // Most overlapping addresses first: one shared address is a household, four is
            // the same person on the same connection.
            .Select(a => (Account: a, Overlap: a.ConfirmedIps.Count(shared.Contains)))
            .OrderByDescending(r => r.Overlap)
            .ThenByDescending(r => r.Account.LastSeen ?? DateTimeOffset.MinValue)
            .Take(MaxRows)
            .ToList();

        var lines = rows.Select(r =>
            $"**{Sanitize.Code(r.Account.Names.Count > 0 ? r.Account.Names[0] : r.Account.Id)}**\n" +
            $"　`{Sanitize.Code(r.Account.Id)}` — {r.Overlap} shared address(es), last seen {Seen(r.Account)}");

        var embed = Theme.Warning($"{Theme.Warn} {alts.Count} linked account(s)",
                $"{header}\n\n{string.Join("\n", lines)}")
            .AddField("Confirmed addresses", subject.ConfirmedIps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), true)
            .AddField("Known names", Names(subject), true)
            .AddField("Seen", Seen(subject), true);

        if (alts.Count > MaxRows)
            embed.AddField("Not shown", $"{alts.Count - MaxRows} more account(s) share an address.");

        /* Said on every reply that finds something, because this is the field that gets
           screenshotted into a staff channel and argued about. */
        embed.AddField($"{Theme.Info} What this means",
            "Shared addresses are EVIDENCE, not proof. Households, phone tethering and shared " +
            "VPN exits all put unrelated people on one address. Confirmed addresses only - " +
            "guessed ones are never used for this.");

        return embed.Brand("Alt detection");
    }

    private static string Names(AccountRecord account) =>
        account.Names.Count == 0
            ? "none recorded"
            : string.Join(", ", account.Names.Take(3).Select(Sanitize.Code)) +
              (account.Names.Count > 3 ? $" +{account.Names.Count - 3}" : "");

    private static string Seen(AccountRecord account) =>
        account.LastSeen is { } at ? Theme.Relative(at) : "unknown";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m => m.Embed = embed.Brand().Build());
}
