using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Data;
using PavlovBot.Core.Economy;
using PavlovBot.Core.Factions;
using PavlovBot.Core.Text;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/faction</c> - roster health: who is active, who is not, and how the ranks sit.
/// </summary>
/// <remarks>
/// IT RECOMMENDS AND NEVER ACTS. Nothing here demotes, removes or suspends anybody, and there
/// is no configuration that would let it - the brief permits automatic enforcement only where
/// the config explicitly supports it, and adding such a switch would mean a bot that quietly
/// demoted a Sergeant who was on holiday.
///
/// FACTION LEADER, not Mod. This is the roster's own management view, and the people who need
/// it are the ones who already manage the roster.
/// </remarks>
public sealed class FactionStatsCommand(
    RosterService rosters, Boards boards, SerializedStore store, Access access) : ISlashCommand
{
    public string Name => "factionstats";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build()
    {
        var faction = new SlashCommandOptionBuilder()
            .WithName("faction").WithDescription("Which faction")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);

        foreach (var name in rosters.Factions.Names) faction.AddChoice(name, name);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Faction Leader - Roster activity, rank distribution and who to review")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("overview").WithDescription("Members, activity and rank distribution")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(faction))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("inactive").WithDescription("Members worth reviewing")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(faction))
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.FactionLeader, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.FactionLeader, command))).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        var name = sub?.Options.FirstOrDefault(o => o.Name == "faction")?.Value?.ToString();

        if (rosters.Factions.Get(name) is not { } faction)
        {
            await Reply(command, Theme.Failure("Unknown faction")).ConfigureAwait(false);
            return;
        }

        if (!rosters.Enabled)
        {
            await Reply(command, Theme.Failure("The roster files are unreachable",
                "Check `FACTION_ROLES_PATH`. Without it there is no roster to report on.")).ConfigureAwait(false);
            return;
        }

        var report = FactionActivity.Report(faction, await MembersAsync(faction, ct).ConfigureAwait(false), DateTimeOffset.UtcNow);

        await Reply(command, sub?.Name == "inactive" ? Inactive(report) : Overview(report, faction)).ConfigureAwait(false);
    }

    /// <summary>
    /// The roster, joined to playtime and last-seen.
    /// </summary>
    /// <remarks>
    /// BOTH TABLES READ ONCE, then joined in memory. Looking each member up individually would
    /// be a deserialisation of the whole playtime dataset per member - the N+1 the brief warns
    /// about, and on an eighty-member roster it is eighty full parses for one command.
    /// </remarks>
    private async Task<IReadOnlyList<MemberActivity>> MembersAsync(FactionDefinition faction, CancellationToken ct)
    {
        var roster = await rosters.RosterAsync(faction, ct).ConfigureAwait(false);
        var playtime = boards.Playtime();
        var lastSeen = store.Read(Datasets.LastSeen, new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

        return
        [
            .. roster.Select(m => new MemberActivity(
                m.Player,
                m.Rank,
                playtime.GetValueOrDefault(m.Player)?.Minutes ?? 0,
                lastSeen.TryGetValue(m.Player, out var seen) ? seen : playtime.GetValueOrDefault(m.Player)?.LastSeen)),
        ];
    }

    private static EmbedBuilder Overview(FactionReport report, FactionDefinition faction)
    {
        var embed = Theme.Notice($"{report.Faction} — {report.Total} member(s)")
            .AddField("Active", $"{report.Active} ({report.ActivePercent}%)", inline: true)
            .AddField("Worth reviewing", report.Inactive30.ToString(System.Globalization.CultureInfo.InvariantCulture), inline: true)
            .AddField("Total playtime", $"{report.TotalMinutes / 60:N0}h", inline: true);

        if (faction.HasRanks)
        {
            embed.AddField("Ranks", string.Join("\n", report.RankCounts.Select(r =>
                $"`{r.Count,3}{(r.Cap == int.MaxValue ? "    " : $"/{r.Cap,-3}")}` {Sanitize.Message(r.Rank)}")));
        }

        embed.AddField("Active means",
            $"Seen inside the last {FactionActivity.ActiveWindow.TotalDays:F0} days. " +
            $"Worth reviewing means idle for {FactionActivity.InactiveAfter.TotalDays:F0}+ days, " +
            $"or under {FactionActivity.MinimumMonthlyMinutes} minutes of recorded playtime.");

        return embed;
    }

    private static EmbedBuilder Inactive(FactionReport report)
    {
        if (report.Inactive.Count == 0)
            return Theme.Success($"{report.Faction} — everybody is active", "Nobody meets the review thresholds.");

        /* NEVER-SEEN MEMBERS ARE SEPARATED OUT. Mixed into the idle list they sort to the top
           and read as the most inactive people in the faction, when what they usually are is
           members who predate the bot's tracking. That is a removal list nobody should act on. */
        var idle = report.Inactive.Where(i => i.Days is not null).ToList();
        var unknown = report.Inactive.Where(i => i.Days is null).ToList();

        var embed = Theme.Warning($"{report.Faction} — {report.Inactive.Count} to review");

        if (idle.Count > 0)
        {
            embed.AddField($"Idle ({idle.Count})", Theme.Paginate(idle.Take(25).Select(i =>
                $"`{i.Member.Rank,-16}` **{Sanitize.Code(i.Member.Player)}** — {Sanitize.Message(i.Reason)}"))[0]);
        }

        if (unknown.Count > 0)
        {
            embed.AddField($"No record ({unknown.Count})",
                Theme.Paginate(unknown.Take(15).Select(i => $"`{Sanitize.Code(i.Member.Player)}`"))[0] +
                "\n\nThe bot has never watched these accounts connect. That usually means they " +
                "predate its tracking or play under another name - **not** that they are inactive.");
        }

        embed.AddField("Nothing was changed",
            "This is a list to look at. No rank was altered and nobody was removed - " +
            "use `/demotion` or `/whitelist remove` if that is what you decide.");

        return embed;
    }

    internal static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary>
/// <c>/economy</c> - earnings that do not look like play.
/// </summary>
/// <remarks>
/// EXTENDS THE EXISTING DETECTOR rather than replacing it. MoneyAnomalyDetector already
/// watches a rolling window per player and alerts on a threshold; what it cannot do is explain
/// itself or compare somebody against their own history. This reads the same stored window and
/// answers "why is this suspicious" from it.
///
/// IT FLAGS AND NEVER PUNISHES. A large total is not evidence: a good night, a payout owed for
/// a week arriving at once, and a duplication bug are indistinguishable in a number. The
/// output is a case worth opening.
/// </remarks>
public sealed class EconomyIntelCommand(
    MoneyAnomalyDetector detector, SerializedStore store, Access access) : ISlashCommand
{
    public string Name => "economy";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Earnings that do not look like play")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("anomalies").WithDescription("Everybody whose recent earnings look wrong")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("audit").WithDescription("One player's recent credits, and what they look like")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("playerid").WithDescription("In-game name")
                    .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true)))
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        if (!detector.Enabled)
        {
            await Reply(command, Theme.Warning("Money watching is off",
                "Set `MONEY_ALERT_THRESHOLD` to start recording a rolling earnings window. " +
                "Nothing is being collected until then, so there is nothing to audit.")).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        var windows = store.Read(Datasets.MoneyWindow, new Dictionary<string, EarningWindow>(StringComparer.OrdinalIgnoreCase));

        await Reply(command, sub?.Name == "audit"
            ? Audit(Sanitize.Id(sub.Options.FirstOrDefault()?.Value?.ToString() ?? ""), windows)
            : Anomalies(windows)).ConfigureAwait(false);
    }

    /// <summary>
    /// The server's typical earnings over the window: the MEDIAN, not the mean.
    /// </summary>
    /// <remarks>
    /// The mean is dragged upward by exactly the outliers this is meant to detect - one
    /// exploiter raises the "normal" they are compared against, which is how a detector talks
    /// itself out of its own finding. The median barely moves.
    /// </remarks>
    private static long ServerNormal(IEnumerable<EarningWindow> windows)
    {
        var totals = windows.Select(w => w.Entries.Sum(e => e.Amount)).Where(t => t > 0).OrderBy(t => t).ToList();
        return totals.Count == 0 ? 0 : totals[totals.Count / 2];
    }

    private EmbedBuilder Anomalies(IReadOnlyDictionary<string, EarningWindow> windows)
    {
        var normal = ServerNormal(windows.Values);

        var flagged = windows
            .Select(kv => FraudSignals.Assess(
                kv.Key,
                [.. kv.Value.Entries.Select(e => e.Amount)],
                ownBaseline: 0,   // no per-player history is stored beyond this window
                serverNormal: normal,
                detector.Window))
            .Where(v => v.Suspicious)
            .OrderByDescending(v => v.Earned)
            .ToList();

        if (flagged.Count == 0)
        {
            return Theme.Success("Nothing unusual",
                $"No player's earnings over the last {detector.Window.TotalMinutes:F0} minutes look " +
                $"out of place. Server-typical for that span is {normal:N0}.");
        }

        return Theme.Warning($"{flagged.Count} player(s) worth a look",
            string.Join("\n\n", flagged.Take(10).Select(v =>
                $"**{Sanitize.Code(v.Player)}** — {v.Earned:N0}\n" +
                string.Join("\n", v.Signals.Select(s => $"{Theme.Dot} {Sanitize.Message(s.Summary)}")))))
            .AddField("Nothing was done",
                "These are statistical flags, not findings. A good night and a duplication bug " +
                "look identical in a total. Open a case with `/case open` if one is worth pursuing.");
    }

    private EmbedBuilder Audit(string player, IReadOnlyDictionary<string, EarningWindow> windows)
    {
        if (player.Length == 0) return Theme.Failure("That name has nothing usable in it");

        if (!windows.TryGetValue(player, out var window) || window.Entries.Count == 0)
        {
            return Theme.Notice($"{Sanitize.Code(player)} — no recent credits",
                $"Nothing recorded in the last {detector.Window.TotalMinutes:F0} minutes. The window " +
                "is rolling, so this only ever covers recent activity.");
        }

        var credits = window.Entries.Select(e => e.Amount).ToList();
        var verdict = FraudSignals.Assess(player, [.. credits], 0, ServerNormal(windows.Values), detector.Window);

        var title = $"{Sanitize.Code(player)} — last {detector.Window.TotalMinutes:F0} minutes";

        var embed = (verdict.Suspicious ? Theme.Warning(title) : Theme.Notice(title))
            .AddField("Earned", $"{verdict.Earned:N0}", inline: true)
            .AddField("Credits", credits.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), inline: true)
            .AddField("Largest", $"{credits.Max():N0}", inline: true);

        embed.AddField("Recent credits", "```\n" +
            string.Join("\n", window.Entries.TakeLast(15).Select(e => $"{e.At:HH:mm:ss}  {e.Amount,10:N0}")) + "\n```");

        embed.AddField("Assessment", verdict.Assessment);

        if (verdict.Suspicious)
        {
            embed.AddField("Signals", string.Join("\n", verdict.Signals.Select(s =>
                $"`+{s.Weight,3}` {Sanitize.Message(s.Summary)}")));
        }

        return embed;
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        FactionStatsCommand.Reply(command, embed);
}
