using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Penal;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <param name="Reason">Why the warrant was issued. Shown to whoever runs the check.</param>
public sealed record Warrant(string Player, string Reason, string IssuedBy, DateTimeOffset At);

/// <param name="Codes">The charges booked, so a sentence can be re-derived or appealed.</param>
public sealed record Arrest(
    string Player, IReadOnlyList<string> Codes, int JailMinutes, int Bail,
    string ArrestedBy, DateTimeOffset At);

/// <summary>The bail multiplier, scaled by <c>/bail</c>.</summary>
public sealed record PoliceConfig(double BailRate = 1.0);

/// <summary><c>/warrant</c> - issue, clear and check warrants.</summary>
public sealed class WarrantCommand(SerializedStore store, Access access, ILogger<WarrantCommand> logger) : ISlashCommand
{
    public string Name => "warrant";

    public ApplicationCommandProperties Build()
    {
        static SlashCommandOptionBuilder Player(bool required) =>
            new SlashCommandOptionBuilder()
                .WithName("playerid").WithDescription("Player ID or username")
                .WithType(ApplicationCommandOptionType.String).WithRequired(required).WithAutocomplete(true);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Police - Manage warrants")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("give").WithDescription("Issue a warrant")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player(true))
                .AddOption("reason", ApplicationCommandOptionType.String, "Why", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove").WithDescription("Clear a player's warrants")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player(true)))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("check").WithDescription("Look up warrants")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player(false)))
            .Build();
    }

    private Dictionary<string, List<Warrant>> Load() =>
        store.Read(Datasets.Warrants, new Dictionary<string, List<Warrant>>(StringComparer.OrdinalIgnoreCase));

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sub = command.Data.Options.First();
        var player = Sanitize.Id(sub.Options.FirstOrDefault(o => o.Name == "playerid")?.Value as string ?? "");

        // Checking is public; issuing and clearing are not. A player being able to see
        // whether they are wanted is the point of a warrant.
        if (sub.Name != "check" && !access.Allows(RequiredAccess.Police, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Police))).ConfigureAwait(false);
            return;
        }

        switch (sub.Name)
        {
            case "give":
            {
                var reason = Sanitize.Message(sub.Options.First(o => o.Name == "reason").Value as string ?? "");
                var warrant = new Warrant(player, reason, command.User.Username, DateTimeOffset.UtcNow);

                await store.UpdateAsync(Datasets.Warrants,
                    new Dictionary<string, List<Warrant>>(StringComparer.OrdinalIgnoreCase),
                    warrants =>
                    {
                        if (!warrants.TryGetValue(player, out var list)) warrants[player] = list = [];
                        list.Add(warrant);
                        return warrants;
                    }, ct).ConfigureAwait(false);

                logger.LogInformation("warrant issued | player=\"{Player}\" | by={By} | {Reason}", player, command.User.Username, reason);

                await Reply(command, Theme.Warning("Warrant issued",
                    $"**{Sanitize.Code(player)}** — {Sanitize.Code(reason)}")
                    .AddField("Issued by", command.User.Username, true)).ConfigureAwait(false);
                break;
            }

            case "remove":
            {
                var had = Load().GetValueOrDefault(player)?.Count ?? 0;
                await store.UpdateAsync(Datasets.Warrants,
                    new Dictionary<string, List<Warrant>>(StringComparer.OrdinalIgnoreCase),
                    warrants => { warrants.Remove(player); return warrants; }, ct).ConfigureAwait(false);

                await Reply(command, had > 0
                    ? Theme.Success("Warrants cleared", $"Cleared **{had}** warrant(s) on **{Sanitize.Code(player)}**.")
                    : Theme.Notice("Nothing to clear", $"**{Sanitize.Code(player)}** has no warrants.")).ConfigureAwait(false);
                break;
            }

            default:
            {
                var all = Load();
                if (player.Length > 0)
                {
                    var list = all.GetValueOrDefault(player) ?? [];
                    await Reply(command, list.Count == 0
                        ? Theme.Success("No warrants", $"**{Sanitize.Code(player)}** is clean.")
                        : Theme.Warning($"{list.Count} warrant(s) on {Sanitize.Code(player)}",
                            string.Join("\n", list.Select((w, i) =>
                                $"**{i + 1}.** {Sanitize.Code(w.Reason)} — {w.IssuedBy}, {Theme.Relative(w.At)}")))).ConfigureAwait(false);
                    return;
                }

                var wanted = all.Where(kv => kv.Value.Count > 0)
                    .OrderByDescending(kv => kv.Value.Count)
                    .Select(kv => $"`{Sanitize.Code(kv.Key)}` — {kv.Value.Count}")
                    .ToList();

                await Reply(command, wanted.Count == 0
                    ? Theme.Success("Nobody is wanted")
                    : Theme.Warning($"{wanted.Count} wanted player(s)", Theme.Paginate(wanted)[0])).ConfigureAwait(false);
                break;
            }
        }
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary>
/// <c>/arrest</c> - book a player on penal-code charges.
/// </summary>
/// <remarks>
/// The arithmetic lives in <see cref="PenalCode"/>: bail is scaled and rounded PER CHARGE,
/// never on the total, so the figures on the receipt add up to the number at the bottom.
/// </remarks>
public sealed class ArrestCommand(SerializedStore store, Access access, ILogger<ArrestCommand> logger) : ISlashCommand
{
    public string Name => "arrest";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Police - Book a player on penal-code charges")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player to arrest", isRequired: true, isAutocomplete: true)
            .AddOption("codes", ApplicationCommandOptionType.String, "Charge codes, comma separated (e.g. PC 100, PC 210)", isRequired: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Police, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Police))).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(command.Data.Options.First(o => o.Name == "playerid").Value as string ?? "");
        var raw = command.Data.Options.First(o => o.Name == "codes").Value as string ?? "";
        var codes = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var rate = store.Read(Datasets.PoliceConfig, new PoliceConfig()).BailRate;
        var booking = PenalCode.Book(codes, rate);

        if (booking.Charges.Count == 0)
        {
            /* Unknown codes are SKIPPED, never guessed at - so an arrest naming only
               unknown codes books nothing, and says so rather than silently jailing
               somebody for zero minutes. */
            await Reply(command, Theme.Failure("No recognised charges",
                $"None of `{Sanitize.Code(raw)}` matched the penal code. Nothing was recorded.")).ConfigureAwait(false);
            return;
        }

        var arrest = new Arrest(player, booking.Charges.Select(c => c.Code).ToList(),
            booking.JailMinutes, booking.Bail, command.User.Username, DateTimeOffset.UtcNow);

        await store.UpdateAsync(Datasets.Arrests,
            new Dictionary<string, List<Arrest>>(StringComparer.OrdinalIgnoreCase),
            arrests =>
            {
                if (!arrests.TryGetValue(player, out var list)) arrests[player] = list = [];
                list.Add(arrest);
                return arrests;
            }, ct).ConfigureAwait(false);

        // An arrest satisfies the warrant that prompted it.
        await store.UpdateAsync(Datasets.Warrants,
            new Dictionary<string, List<Warrant>>(StringComparer.OrdinalIgnoreCase),
            warrants => { warrants.Remove(player); return warrants; }, ct).ConfigureAwait(false);

        logger.LogInformation("arrest | player=\"{Player}\" | codes={Codes} | {Minutes}min ${Bail} | by={By}",
            player, string.Join(",", arrest.Codes), booking.JailMinutes, booking.Bail, command.User.Username);

        var lines = booking.Charges.Select(c =>
            $"`{c.Code}` {c.Name} — {(c.Special is not null ? c.Special.ToString() : $"{c.JailMinutes} min")}" +
            (c.BailAt(rate) is { } bail ? $", ${bail.ToString("N0", CultureInfo.GetCultureInfo("en-US"))}" : ""));

        var embed = Theme.Punishment($"{Theme.Deny} Booked — {Sanitize.Code(player)}", string.Join("\n", lines))
            .AddField("Sentence", booking.SentenceLabel(), true)
            .AddField("Bail", booking.BailLabel(), true)
            .AddField("Arresting officer", command.User.Username, true);

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/backgroundcheck</c> - a player's record in one place.</summary>
public sealed class BackgroundCheckCommand(SerializedStore store) : ISlashCommand
{
    public string Name => "backgroundcheck";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Police - Pull a player's record")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player ID or username", isRequired: true, isAutocomplete: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var player = Sanitize.Id(command.Data.Options.First().Value as string ?? "");

