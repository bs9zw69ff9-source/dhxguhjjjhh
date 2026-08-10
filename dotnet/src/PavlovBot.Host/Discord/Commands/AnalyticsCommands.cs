using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Events;
using PavlovBot.Core.Text;
using PavlovBot.Host.Events;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>Windows every analytics surface offers, so they cannot disagree.</summary>
internal static class AnalyticsWindows
{
    internal static readonly (string Label, TimeSpan Window)[] All =
    [
        ("24h", TimeSpan.FromDays(1)),
        ("7d", TimeSpan.FromDays(7)),
        ("30d", TimeSpan.FromDays(30)),
        ("90d", TimeSpan.FromDays(90)),
    ];

    internal static SlashCommandOptionBuilder Option()
    {
        var option = new SlashCommandOptionBuilder()
            .WithName("window").WithDescription("Period to report on (default 7d)")
            .WithType(ApplicationCommandOptionType.String).WithRequired(false);

        foreach (var (label, _) in All) option.AddChoice(label, label);
        return option;
    }

    internal static (string Label, TimeSpan Window) Of(string? choice) =>
        All.FirstOrDefault(w => string.Equals(w.Label, choice, StringComparison.OrdinalIgnoreCase))
            is { Label: not null } match ? match : ("7d", TimeSpan.FromDays(7));
}

/// <summary>
/// <c>/serverstats</c> - what the timeline says about the servers.
/// </summary>
/// <remarks>
/// EVERY NUMBER IS COMPUTED IN SQL and every number is bounded to a stated window. The panel
/// names the window in its title, because a count with no period attached gets read as
/// all-time and then quoted as all-time.
///
/// IT SAYS WHAT IT CANNOT ANSWER. Session counts, session length and retention are not
/// derivable from what the bot records - there is no leave event - and an analytics panel
/// that quietly omits them invites somebody to assume they are zero. Saying so costs one
/// field and prevents a wrong conclusion.
/// </remarks>
public sealed class ServerStatsCommand(AnalyticsService analytics, IEventStore events, Access access) : ISlashCommand
{
    public string Name => "serverstats";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Player and moderation activity over a period")
            .AddOption(AnalyticsWindows.Option())
            .AddOption("server", ApplicationCommandOptionType.String, "Narrow to one server", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var options = command.Data.Options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal);
        var (label, window) = AnalyticsWindows.Of(options.GetValueOrDefault("window")?.ToString());
        var server = options.GetValueOrDefault("server")?.ToString();

        if (events.Count() == 0)
        {
            await Reply(command, Theme.Notice("No timeline data yet",
                "Nothing has been recorded. The timeline starts collecting when the bot does, " +
                "so a fresh deploy has nothing to report until players connect.\n\n" +
                "If this persists, check `EVENT_RETENTION_DAYS` is not 0.")).ConfigureAwait(false);
            return;
        }

        var stats = analytics.Overview(window, server);

        var embed = Theme.Notice($"Server analytics — last {label}" + (server is { Length: > 0 } s ? $" — {Sanitize.Code(s)}" : ""))
            .AddField("Unique players", stats.UniquePlayers.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), inline: true)
            .AddField("Joins", stats.Joins.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), inline: true)
            .AddField("Joins per day", stats.JoinsPerDay >= 1 ? $"{stats.JoinsPerDay:N0}" : "—", inline: true);

        if (stats.BusiestHour is { } peak)
            embed.AddField("Busiest hour (UTC)", $"{peak.Hour}:00 — {peak.Count} joins", inline: true);

        embed.AddField("Staff actions", stats.StaffActions.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), inline: true);
        embed.AddField("Bans", stats.Bans.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), inline: true);
        embed.AddField("Security detections", stats.SecurityDetections.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), inline: true);

        if (stats.Servers.Count > 1)
        {
            var total = Math.Max(stats.Servers.Sum(x => x.Count), 1);
            embed.AddField("By server", string.Join("\n", stats.Servers.Take(10).Select(x =>
                $"`{x.Server,-16}` {x.Count,6:N0}  {100.0 * x.Count / total,5:N1}%")));
        }

        if (stats.PerDay.Count > 1)
        {
            var peakDay = Math.Max(stats.PerDay.Max(d => d.Count), 1);
            embed.AddField("Joins per day", "```\n" + string.Join("\n", stats.PerDay.TakeLast(14).Select(d =>
                $"{d.Day}  {new string('#', (int)Math.Round(20.0 * d.Count / peakDay))} {d.Count}")) + "\n```");
        }

        /* NAMED, NOT OMITTED. A panel that silently lacks session counts invites the reader to
           assume they are zero or that nobody thought of it. Neither is true: the data to
           compute them does not exist yet, and saying which data is the only way somebody can
           decide whether to go and collect it. */
        embed.AddField("Not shown, and why",
            "**Sessions and session length** need a leave event; the log tracker raises joins " +
            "and address confirmations only, so a session has a start and no end.\n" +
            "**Average concurrent players** needs a sampled count over time, which is a gauge " +
            "rather than an event stream.\n" +
            "**Retention** needs first-seen per player joined against these events.\n" +
            "None are estimated here - a made-up number in an analytics panel cannot be told " +
            "apart from a real one.");

        await Reply(command, embed).ConfigureAwait(false);
    }

    internal static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary>
