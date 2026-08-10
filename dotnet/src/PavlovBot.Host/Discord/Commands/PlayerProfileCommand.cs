using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Intelligence;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Intelligence;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/player</c> - everything the bot knows about one player, in one place.
/// </summary>
/// <remarks>
/// THE PROBLEM IT SOLVES. Answering "who is this" meant running /checkban, /warnings, /alts,
/// /playtime, /whitelist list and reading the mod log, then joining the results by hand. Six
/// commands, six replies, and whatever the moderator forgot to check.
///
/// SUBCOMMANDS RATHER THAN ONE WALL OF TEXT. The overview is what somebody wants nine times
/// out of ten; the sections exist for the tenth. A single reply carrying every address, every
/// past name and every associate would be unreadable and would also print network detail to
/// people who should not have it.
///
/// NETWORK DETAIL IS ADMIN-ONLY, and the gate is not here. The service returns a profile
/// already narrowed to what the caller may see, so this class never holds an address it must
/// not print. See <see cref="ProfileRedaction"/> for why redaction lives there rather than at
/// each field.
///
/// EPHEMERAL, ALWAYS. Even the moderation view names a player, their bans and their warnings,
/// and this gets run in whatever channel a moderator happens to be in.
/// </remarks>
public sealed class PlayerProfileCommand(PlayerIntelligenceService intelligence, Access access) : ISlashCommand
{
    public string Name => "player";

    public bool Ephemeral => true;

    /// <summary>Access needed to see addresses and network intelligence.</summary>
    /// <remarks>
    /// ADMIN, not Mod. An address is the one piece of this that identifies somebody outside
    /// the game, and the brief is explicit that network intelligence is separate from
    /// moderation history. Moderators keep everything they need to moderate.
    /// </remarks>
    public const RequiredAccess NetworkAccess = RequiredAccess.Admin;

    public ApplicationCommandProperties Build()
    {
        static SlashCommandOptionBuilder Player() =>
            new SlashCommandOptionBuilder()
                .WithName("playerid").WithDescription("In-game name")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true);

