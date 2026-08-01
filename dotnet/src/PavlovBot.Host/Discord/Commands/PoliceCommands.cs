using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Penal;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <param name="Reason">Why the warrant was issued. Shown to whoever runs the check.</param>
public sealed record Warrant(string Player, string Reason, string IssuedBy, DateTimeOffset At);

/// <param name="Codes">The charges booked, so a sentence can be re-derived or appealed.</param>
public sealed record Arrest(
    string Player, IReadOnlyList<string> Codes, int JailMinutes, int Bail,
    string ArrestedBy, DateTimeOffset At);

/// <summary>The bail multiplier, scaled by <c>/bail</c>.</summary>
/// <param name="MaxJailMinutes">
/// The most jail one booking can carry however many charges are stacked. Zero means
/// uncapped, which is the behaviour before the cap existed - a default that changes
/// nobody's sentences until somebody chooses a number.
/// </param>
public sealed record PoliceConfig(double BailRate = 1.0, int MaxJailMinutes = 0);

/// <summary><c>/warrant</c> - issue, clear and check warrants.</summary>
public sealed class WarrantCommand(SerializedStore store, Access access, Paged paged, ILogger<WarrantCommand> logger) : ISlashCommand
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

                if (wanted.Count == 0)
                {
                    await Reply(command, Theme.Success("Nobody is wanted")).ConfigureAwait(false);
                    break;
                }

                // Every page. This used to show only the first and give no sign there were
                // more wanted players.
                await paged.SendAsync(command, $"{wanted.Count} wanted player(s)",
                    Theme.Paginate(wanted), ct).ConfigureAwait(false);
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
public sealed class ArrestCommand : ISlashCommand
{
    private readonly SerializedStore _store;
    private readonly AuditLog _audit;
    private readonly Access _access;
    private readonly ArrestBooking _booking;
    private readonly PavlovBot.Host.Rcon.RconRegistry _rcon;
    private readonly ILogger<ArrestCommand> _logger;

    public ArrestCommand(SerializedStore store, AuditLog audit, Access access,
        ArrestBooking booking, PavlovBot.Host.Rcon.RconRegistry rcon, ILogger<ArrestCommand> logger)
    {
        _store = store;
        _rcon = rcon;
        _audit = audit;
        _access = access;
        _booking = booking;
        _logger = logger;

        /* The picker raises this when an officer confirms. Recording an arrest stays HERE,
           so the component flow owns the UI and this owns what an arrest means - the two
           entry points then cannot drift into recording different things. */
        _booking.Confirmed += OnConfirmedAsync;
    }

    public string Name => "arrest";

