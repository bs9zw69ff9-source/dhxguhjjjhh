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

        await Reply(command, Leaderboard(label, window)).ConfigureAwait(false);
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
                "A count of work done, not a ranking of quality. Automated actions are excluded.");
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