/// <c>/staff</c> - who has been moderating, and whether any of it looks unusual.
/// </summary>
/// <remarks>
/// FOR TRANSPARENCY, NOT FOR PUNISHMENT, and the wording throughout says so. A high total is
/// somebody doing the work; the only thing flagged is a RATE far outside that person's own
/// baseline, which is as likely to be a raid being handled as anything else - and either way
/// a second person should know promptly.
///
/// ADMIN, not Mod. "What has each moderator been doing" is a management question, and making
/// it Mod would let every moderator audit every other one, which changes the room.
/// </remarks>
public sealed class StaffStatsCommand(AnalyticsService analytics, Access access) : ISlashCommand
{
    public string Name => "staff";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Staff activity, and anything outside somebody's usual rate")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("activity").WithDescription("Everyone, busiest first")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(AnalyticsWindows.Option()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("member").WithDescription("One staff member in detail")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("moderator", ApplicationCommandOptionType.String, "Their username as the audit log records it", isRequired: true)
                .AddOption(AnalyticsWindows.Option()))
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        var options = sub?.Options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal) ?? [];
        var (label, window) = AnalyticsWindows.Of(options.GetValueOrDefault("window")?.ToString());

        await Reply(command, sub?.Name == "member"
            ? Member(Sanitize.Id(options.GetValueOrDefault("moderator")?.ToString() ?? ""), label, window)
            : Leaderboard(label, window)).ConfigureAwait(false);
    }

    private EmbedBuilder Leaderboard(string label, TimeSpan window)
    {
        var rows = analytics.StaffLeaderboard(window);
        if (rows.Count == 0)
            return Theme.Notice($"Staff activity — last {label}", "No staff actions recorded in this window.");

        return Theme.Notice($"Staff activity — last {label}",
            string.Join("\n", rows.Take(20).Select((row, i) =>
                $"`{i + 1,2}.` **{Sanitize.Code(row.Moderator)}** — {row.Actions:N0} action(s)")))
            .AddField("Reading this",
                "A high count is somebody doing the work. Nothing here is a ranking of quality, " +
                "and automated actions are excluded so the responders do not top the list.");
    }

    private EmbedBuilder Member(string moderator, string label, TimeSpan window)
    {
        if (moderator.Length == 0) return Theme.Failure("That name has nothing usable in it");

        var activity = analytics.ActivityOf(moderator, window);
        if (activity.Total == 0)
            return Theme.Notice($"{Sanitize.Code(moderator)} — last {label}", "No actions recorded in this window.");

        var embed = Theme.Notice($"{Sanitize.Code(moderator)} — last {label}")
            .AddField("Total", activity.Total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), inline: true);

        if (activity.BusiestHour is { } peak)
            embed.AddField("Busiest hour (UTC)", $"{peak.Hour}:00 — {peak.Count}", inline: true);

        embed.AddField("Actions", string.Join("\n", activity.Actions.Take(15).Select(a =>
            $"`{a.Count,5:N0}` {Sanitize.Message(a.Action)}")));

        var anomaly = StaffAnomaly.Check(activity.BusiestHour?.Count ?? 0, activity.Total, window);

        if (anomaly.Unusual)
        {
            /* WORDED AS A QUESTION. This fires on a raid being handled at least as often as on
               anything wrong, and an alert phrased as an accusation gets a moderator defensive
               about doing their job quickly. */
            embed.AddField($"{Theme.Warn} Worth a glance",
                $"{anomaly.Explanation}\n\n" +
                "That is a burst against their own usual rate, which a busy evening or a raid " +
                "explains just as well as anything else. **This is not a disciplinary finding** " +
                "and nothing has been done about it.");
        }

        return embed;
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        ServerStatsCommand.Reply(command, embed);
}
