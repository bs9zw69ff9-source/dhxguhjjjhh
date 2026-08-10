using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Events;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Events;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/eventlog</c> - one timeline across every system, filtered.
/// </summary>
/// <remarks>
/// THE QUESTION IT ANSWERS is "what happened", which currently means reading a staff channel,
/// a join feed and an application log side by side and correlating by clock. The timeline is
/// one ordered account of all of it.
///
/// EVERY VIEW IS TIME-BOUNDED, with no option to remove the bound. An unbounded query against
/// a table that grows forever is the failure this store was built to avoid, so the window is
/// a required option with a short default rather than something a caller can forget.
///
/// EPHEMERAL. It names players, staff and what they did, and it gets run wherever a moderator
/// happens to be standing.
/// </remarks>
public sealed class EventLogCommand(IEventStore events, Access access) : ISlashCommand
{
    public string Name => "eventlog";

    public bool Ephemeral => true;

    /// <summary>How many events one reply shows.</summary>
    /// <remarks>
    /// Twenty-five, because an embed that needs scrolling is a worse answer than a narrower
    /// filter. The reply says when there is more, which is a prompt to filter rather than to
    /// paginate through hundreds of lines.
    /// </remarks>
    public const int PageSize = 25;

    private static readonly (string Label, TimeSpan Window)[] Windows =
    [
        ("1h", TimeSpan.FromHours(1)),
        ("6h", TimeSpan.FromHours(6)),
        ("24h", TimeSpan.FromDays(1)),
        ("7d", TimeSpan.FromDays(7)),
        ("30d", TimeSpan.FromDays(30)),
    ];

    public ApplicationCommandProperties Build()
    {
        static SlashCommandOptionBuilder Window()
        {
            var option = new SlashCommandOptionBuilder()
                .WithName("window").WithDescription("How far back to look (default 6h)")
                .WithType(ApplicationCommandOptionType.String).WithRequired(false);

            foreach (var (label, _) in Windows) option.AddChoice(label, label);
            return option;
        }

        static SlashCommandOptionBuilder Subject(string name, string description, bool autocomplete) =>
            new SlashCommandOptionBuilder()
                .WithName(name).WithDescription(description)
                .WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .WithAutocomplete(autocomplete);

        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - One timeline across every system")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("recent").WithDescription("Everything, newest first")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Window()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("player").WithDescription("Everything involving one player")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Subject("playerid", "In-game name", autocomplete: true))
                .AddOption(Window()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("staff").WithDescription("Everything one staff member did")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Subject("moderator", "Their Discord username, as the audit log records it", autocomplete: false))
                .AddOption(Window()));

        /* ONE SUBCOMMAND PER CATEGORY, generated from the enum. Hand-writing them is how a
           category added later ends up filterable by the store and invisible from Discord. */
        foreach (var category in Enum.GetValues<EventCategory>())
        {
            builder.AddOption(new SlashCommandOptionBuilder()
                .WithName(category.ToString().ToLowerInvariant())
                .WithDescription($"Only {category.ToString().ToLowerInvariant()} events")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Window()));
        }

        return builder.Build();
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
        var section = sub?.Name ?? "recent";
        var options = sub?.Options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal) ?? [];

        var window = WindowOf(options.GetValueOrDefault("window")?.ToString());
        var since = DateTimeOffset.UtcNow - window.Window;

        var query = section switch
        {
            "player" => new EventQuery(PageSize + 1, since, Player: Sanitize.Id(options.GetValueOrDefault("playerid")?.ToString() ?? "")),
            "staff" => new EventQuery(PageSize + 1, since, Actor: Sanitize.Id(options.GetValueOrDefault("moderator")?.ToString() ?? "")),
            "recent" => new EventQuery(PageSize + 1, since),
            _ => new EventQuery(PageSize + 1, since, Category: Enum.Parse<EventCategory>(section, ignoreCase: true)),
        };

        if (section is "player" or "staff" && string.IsNullOrEmpty(query.Player ?? query.Actor))
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        var found = events.Query(query);

        await Reply(command, Render(found, section, window.Label, query)).ConfigureAwait(false);
    }

    private static EmbedBuilder Render(IReadOnlyList<ServerEvent> found, string section, string window, EventQuery query)
    {
        var subject = query.Player ?? query.Actor;
        var title = subject is not null
            ? $"Timeline — {Sanitize.Code(subject)} — last {window}"
            : $"Timeline — {section} — last {window}";

        if (found.Count == 0)
        {
            /* "NOTHING HAPPENED" AND "NOTHING WAS RECORDED" ARE DIFFERENT, and a moderator
               investigating cannot tell them apart from an empty list. The timeline only
               holds events since it was switched on, so saying so is the difference between
               a useful answer and a misleading one. */
            return Theme.Notice(title,
                "No events in this window.\n\n" +
                "The timeline only holds what has happened since it was enabled, so an empty " +
                "result for an older period means it was not recording yet, not that the " +
                "server was quiet.");
        }

        var more = found.Count > PageSize;
        var page = found.Take(PageSize).ToList();

        var lines = page.Select(e =>
            $"`{EasternTime.Stamp(e.At)}` {Glyph(e.Category)} **{Sanitize.Message(Label(e))}**" +
            (e.Detail is { Length: > 0 } d ? $"\n{Sanitize.Message(d)}" : ""));

        var embed = Theme.Notice(title, Theme.Paginate(lines)[0]);

        return more
            ? embed.Brand($"Showing the {PageSize} most recent. Narrow the window or the filter to see further back.")
            : embed;
    }

    /// <summary>One readable line for an event, whatever produced it.</summary>
    private static string Label(ServerEvent e)
    {
        // "staff.tempban" -> "tempban", which is what the action was called when it happened.
        var action = e.Kind.Contains('.', StringComparison.Ordinal)
            ? e.Kind[(e.Kind.IndexOf('.', StringComparison.Ordinal) + 1)..]
            : e.Kind;

        var who = e.Player is { Length: > 0 } p ? $" {p}" : "";
        var by = e.Actor is { Length: > 0 } a && !EventMapping.IsAutomated(a) ? $" (by {a})" : "";
        var on = e.Server is { Length: > 0 } s ? $" on {s}" : "";

        return $"{action}{who}{by}{on}";
    }

    private static string Glyph(EventCategory category) => category switch
    {
        EventCategory.Security => Theme.Warn,
        EventCategory.Staff => Theme.Deny,
        EventCategory.Economy => Theme.Money,
        EventCategory.Faction => Theme.Rank,
        EventCategory.Server => Theme.Info,
        _ => Theme.Dot,
    };

    internal static (string Label, TimeSpan Window) WindowOf(string? choice) =>
        Windows.FirstOrDefault(w => string.Equals(w.Label, choice, StringComparison.OrdinalIgnoreCase))
            is { Label: not null } match ? match : ("6h", TimeSpan.FromHours(6));

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