        var arrests = store.Read(Datasets.Arrests, new Dictionary<string, List<Arrest>>(StringComparer.OrdinalIgnoreCase))
            .GetValueOrDefault(player) ?? [];
        var warrants = store.Read(Datasets.Warrants, new Dictionary<string, List<Warrant>>(StringComparer.OrdinalIgnoreCase))
            .GetValueOrDefault(player) ?? [];

        if (arrests.Count == 0 && warrants.Count == 0)
        {
            await Reply(command, Theme.Success("Clean record", $"**{Sanitize.Code(player)}** has no arrests or warrants.")).ConfigureAwait(false);
            return;
        }

        var embed = Theme.Notice($"Record — {Sanitize.Code(player)}")
            .AddField("Arrests", arrests.Count.ToString(CultureInfo.InvariantCulture), true)
            .AddField("Total jail time", $"{arrests.Sum(a => a.JailMinutes)} min", true)
            .AddField("Outstanding warrants", warrants.Count.ToString(CultureInfo.InvariantCulture), true);

        if (arrests.Count > 0)
        {
            // Most recent first: the last five arrests are what an officer at the scene
            // needs, and a full history is a scroll nobody reads.
            var recent = arrests.OrderByDescending(a => a.At).Take(5)
                .Select(a => $"{Theme.Relative(a.At)} — `{string.Join(", ", a.Codes)}` ({a.JailMinutes} min) by {a.ArrestedBy}");
            embed.AddField("Recent arrests", string.Join("\n", recent));
        }