        static SlashCommandOptionBuilder Section(string name, string description) =>
            new SlashCommandOptionBuilder()
                .WithName(name).WithDescription(description)
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player());

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Everything known about a player, in one place")
            .AddOption(Section("overview", "Identity, activity, moderation, faction and risk"))
            .AddOption(Section("risk", "Why their risk score is what it is"))
            .AddOption(Section("history", "Every name and staff action on record"))
            .AddOption(Section("security", "Admin - addresses, VPN and network intelligence"))
            .AddOption(Section("associations", "Admin - accounts sharing a confirmed address"))
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        var section = sub?.Name ?? "overview";

        var player = Sanitize.Id(sub?.Options.FirstOrDefault(o => o.Name == "playerid")?.Value as string ?? "");
        if (player.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        /* THE SECTIONS THAT ARE ENTIRELY NETWORK DATA ARE REFUSED OUTRIGHT, rather than
           served empty. A moderator running /player security and getting a blank panel would
           read it as "nothing to see", which is the opposite of true. */
        var maySeeNetwork = access.Allows(NetworkAccess, command);
        if (section is "security" or "associations" && !maySeeNetwork)
        {
            await Reply(command, Theme.Denied("Not allowed",
                "Network intelligence is Admin-only. The rest of `/player` is available to you.")).ConfigureAwait(false);
            return;
        }

        var profile = await intelligence
            .ProfileAsync(player, ProfileRedaction.VisibilityFor(maySeeNetwork), ct)
            .ConfigureAwait(false);

        if (!profile.Known)
        {
            /* SAID PLAINLY. An empty profile and a clean one look identical, and the
               difference matters: "never seen" is not "no record of wrongdoing". */
            await Reply(command, Theme.Notice($"Never seen: {Sanitize.Code(player)}",
                "The bot has no record of this player at all - no sessions, no bans, no warnings. " +
                "Check the spelling; in-game names are case-sensitive and easy to mistype.")).ConfigureAwait(false);
            return;
        }

        await Reply(command, section switch
        {
            "risk" => Risk(profile),
            "history" => History(profile),
            "security" => Security(profile),
            "associations" => Associations(profile),
            _ => Overview(profile),
        }).ConfigureAwait(false);
    }

    // ---- sections ----

    private static EmbedBuilder Overview(PlayerProfile p)
    {
        var embed = Themed(p, $"{Sanitize.Code(p.Identity.Current)}");

        embed.AddField("Identity",
            $"Account: `{Sanitize.Code(p.Identity.AccountId ?? "not recorded")}`\n" +
            $"Previous names: {p.Identity.PreviousNames.Count}", inline: true);

        embed.AddField("Activity",
            $"{(p.Activity.Online ? $"{Theme.Up} online on {string.Join(", ", p.Activity.Servers)}" : $"{Theme.Down} offline")}\n" +
            $"Playtime: {Duration(p.Activity.PlaytimeMinutes)}\n" +
            $"Last seen: {Seen(p.Activity.LastSeen)}", inline: true);

        embed.AddField("Moderation",
            p.Moderation.Banned
                ? $"{Theme.Deny} **BANNED** — {Sanitize.Message(p.Moderation.ActiveBan ?? "no reason")}\n" +
                  (p.Moderation.BanIsPermanent ? "Permanent" : $"Until {Theme.Relative(p.Moderation.BanExpires!.Value)}")
                : $"Warnings: {p.Moderation.Warnings} active, {p.Moderation.TotalWarnings} total\n" +
                  $"Kicks: {p.Moderation.Kicks}");

        if (p.Faction.Name is { } faction)
            embed.AddField("Faction", $"{faction} — **{p.Faction.Rank}**", inline: true);

        if (p.Economy.Balance is { } balance)
            embed.AddField("Balance", $"${balance:N0}" + (p.Economy.OwedWages > 0 ? $" (+${p.Economy.OwedWages:N0} owed)" : ""), inline: true);

        embed.AddField("Risk", $"**{p.Risk.Score}/100** — {p.Risk.Band.ToString().ToUpperInvariant()} " +
            $"({p.Risk.Confidence.ToString().ToLowerInvariant()} confidence)\n" +
            $"{p.Risk.Assessment}");

        if (p.Risk.Signals.Count > 0)
            embed.AddField("Why", "Run `/player risk` for the individual signals.");

        return Footnote(embed, p);
    }

    /// <summary>
    /// The score broken into the reasons behind it.
    /// </summary>
    /// <remarks>
    /// EVERY SIGNAL, WITH ITS WEIGHT, and an explicit statement that nothing was done about
    /// it. A moderator has to be able to disagree with this screen, which means seeing what
    /// it was built from rather than a number and a verdict.
    /// </remarks>
    private static EmbedBuilder Risk(PlayerProfile p)
    {
        var embed = Themed(p, $"Risk — {Sanitize.Code(p.Identity.Current)}")
            .AddField("Score", $"**{p.Risk.Score}/100** ({p.Risk.Band.ToString().ToUpperInvariant()})", inline: true)
            .AddField("Confidence", p.Risk.Confidence.ToString(), inline: true);

        if (p.Risk.Signals.Count == 0)
        {
            embed.AddField("Signals", "None. Nothing about this player scored at all.");
            return Footnote(embed, p);
        }

        var lines = p.Risk.Signals.Select(s =>
            $"`+{s.Weight,3}` **{Spaced(s.Kind.ToString())}**\n{Sanitize.Message(s.Summary)}" +
            (s.Evidence is { Length: > 0 } e ? $"\n↳ `{Sanitize.Code(e)}`" : ""));

        embed.AddField("Signals", Theme.Paginate(lines)[0]);

        embed.AddField("Assessment", p.Risk.Assessment);
        embed.AddField("Recommended", Recommend(p.Risk.Recommendation));

        /* SAID EVERY TIME, not just when the score is high. The one thing a moderator must
           never assume is that the bot has already acted. */
        embed.AddField($"{Theme.Warn} No action was taken",
            "This is an assessment, not a punishment. Signals combine so that weak ones cannot " +
            "add up to a strong verdict, but a high score is still a reason to look rather than " +
            "a reason to ban.");

        return Footnote(embed, p);
    }

    private static EmbedBuilder History(PlayerProfile p)
    {
        var embed = Themed(p, $"History — {Sanitize.Code(p.Identity.Current)}");

        embed.AddField("Names",
            p.Identity.PreviousNames.Count == 0
                ? "No other name recorded."
                : Theme.Paginate(p.Identity.PreviousNames.Select(n => $"`{Sanitize.Code(n)}`"))[0]);

        embed.AddField("First seen", Seen(p.Activity.FirstSeen), inline: true);
        embed.AddField("Last seen", Seen(p.Activity.LastSeen), inline: true);

        embed.AddField("Staff actions",
            p.Moderation.Actions.Count == 0
                ? "Nothing on record."
                : string.Join("\n", p.Moderation.Actions.Select(a => $"{Theme.Dot} {Sanitize.Message(a)}")));

        return Footnote(embed, p);
    }

    private static EmbedBuilder Security(PlayerProfile p)
    {
        var embed = Themed(p, $"Network — {Sanitize.Code(p.Identity.Current)}");

        embed.AddField("Confirmed addresses",
            p.Network.ConfirmedIps.Count == 0
                ? "None recorded."
                : Theme.Paginate(p.Network.ConfirmedIps.Select(i => $"`{Sanitize.Code(i)}`"))[0]);

        /* LABELLED AS UNRELIABLE, in the field name. A guessed address is a correlation
           between a join line and a nearby address line, and on a busy server two players
           connecting a second apart can be attributed to each other. Presenting it beside a
           confirmed one without saying so is how a coincidence becomes evidence. */
        if (p.Network.GuessedIps.Count > 0)
        {
            embed.AddField($"{Theme.Warn} Guessed addresses — not reliable",
                Theme.Paginate(p.Network.GuessedIps.Select(i => $"`{Sanitize.Code(i)}`"))[0]);
        }

        embed.AddField("VPN", Tri(p.Network.Vpn), inline: true);
        embed.AddField("Proxy", Tri(p.Network.Proxy), inline: true);
        embed.AddField("ASN", p.Network.Asn ?? "unknown", inline: true);

        if (p.Network.Country is { } country) embed.AddField("Country", country, inline: true);

        return Footnote(embed, p);
    }

    private static EmbedBuilder Associations(PlayerProfile p)
    {
        var embed = Themed(p, $"Associations — {Sanitize.Code(p.Identity.Current)}");

        if (p.Associations.Count == 0)
        {
            embed.AddField("Linked accounts", "None. No other account shares a confirmed address.");
            return Footnote(embed, p);
        }

        embed.AddField("Linked accounts", Theme.Paginate(p.Associations.Select(a =>
            $"{(a.Banned ? Theme.Deny : Theme.Dot)} `{Sanitize.Code(a.Name)}` — {a.Reason}" +
            (a.Banned ? " **(banned)**" : "")))[0]);

        /* THE CAVEAT IS NOT OPTIONAL. This panel is the one most likely to be screenshotted
           as proof of alt accounts, and every link on it has an innocent explanation that is
           more common than the guilty one. */
        embed.AddField("What this means",
            "A link is a CONFIRMED SHARED ADDRESS and nothing more. Households, phone " +
            "tethering, student halls and recycled ISP leases all put unrelated people on one " +
            "address. Treat it as a question, not an answer.");

        return Footnote(embed, p);
    }

    // ---- presentation ----

    private static EmbedBuilder Themed(PlayerProfile p, string title) => p.Risk.Band switch
    {
        RiskBand.Critical => Theme.Punishment($"{Theme.Deny} {title}"),
        RiskBand.High => Theme.Warning($"{title}"),
        _ => Theme.Notice($"{title}"),
    };

    /// <summary>Says when something was withheld, rather than leaving a silent gap.</summary>
    private static EmbedBuilder Footnote(EmbedBuilder embed, PlayerProfile p) =>
        p.Redacted
            ? embed.AddField("Withheld",
                "Network detail exists for this player and is not shown at your access level. " +
                "The risk score still counts it.")
            : embed;

    private static string Recommend(RiskRecommendation recommendation) => recommendation switch
    {
        RiskRecommendation.Investigate => "Open a case and investigate. Do not ban on this alone.",
        RiskRecommendation.ReviewManually => "A person should review this before they play much longer.",
        RiskRecommendation.Monitor => "Worth an eye. Nothing here needs acting on now.",
        _ => "Nothing.",
    };

    private static string Tri(bool? value) => value switch
    {
        true => $"{Theme.Bad} yes",
        false => $"{Theme.Ok} no",
        // Never screened is not the same as screened and clean, and reporting it as "no"
        // would turn a gap in the data into a clean bill of health.
        null => "never screened",
    };

    private static string Seen(DateTimeOffset? at) =>
        at is { } value ? $"{EasternTime.Stamp(value)} ({Theme.Relative(value)})" : "never";

    private static string Duration(long minutes) =>
        minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";

    /// <summary>"SharedAddressWithBanned" -> "Shared address with banned".</summary>
    private static string Spaced(string pascal)
    {
        var spaced = string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : $"{c}"));
        return spaced;
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
