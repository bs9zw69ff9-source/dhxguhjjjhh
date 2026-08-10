using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Cases;
using PavlovBot.Core.Intelligence;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Cases;
using PavlovBot.Host.Intelligence;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/case</c> - moderation cases: the record of an investigation and its reasoning.
/// </summary>
/// <remarks>
/// WHAT THIS RECORDS THAT NOTHING ELSE DOES. Every other system here records a FACT - a ban, a
/// warning, a flagged join. None of them records the REASONING, so "why was this player
/// permanently banned in March" is currently answerable only from whatever a moderator
/// happened to type in the reason field.
///
/// OPENING A CASE PUNISHES NOBODY, and that is worth being loud about in the UI. If a case
/// carried consequences, staff would stop opening speculative ones, and the speculative ones
/// are exactly the ones worth having written down.
///
/// EPHEMERAL. A case names a player, an accusation and the evidence for it, and a case that
/// turns out to be unfounded should not have been broadcast to a channel on the way.
/// </remarks>
public sealed class CaseCommand(CaseService cases, PlayerIntelligenceService intelligence, Access access) : ISlashCommand
{
    public string Name => "case";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build()
    {
        static SlashCommandOptionBuilder Id() =>
            new SlashCommandOptionBuilder()
                .WithName("id").WithDescription("Case number")
                .WithType(ApplicationCommandOptionType.Integer).WithRequired(true);

        static SlashCommandOptionBuilder Text(string name, string description, bool required = true) =>
            new SlashCommandOptionBuilder()
                .WithName(name).WithDescription(description)
                .WithType(ApplicationCommandOptionType.String).WithRequired(required);

        var kind = new SlashCommandOptionBuilder()
            .WithName("kind").WithDescription("What sort of evidence this is")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);
        foreach (var value in Enum.GetValues<EvidenceKind>())
            kind.AddChoice(value.ToString(), value.ToString());