        if (warrants.Count > 0)
            embed.AddField($"{Theme.Warn} Wanted for", string.Join("\n", warrants.Select(w => Sanitize.Code(w.Reason))));

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/bail</c> - scale every charge's bail figure.</summary>
public sealed class BailCommand(SerializedStore store, Access access) : ISlashCommand
{
    public string Name => "bail";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Scale every charge's bail price")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action").WithDescription("What to do")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .AddChoice("show", "show").AddChoice("set", "set").AddChoice("reset", "reset"))
            .AddOption("percent", ApplicationCommandOptionType.Integer, "Percentage of the base price, e.g. 150", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var action = command.Data.Options.First(o => o.Name == "action").Value as string ?? "show";
        var current = store.Read(Datasets.PoliceConfig, new PoliceConfig());

        if (action == "show")
        {
            await Reply(command, Theme.Notice("Bail rate",
                $"Currently **{current.BailRate * 100:0}%** of the base prices.")).ConfigureAwait(false);
            return;
        }

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Mod))).ConfigureAwait(false);
            return;
        }

        double rate;
        if (action == "reset") rate = 1.0;
        else
        {
            var percent = command.Data.Options.FirstOrDefault(o => o.Name == "percent")?.Value as long?;
            if (percent is null or < 0)
            {
                await Reply(command, Theme.Failure("Give a percentage", "For example `150` for one and a half times the base price.")).ConfigureAwait(false);
                return;
            }
            rate = percent.Value / 100.0;
        }

        await store.WriteAsync(Datasets.PoliceConfig, new PoliceConfig(rate), ct).ConfigureAwait(false);

        // Show the effect on a real charge, because "1.5" means nothing until you see it
        // land on a number somebody is going to pay.
        var example = PenalCode.Get("PC 100");
        var suffix = example is not null ? $" — `PC 100` is now ${example.BailAt(rate)}." : "";

        await Reply(command, Theme.Success("Bail rate updated", $"Now **{rate * 100:0}%** of the base prices.{suffix}")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
