using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Core.Text;
using PavlovBot.Host.Factions;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/whitelist</c> - add, remove and list faction members.
/// </summary>
/// <remarks>
/// Every write goes through <see cref="RosterService"/>, which enforces the rules the
/// storage cannot: one faction per player, one rank, at most one sub-class, and the
/// per-rank caps. The roster files are plain text the game reads live and nothing stops a
/// name appearing in six of them at once, so the boundary is the only enforcement point.
/// </remarks>
public sealed class WhitelistCommand(RosterService rosters, Access access, Boards boards, ILogger<WhitelistCommand> logger) : ISlashCommand
{
    public string Name => "whitelist";

    public ApplicationCommandProperties Build()
    {
        static SlashCommandOptionBuilder Faction() =>
            new SlashCommandOptionBuilder()
                .WithName("faction").WithDescription("Which faction")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .AddChoice("Gambino", "Gambino").AddChoice("Colombo", "Colombo").AddChoice("NYPD", "NYPD");

        static SlashCommandOptionBuilder Player() =>
            new SlashCommandOptionBuilder()
                .WithName("playerid").WithDescription("Player ID or username")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Manage a faction whitelist")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("add").WithDescription("Whitelist Leader - Add a player to a faction")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player()).AddOption(Faction()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove").WithDescription("Whitelist Leader - Remove a player from a faction")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player()).AddOption(Faction()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list").WithDescription("Show a faction's roster with ranks")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Faction()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("playtime").WithDescription("Whitelisted members' playtime, highest to lowest")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Faction()))
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sub = command.Data.Options.First();
        var options = sub.Options.ToDictionary(o => o.Name, o => o.Value?.ToString() ?? "", StringComparer.Ordinal);
        var factionName = options.GetValueOrDefault("faction") ?? "";
        var faction = FactionRegistry.Get(factionName);

        if (faction is null)
        {
            await Reply(command, Theme.Failure("Unknown faction", $"`{Sanitize.Code(factionName)}` is not a faction.")).ConfigureAwait(false);
            return;
        }

        if (sub.Name == "list")
        {
            await Reply(command, await BuildRosterAsync(faction, ct).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }

        if (sub.Name == "playtime")
        {
            await Reply(command, await BuildPlaytimeAsync(faction, ct).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }

        /* Per-faction authority. A faction leader manages every roster; a per-faction role
           manages only its own - which is what stops the Gambino leader quietly adding
           themselves to the NYPD whitelist. */
        if (!access.CanManage(command.User, faction.Name))
        {
            await Reply(command, Theme.Denied("Not your roster",
                $"You do not manage the **{faction.Name}** whitelist.")).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(options.GetValueOrDefault("playerid") ?? "");
        if (player.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        var result = sub.Name == "add"
            ? await rosters.JoinAsync(faction, player, ct).ConfigureAwait(false)
            : await rosters.LeaveAsync(faction, player, ct).ConfigureAwait(false);

        logger.LogInformation("whitelist {Action} | player=\"{Player}\" | faction={Faction} | by={By} | {Outcome}",
            sub.Name, player, faction.Name, command.User.Username, result.Outcome);

        await Reply(command, Describe(result, player, faction)).ConfigureAwait(false);
    }

    /// <summary>
    /// This faction's members ranked by time on the server.
    /// </summary>
    /// <remarks>
    /// Read from the roster, not from the playtime table: the question is "who on THIS
    /// whitelist is active", so a member with no recorded time still belongs on the list -
    /// as zero. Dropping them would silently answer a different question, and the inactive
    /// members are exactly who this is used to find.
    /// </remarks>
    private async Task<EmbedBuilder> BuildPlaytimeAsync(FactionDefinition faction, CancellationToken ct)
    {
        var roster = await rosters.RosterAsync(faction, ct).ConfigureAwait(false);
        if (roster.Count == 0)
            return Theme.Notice($"{faction.Name} playtime", "Nobody is on this roster.");

        var playtime = boards.Playtime();

        var rows = roster
            .Select(m => (m.Player, Minutes: playtime.GetValueOrDefault(m.Player)?.Minutes ?? 0))
            .OrderByDescending(r => r.Minutes)
            .ThenBy(r => r.Player, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lines = rows.Select((r, i) =>
            $"`{i + 1,2}.` **{Sanitize.Code(r.Player)}** — " +
            (r.Minutes >= 60 ? $"{r.Minutes / 60}h {r.Minutes % 60}m" : $"{r.Minutes}m"));

        var pages = Theme.Paginate(lines);
        return Theme.Notice($"{faction.Name} playtime — {roster.Count} member(s)", pages[0])
            .Brand(pages.Count > 1 ? $"Showing the first of {pages.Count} pages" : null);
    }

    private async Task<EmbedBuilder> BuildRosterAsync(FactionDefinition faction, CancellationToken ct)
    {
        var roster = await rosters.RosterAsync(faction, ct).ConfigureAwait(false);
        if (roster.Count == 0)
            return Theme.Notice($"{faction.Name} whitelist", "Nobody is on this roster.");

        /* Grouped by rank, HIGHEST FIRST. A flat alphabetical list of eighty names answers
           "is X whitelisted" and nothing else; grouped by rank it also answers "who runs
           this faction", which is the question people actually ask. */
        var lines = new List<string>();
        foreach (var rank in faction.Order.Reverse())
        {
            var members = roster.Where(m => string.Equals(m.Rank, rank, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Player, StringComparer.OrdinalIgnoreCase).ToList();
            if (members.Count == 0) continue;

            var cap = faction.CapFor(rank);
            var capLabel = cap == int.MaxValue ? "" : $"/{cap}";
            lines.Add($"**{rank}** ({members.Count}{capLabel})");
            lines.Add(string.Join(", ", members.Select(m => $"`{Sanitize.Code(m.Player)}`")));
        }

        var pages = Theme.Paginate(lines);
        return Theme.Notice($"{faction.Name} whitelist — {roster.Count} member(s)", pages[0])
            .Brand(pages.Count > 1 ? $"Showing the first of {pages.Count} pages" : null);
    }

    private static EmbedBuilder Describe(MembershipDecision decision, string player, FactionDefinition faction) => decision.Outcome switch
    {
        MembershipOutcome.Allowed =>
            Theme.Success("Whitelist updated", $"**{Sanitize.Code(player)}** — {faction.Name} **{decision.Rank}**"),

        MembershipOutcome.AlreadyInAnotherFaction =>
            Theme.Denied("Already in another faction",
                $"**{Sanitize.Code(player)}** belongs to **{decision.Conflict}**. " +
                "Remove them from that faction first - a player may only belong to one."),

        MembershipOutcome.RankFull =>
            Theme.Denied("That rank is full",
                $"**{decision.Rank}** is capped at **{decision.Cap}** and is already at its limit."),

        MembershipOutcome.NoChange =>
            Theme.Notice("Nothing to do", $"**{Sanitize.Code(player)}** is already in that state."),

        MembershipOutcome.NotWhitelisted =>
            Theme.Failure("Not whitelisted", $"**{Sanitize.Code(player)}** is not on this roster."),

        _ => Theme.Failure("Refused", decision.Outcome.ToString()),
    };

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/promotion</c> and <c>/demotion</c> - move a member one step.</summary>
public sealed class RankChangeCommand : ISlashCommand
{
    private readonly RosterService _rosters;
    private readonly Access _access;
    private readonly ILogger _logger;
    private readonly int _direction;

    private RankChangeCommand(RosterService rosters, Access access, ILogger logger, string name, int direction)
    {
        _rosters = rosters;
        _access = access;
        _logger = logger;
        Name = name;
        _direction = direction;
    }

    public static RankChangeCommand Promotion(RosterService r, Access a, ILogger<RankChangeCommand> l) => new(r, a, l, "promotion", +1);
    public static RankChangeCommand Demotion(RosterService r, Access a, ILogger<RankChangeCommand> l) => new(r, a, l, "demotion", -1);

    public string Name { get; }

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription($"Whitelist Leader - Move a member one rank {(_direction > 0 ? "up" : "down")}")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player ID or username", isRequired: true, isAutocomplete: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var player = Sanitize.Id(command.Data.Options.FirstOrDefault()?.Value as string ?? "");
        var membership = await _rosters.FindAsync(player, ct).ConfigureAwait(false);

        if (membership is null)
        {
            await Reply(command, Theme.Failure("Not whitelisted",
                $"**{Sanitize.Code(player)}** is not on any roster.")).ConfigureAwait(false);
            return;
        }

        if (!_access.CanManage(command.User, membership.Faction.Name))
        {
            await Reply(command, Theme.Denied("Not your roster",
                $"You do not manage the **{membership.Faction.Name}** whitelist.")).ConfigureAwait(false);
            return;
        }

        var decision = await _rosters.ChangeRankAsync(membership.Faction, player, _direction, ct).ConfigureAwait(false);

        _logger.LogInformation("{Command} | player=\"{Player}\" | faction={Faction} | by={By} | {Outcome} -> {Rank}",
            Name, player, membership.Faction.Name, command.User.Username, decision.Outcome, decision.Rank ?? "-");

        var embed = decision.Outcome switch
        {
            MembershipOutcome.Allowed => Theme.Success(
                _direction > 0 ? $"{Theme.Rank} Promoted" : "Demoted",
                $"**{Sanitize.Code(player)}** — {membership.Rank} → **{decision.Rank}**"),

            MembershipOutcome.AlreadyHighest => Theme.Notice("Already at the top",
                $"**{Sanitize.Code(player)}** is **{decision.Rank}**, the highest {membership.Faction.Name} rank."),

            MembershipOutcome.AlreadyLowest => Theme.Notice("Already at the bottom",
                $"**{Sanitize.Code(player)}** is **{decision.Rank}**. Remove them from the whitelist instead."),

            /* A demotion into a full rank is refused too. Overflow is overflow regardless of
               direction, and checking only the promote path is a real bug in this shape. */
            MembershipOutcome.RankFull => Theme.Denied("That rank is full",
                $"**{decision.Rank}** is capped at **{decision.Cap}** and is already at its limit."),

            _ => Theme.Failure("Refused", decision.Outcome.ToString()),
        };

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
