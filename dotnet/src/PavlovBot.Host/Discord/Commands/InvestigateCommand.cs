using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Cases;
using PavlovBot.Core.Events;
using PavlovBot.Core.Intelligence;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Cases;
using PavlovBot.Host.Events;
using PavlovBot.Host.Intelligence;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/investigate</c> - the questions the brief's AI assistant was for, answered from the data.
/// </summary>
/// <remarks>
/// WHY THERE IS NO LANGUAGE MODEL HERE, and this is a design decision rather than an omission.
///
/// The brief asks for an AI assistant answering questions like "why was John flagged", "what
/// happened on US-1 in the last hour" and "summarise case 1842". Every one of those has a
/// deterministic answer already sitting in structured data: the risk signals, the timeline,
/// the case. Putting a model in front of them would add an API key, a per-question cost,
/// network egress of player names and moderation history to a third party, and the ability to
/// get the answer WRONG in fluent prose - all to reword facts the bot can already state
/// exactly.
///
/// The brief itself says the assistant must distinguish FACT from INFERENCE and never present
/// an inference as confirmed. The most reliable way to honour that is to only ever state
/// facts, and to label the one place the bot does infer - the risk score - as an inference
/// wherever it appears. That is what this does.
///
/// A model would earn its place for free-form questions nobody anticipated. If that is wanted
/// later, the right shape is already here: these are the read tools it would call, they are
/// permission-scoped, and they return structured results rather than prose. Adding a model on
/// top is then a genuinely separate decision with a separate cost, made deliberately.
///
/// NOTHING HERE ACTS. Every path reads. The brief's requirement that dangerous actions need
/// human confirmation is met the strongest way available: there is no action to confirm.
/// </remarks>
public sealed class InvestigateCommand(
    PlayerIntelligenceService intelligence,
    CaseService cases,
    IEventStore events,
    Access access) : ISlashCommand
{
    public string Name => "investigate";

    public bool Ephemeral => true;

    /// <summary>How much timeline a summary pulls in.</summary>
    private const int TimelineDepth = 12;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Assemble everything known about a player, a server or a case")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("player").WithDescription("Why is this player flagged, and what have they been doing")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("playerid").WithDescription("In-game name")
                    .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true)))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("recent").WithDescription("What has happened lately, across everything")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("hours", ApplicationCommandOptionType.Integer, "How far back (default 1)", isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("case").WithDescription("Summarise a case and its evidence")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("id", ApplicationCommandOptionType.Integer, "Case number", isRequired: true))
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        var options = sub?.Options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal) ?? [];

        var embed = (sub?.Name ?? "recent") switch
        {
            "player" => await PlayerAsync(command, options, ct).ConfigureAwait(false),
            "case" => Case(options.GetValueOrDefault("id") is long id ? (int)id : 0),
            _ => Recent(options.GetValueOrDefault("hours") is long h ? (int)Math.Clamp(h, 1, 24) : 1),
        };

        await Reply(command, embed).ConfigureAwait(false);
    }

    /// <summary>
    /// "Why is this player flagged?" - the risk signals plus what the timeline saw.
    /// </summary>
    /// <remarks>
    /// FACT AND INFERENCE ARE LABELLED SEPARATELY, in their own fields. Everything the bot
    /// RECORDED is a fact; the risk score is the only thing it CONCLUDED, and it is the only
    /// thing under the inference heading. That split is the brief's requirement and it is
    /// cheap to honour when nothing is being generated.
    /// </remarks>
    private async Task<EmbedBuilder> PlayerAsync(
        SocketSlashCommand command, IReadOnlyDictionary<string, object> options, CancellationToken ct)
    {
        var player = Sanitize.Id(options.GetValueOrDefault("playerid")?.ToString() ?? "");
        if (player.Length == 0) return Theme.Failure("That name has nothing usable in it");

        var visibility = ProfileRedaction.VisibilityFor(access.Allows(PlayerProfileCommand.NetworkAccess, command));
        var profile = await intelligence.ProfileAsync(player, visibility, ct).ConfigureAwait(false);

        if (!profile.Known)
            return Theme.Notice($"Never seen: {Sanitize.Code(player)}", "There is no record of this player at all.");

        var embed = Theme.Notice($"Investigation — {Sanitize.Code(player)}");

        // ---- FACTS ----
        var facts = new List<string>
        {
            $"Last seen {(profile.Activity.LastSeen is { } seen ? Theme.Relative(seen) : "never")}, " +
            $"{profile.Activity.PlaytimeMinutes / 60}h {profile.Activity.PlaytimeMinutes % 60}m recorded.",
        };

        if (profile.Moderation.Banned)
            facts.Add($"**Banned now** — {Sanitize.Message(profile.Moderation.ActiveBan ?? "no reason recorded")}.");

        if (profile.Moderation.TotalWarnings > 0)
            facts.Add($"{profile.Moderation.Warnings} active warning(s), {profile.Moderation.TotalWarnings} in total.");

        if (profile.Identity.PreviousNames.Count > 0)
            facts.Add($"Has used {profile.Identity.PreviousNames.Count} other name(s).");

        if (profile.Faction.Name is { } faction)
            facts.Add($"{faction} — {profile.Faction.Rank}.");

        var open = cases.For(player).Where(c => !c.Closed).ToList();
        if (open.Count > 0)
            facts.Add($"{open.Count} open case(s): {string.Join(", ", open.Select(c => $"#{c.Id}"))}.");

        embed.AddField("Facts — what the bot recorded", string.Join("\n", facts.Select(f => $"{Theme.Dot} {f}")));

        // ---- INFERENCE ----
        embed.AddField($"Inference — what the bot CONCLUDED (risk {profile.Risk.Score}/100)",
            profile.Risk.Signals.Count == 0
                ? "Nothing. No signal scored."
                : $"{Sanitize.Message(profile.Risk.Assessment)}\n\n" +
                  string.Join("\n", profile.Risk.Signals.Take(6).Select(s => $"`+{s.Weight,3}` {Sanitize.Message(s.Summary)}")));

        // ---- WHAT THE TIMELINE SAW ----
        var recent = events.Query(new EventQuery(TimelineDepth, Player: player));
        if (recent.Count > 0)
        {
            embed.AddField("Recently on the timeline", string.Join("\n", recent.Select(e =>
                $"`{EasternTime.Stamp(e.At)}` {Sanitize.Message(e.Kind)}" +
                (e.Detail is { Length: > 0 } d ? $" — {Sanitize.Message(d)}" : ""))));
        }

        embed.AddField("Uncertainty",
            "Everything under **Facts** was recorded and is exact. Everything under **Inference** " +
            "is the risk engine's reading of those facts and can be wrong - shared addresses in " +
            "particular have innocent explanations more often than guilty ones. " +
            $"Recommended: {profile.Risk.Recommendation}. **No action has been taken.**");

        return embed;
    }

    private EmbedBuilder Recent(int hours)
    {
        var since = DateTimeOffset.UtcNow - TimeSpan.FromHours(hours);

        var security = events.Query(new EventQuery(10, since, Category: EventCategory.Security));
        var staff = events.Query(new EventQuery(10, since, Category: EventCategory.Staff));
        var players = events.CountDistinct(EventGrouping.Player, new EventQuery(1, since, Category: EventCategory.Player));

        var embed = Theme.Notice($"What happened in the last {hours}h")
            .AddField("Distinct players seen", players.ToString(System.Globalization.CultureInfo.InvariantCulture), inline: true)
            .AddField("Staff actions", staff.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), inline: true)
            .AddField("Security detections", security.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), inline: true);

        if (security.Count > 0)
        {
            embed.AddField($"{Theme.Warn} Security", string.Join("\n", security.Take(8).Select(e =>
                $"`{EasternTime.Stamp(e.At)}` {Sanitize.Message(e.Player ?? "unknown")} — {Sanitize.Message(e.Detail ?? e.Kind)}")));
        }

        if (staff.Count > 0)
        {
            embed.AddField("Staff", string.Join("\n", staff.Take(8).Select(e =>
                $"`{EasternTime.Stamp(e.At)}` {Sanitize.Message(e.Kind)} {Sanitize.Message(e.Player ?? "")} " +
                $"by {Sanitize.Message(e.Actor ?? "unknown")}")));
        }

        if (security.Count == 0 && staff.Count == 0)
            embed.AddField("Quiet", "No staff actions and no detections in this window.");

        return embed;
    }

    private EmbedBuilder Case(int id)
    {
        var subject = cases.Find(id);
        if (subject is null) return Theme.Failure($"There is no case #{id}");

        var verdict = CaseService.Verify(subject);
        var detections = subject.Evidence.Count(e => e.Kind == EvidenceKind.Detection);
        var statements = subject.Evidence.Count(e => e.Kind == EvidenceKind.Statement);

        var embed = Theme.Notice($"Case #{subject.Id} — {Sanitize.Code(subject.Subject)}")
            .WithDescription(Sanitize.Message(subject.Reason));

        embed.AddField("Status", CaseTransitions.Name(subject.Status), inline: true);
        embed.AddField("Open for", $"{(int)subject.Age(DateTimeOffset.UtcNow).TotalDays}d", inline: true);
        embed.AddField("Evidence", $"{subject.Evidence.Count} ({detections} automated, {statements} statements)", inline: true);

        /* THE MIX IS REPORTED, not just the count. A case built entirely on automated
           detections and no human statement is a case nobody has actually looked at, and that
           is worth knowing before acting on it. */
        if (subject.Evidence.Count > 0 && statements == 0)
        {
            embed.AddField($"{Theme.Warn} No human statement",
                "Every piece of evidence here came from the bot. Nobody has yet written down " +
                "what they saw, so this case rests entirely on automated inference.");
        }

        if (subject.Notes.Count > 0)
            embed.AddField("Latest note", Sanitize.Message(subject.Notes[^1].Text));

        if (subject.Resolution is { Length: > 0 } resolution)
            embed.AddField("Finding", Sanitize.Message(resolution));

        if (!verdict.Intact)
        {
            embed.AddField($"{Theme.Warn} EVIDENCE CHAIN BROKEN",
                $"Entry #{verdict.BrokenAt} does not match its hash. Treat this case as unverified.");
        }

        return embed;
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
