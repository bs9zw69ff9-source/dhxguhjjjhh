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
                $"`{r.Count,3}` {Sanitize.Message(r.Rank)}")));
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
                "\n\nThe bot has never seen these connect — usually they predate its tracking or " +
                "play under another name. **Not** the same as inactive.");
        }

        return embed;
    }

    internal static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