        var status = new SlashCommandOptionBuilder()
            .WithName("status").WithDescription("The new status")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);
        foreach (var value in Enum.GetValues<CaseStatus>())
            status.AddChoice(CaseTransitions.Name(value), value.ToString());

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Moderation cases: investigations, evidence and findings")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("open").WithDescription("Open a case on a player")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("playerid").WithDescription("In-game name")
                    .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true))
                .AddOption(Text("reason", "Why you are opening it"))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("attach_risk")
                    .WithDescription("Attach their current risk signals as evidence (default true)")
                    .WithType(ApplicationCommandOptionType.Boolean).WithRequired(false)))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("view").WithDescription("Read a case")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Id()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list").WithDescription("Cases that are still open")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("note").WithDescription("Add a note")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Id()).AddOption(Text("text", "The note")))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("evidence").WithDescription("Add evidence. It can never be edited afterwards")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Id()).AddOption(kind).AddOption(Text("content", "The evidence")))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("assign").WithDescription("Hand the case to somebody")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Id())
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("member").WithDescription("Who owns it now")
                    .WithType(ApplicationCommandOptionType.User).WithRequired(true)))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("status").WithDescription("Change the status")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Id()).AddOption(status)
                .AddOption(Text("finding", "What was concluded (required when closing)", required: false)))
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
        var options = sub?.Options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal) ?? [];
        var by = command.User.Username;

        var embed = (sub?.Name ?? "list") switch
        {
            "open" => await OpenAsync(options, by, ct).ConfigureAwait(false),
            "view" => View(Id(options)),
            "note" => Describe(await cases.AddNoteAsync(Id(options), Str(options, "text"), by, ct).ConfigureAwait(false), "Note added"),
            "evidence" => await EvidenceAsync(options, by, ct).ConfigureAwait(false),
            "assign" => await AssignAsync(options, by, ct).ConfigureAwait(false),
            "status" => await StatusAsync(options, by, ct).ConfigureAwait(false),
            _ => List(),
        };

        await Reply(command, embed).ConfigureAwait(false);
    }

    /// <summary>
    /// Open a case, seeded with the risk signals that prompted it.
    /// </summary>
    /// <remarks>
    /// THE SEEDING IS THE POINT. A case whose evidence a moderator has to retype from a screen
    /// the bot just showed them is a case that gets opened with no evidence on it. The signals
    /// are copied as they stood AT OPENING TIME, which is what makes them evidence: the live
    /// score changes as the player's record does, so a case referring to "their risk score"
    /// would silently rewrite its own reasoning.
    /// </remarks>
    private async Task<EmbedBuilder> OpenAsync(IReadOnlyDictionary<string, object> options, string by, CancellationToken ct)
    {
        var player = Sanitize.Id(Str(options, "playerid"));
        if (player.Length == 0) return Theme.Failure("That name has nothing usable in it");

        var seed = new List<(EvidenceKind, string, string)>();

        if (options.GetValueOrDefault("attach_risk") is not false)
        {
            /* THE MODERATION VIEW, always, whoever is opening the case. A case is read later
               by whoever picks it up, and an address baked into evidence would leak past the
               access check every time it is read afterwards. */
            var profile = await intelligence.ProfileAsync(player, ProfileVisibility.Moderation, ct).ConfigureAwait(false);

            seed.Add((EvidenceKind.Detection,
                $"Risk at opening: {profile.Risk.Score}/100 ({profile.Risk.Confidence} confidence). {profile.Risk.Assessment}",
                "risk-engine"));

            foreach (var signal in profile.Risk.Signals)
                seed.Add((EvidenceKind.Detection, $"[+{signal.Weight}] {signal.Summary}", "risk-engine"));
        }

        var result = await cases.OpenAsync(player, Str(options, "reason"), by, seed, ct).ConfigureAwait(false);
        if (!result.Ok) return Theme.Failure("Could not open the case", result.Error);

        return Theme.Success($"Case #{result.Case!.Id} opened", $"**{Sanitize.Code(player)}** — {Sanitize.Message(result.Case.Reason)}")
            .AddField("Evidence attached", $"{result.Case.Evidence.Count} entr{(result.Case.Evidence.Count == 1 ? "y" : "ies")}", inline: true)
            .AddField("Opened by", by, inline: true)
            .AddField("Nothing was done to them",
                "Opening a case restricts nobody. Use the ban and warning commands for that; " +
                "this is the record of the investigation, not the investigation's teeth.");
    }

    private async Task<EmbedBuilder> EvidenceAsync(IReadOnlyDictionary<string, object> options, string by, CancellationToken ct)
    {
        var kind = Enum.TryParse<EvidenceKind>(Str(options, "kind"), ignoreCase: true, out var parsed)
            ? parsed
            : EvidenceKind.Statement;

        var result = await cases.AddEvidenceAsync(Id(options), kind, Str(options, "content"), by, by, ct).ConfigureAwait(false);

        return Describe(result, "Evidence added",
            "It is appended to the chain and cannot be edited or removed. Anything that changes " +
            "it later will show as a broken chain when the case is read.");
    }

    private async Task<EmbedBuilder> AssignAsync(IReadOnlyDictionary<string, object> options, string by, CancellationToken ct)
    {
        if (options.GetValueOrDefault("member") is not IUser member)
            return Theme.Failure("No member given");

        return Describe(await cases.AssignAsync(Id(options), member.Username, by, ct).ConfigureAwait(false),
            $"Assigned to {member.Username}");
    }

    private async Task<EmbedBuilder> StatusAsync(IReadOnlyDictionary<string, object> options, string by, CancellationToken ct)
    {
        if (!Enum.TryParse<CaseStatus>(Str(options, "status"), ignoreCase: true, out var status))
            return Theme.Failure("That is not a status");

        var finding = Str(options, "finding");

        /* A CLOSING WITHOUT A FINDING IS REFUSED. "Resolved" with no reason recorded is the
           single most useless thing a case can end as - it says an investigation happened and
           nothing about what it concluded, which is the one fact anybody comes back for. */
        if (status is CaseStatus.Resolved or CaseStatus.Dismissed && finding.Trim().Length == 0)
        {
            return Theme.Failure("Say what was concluded",
                $"Closing a case as **{CaseTransitions.Name(status)}** needs a finding. A closed " +
                "case with no reason recorded answers none of the questions somebody will come " +
                "back to it with.");
        }

        return Describe(await cases.SetStatusAsync(Id(options), status, finding, by, ct).ConfigureAwait(false),
            $"Case is now {CaseTransitions.Name(status)}");
    }

    // ---- reading ----

    private EmbedBuilder View(int id)
    {
        var subject = cases.Find(id);
        if (subject is null) return Theme.Failure($"There is no case #{id}");

        var verdict = CaseService.Verify(subject);

        var embed = (subject.Status switch
        {
            CaseStatus.Resolved => Theme.Success($"Case #{subject.Id} — Resolved"),
            CaseStatus.Dismissed => Theme.Notice($"Case #{subject.Id} — Dismissed"),
            CaseStatus.Escalated => Theme.Warning($"Case #{subject.Id} — Escalated"),
            _ => Theme.Notice($"Case #{subject.Id} — {CaseTransitions.Name(subject.Status)}"),
        }).WithDescription($"**{Sanitize.Code(subject.Subject)}** — {Sanitize.Message(subject.Reason)}");

        embed.AddField("Opened", $"{EasternTime.Stamp(subject.OpenedAt)} by {Sanitize.Code(subject.OpenedBy)}", inline: true);
        embed.AddField("Assigned", subject.AssignedTo is { Length: > 0 } a ? Sanitize.Code(a) : "nobody", inline: true);
        embed.AddField("Age", Duration(subject.Age(DateTimeOffset.UtcNow)), inline: true);

        if (subject.Evidence.Count > 0)
        {
            embed.AddField($"Evidence ({subject.Evidence.Count})", Theme.Paginate(subject.Evidence.Select(e =>
                $"`#{e.Sequence}` **{e.Kind}** — {Sanitize.Message(e.Content)}\n" +
                $"↳ {Sanitize.Code(e.Source)}, added by {Sanitize.Code(e.AddedBy)} {Theme.Relative(e.At)}"))[0]);
        }

        if (subject.Notes.Count > 0)
        {
            embed.AddField($"Notes ({subject.Notes.Count})", Theme.Paginate(subject.Notes.Select(n =>
                $"{Theme.Dot} **{Sanitize.Code(n.Author)}** {Theme.Relative(n.At)}\n{Sanitize.Message(n.Text)}"))[0]);
        }

        if (subject.Resolution is { Length: > 0 } resolution)
            embed.AddField("Finding", Sanitize.Message(resolution));

        /* THE CHAIN VERDICT, ON EVERY READ. A tamper check that runs on a timer and logs
           somewhere is not a control: the moment somebody is reading a case to decide
           something is exactly when they need to know whether to trust it. */
        if (!verdict.Intact)
        {
            embed.AddField($"{Theme.Warn} EVIDENCE CHAIN BROKEN",
                $"Entry #{verdict.BrokenAt} does not match its recorded hash. Something has " +
                "changed this case's evidence outside the bot - by a direct database edit, or " +
                "by data corruption. Treat everything from that entry onward as unverified.");
        }

        return embed;
    }

    private EmbedBuilder List()
    {
        var open = cases.Open();
        if (open.Count == 0) return Theme.Success("No open cases", "Everything on record has been resolved or dismissed.");

        var lines = open.Take(20).Select(c =>
            $"`#{c.Id,-4}` **{Sanitize.Code(c.Subject)}** — {CaseTransitions.Name(c.Status)}\n" +
            $"{Sanitize.Message(c.Reason)} · opened {Theme.Relative(c.OpenedAt)}" +
            (c.AssignedTo is { Length: > 0 } a ? $" · {Sanitize.Code(a)}" : " · unassigned"));

        return Theme.Notice($"{open.Count} open case(s)", Theme.Paginate(lines)[0]);
    }

    // ---- helpers ----

    private static EmbedBuilder Describe(CaseResult result, string title, string? note = null)
    {
        if (!result.Ok) return Theme.Failure("Nothing changed", result.Error);

        var embed = Theme.Success(title, $"Case #{result.Case!.Id} — **{Sanitize.Code(result.Case.Subject)}**");
        return note is null ? embed : embed.AddField("Note", note);
    }

    private static int Id(IReadOnlyDictionary<string, object> options) =>
        options.GetValueOrDefault("id") is long id ? (int)id : 0;

    private static string Str(IReadOnlyDictionary<string, object> options, string name) =>
        options.GetValueOrDefault(name)?.ToString() ?? "";

    private static string Duration(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : $"{(int)span.TotalMinutes}m";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