    /// <remarks>
    /// NO OPTIONS. Everything - who, what they are charged with, how long - is chosen from
    /// the menu the command opens. Seventy-two codes is more than anybody memorises, and the
    /// typed form meant an officer had to know the code before they could look it up.
    /// </remarks>
    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Police - Book a player on penal-code charges")
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_access.Allows(RequiredAccess.Police, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Police))).ConfigureAwait(false);
            return;
        }

        var config = _store.Read(Datasets.PoliceConfig, new PoliceConfig());

        await _booking.BeginAsync(command, _rcon.AllOnlinePlayers(), config.BailRate, config.MaxJailMinutes)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replace the computed jail time with the officer's, still held to the cap.
    /// </summary>
    /// <remarks>
    /// BAIL IS UNTOUCHED, including whether there is any. An officer choosing a shorter
    /// sentence is not deciding that a non-bailable charge has become bailable.
    /// </remarks>
    internal static Booking Override(Booking booking, int minutes, int capMinutes)
    {
        ArgumentNullException.ThrowIfNull(booking);

        var wanted = Math.Max(0, minutes);
        var capped = capMinutes > 0 && wanted > capMinutes;

        return booking with
        {
            JailMinutes = capped ? capMinutes : wanted,
            Capped = capped,
            CapMinutes = capped ? capMinutes : null,
        };
    }

    /// <summary>One charge's sentence and price, as it reads on the receipt.</summary>
    /// <remarks>
    /// "no bail" is printed rather than left blank. A blank is indistinguishable from a
    /// charge that costs nothing, and the difference is the whole point of the column.
    /// </remarks>
    internal static string ChargeLine(Charge charge, double rate)
    {
        ArgumentNullException.ThrowIfNull(charge);

        var sentence = charge.SentenceRanges
            ? "ranges with the associated charge"
            : charge.JailMinutes > 0 ? $"{charge.JailMinutes} min" : "no jail";

        var price = charge.BailAt(rate) is { } bail
            ? $"${bail.ToString("N0", CultureInfo.GetCultureInfo("en-US"))}"
            : charge.Rule == BailRule.Associated ? "bail from the associated charge" : "no bail";

        return $"{sentence}, {price}";
    }

    /// <summary>Book the arrest and describe it. Shared by the typed and picked paths.</summary>
    private async Task<EmbedBuilder> RecordAsync(
        string player, Booking booking, string officer, double rate, CancellationToken ct)
    {
        /* A non-bailable booking records NO bail figure. The stored record is shared with
           the Node bot and has no field for "cannot be bailed out", so writing the sum of
           the bailable charges would leave a number that reads as a payable price - and the
           charges are stored alongside it, so the real answer is always re-derivable. */
        var arrest = new Arrest(player, booking.Charges.Select(c => c.Code).ToList(),
            booking.JailMinutes, booking.Bailable ? booking.Bail : 0, officer, DateTimeOffset.UtcNow);

        await _store.UpdateAsync(Datasets.Arrests,
            new Dictionary<string, List<Arrest>>(StringComparer.OrdinalIgnoreCase),
            arrests =>
            {
                if (!arrests.TryGetValue(player, out var list)) arrests[player] = list = [];
                list.Add(arrest);
                return arrests;
            }, ct).ConfigureAwait(false);

        // An arrest satisfies the warrant that prompted it.
        await _store.UpdateAsync(Datasets.Warrants,
            new Dictionary<string, List<Warrant>>(StringComparer.OrdinalIgnoreCase),
            warrants => { warrants.Remove(player); return warrants; }, ct).ConfigureAwait(false);

        await _audit.RecordAsync("arrest", officer, player,
            string.Join(",", arrest.Codes), ct).ConfigureAwait(false);

        _logger.LogInformation("arrest | player=\"{Player}\" | codes={Codes} | {Minutes}min ${Bail} | by={By}",
            player, string.Join(",", arrest.Codes), booking.JailMinutes, booking.Bail, officer);

        var lines = booking.Charges.Select(c => $"`{c.Code}` {c.Name} — {ChargeLine(c, rate)}");

        var embed = Theme.Punishment($"{Theme.Deny} Booked — {Sanitize.Code(player)}", string.Join("\n", lines))
            .AddField("Sentence", booking.SentenceLabel(), true)
            .AddField("Bail", booking.BailLabel(), true)
            .AddField("Arresting officer", officer, true);

        // Said out loud, because a receipt whose sentence does not match its charges is
        // otherwise just wrong-looking.
        if (booking.Capped)
            embed.AddField("Capped", $"Charges totalled more than the **{booking.CapMinutes} min** limit. Bail is not capped.");

        return embed;
    }

    /// <summary>An officer confirmed a picked booking.</summary>
    private async Task OnConfirmedAsync(ArrestConfirmed confirmed)
    {
        var rate = _store.Read(Datasets.PoliceConfig, new PoliceConfig()).BailRate;
        var embed = await RecordAsync(confirmed.Player, confirmed.Result, confirmed.Officer, rate,
            CancellationToken.None).ConfigureAwait(false);

        // Replaces the booking message, so the picker does not sit there re-clickable next
        // to the arrest it produced.
        await confirmed.Interaction.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.Components = new ComponentBuilder().Build();
            m.AllowedMentions = AllowedMentions.None;
        }).ConfigureAwait(false);
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
            .WithDescription("Mod - Bail prices and the sentence cap")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action").WithDescription("What to do")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .AddChoice("show", "show").AddChoice("set", "set").AddChoice("reset", "reset")
                .AddChoice("jailcap", "jailcap"))
            .AddOption("percent", ApplicationCommandOptionType.Integer, "Percentage of the base price, e.g. 150", isRequired: false)
            .AddOption("minutes", ApplicationCommandOptionType.Integer,
                "jailcap: the most jail one booking can carry. 0 removes the cap.", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var action = command.Data.Options.First(o => o.Name == "action").Value as string ?? "show";
        var current = store.Read(Datasets.PoliceConfig, new PoliceConfig());

        if (action == "show")
        {
            await Reply(command, Theme.Notice("Police settings",
                $"Bail is **{current.BailRate * 100:0}%** of the base prices.\n" +
                (current.MaxJailMinutes > 0
                    ? $"Jail is capped at **{current.MaxJailMinutes} min** per booking, however many charges stack. Bail is never capped."
                    : "Jail is **uncapped** — stacked charges add up with no limit. Set one with `/bail jailcap minutes:20`."))).ConfigureAwait(false);
            return;
        }

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Mod))).ConfigureAwait(false);
            return;
        }

        if (action == "jailcap")
        {
            var minutes = command.Data.Options.FirstOrDefault(o => o.Name == "minutes")?.Value as long?;
            if (minutes is null or < 0)
            {
                await Reply(command, Theme.Failure("Give a number of minutes",
                    "For example `20`. Use `0` to remove the cap entirely.")).ConfigureAwait(false);
                return;
            }

            // `with`, not a fresh record: constructing one would silently reset the bail rate.
            await store.WriteAsync(Datasets.PoliceConfig, current with { MaxJailMinutes = (int)minutes.Value }, ct).ConfigureAwait(false);

            await Reply(command, Theme.Success("Sentence cap updated", minutes.Value > 0
                ? $"A booking can now carry at most **{minutes.Value} min** of jail, however many charges are stacked.\n\n" +
                  "Bail is **not** capped — stacking still costs the full amount, which is the deterrent."
                : "The cap is off. Stacked charges add up with no limit.")).ConfigureAwait(false);
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

        /* `with`, not a fresh PoliceConfig: constructing one would default MaxJailMinutes
           back to zero, so changing the bail rate would quietly remove the sentence cap. */
        await store.WriteAsync(Datasets.PoliceConfig, current with { BailRate = rate }, ct).ConfigureAwait(false);

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
