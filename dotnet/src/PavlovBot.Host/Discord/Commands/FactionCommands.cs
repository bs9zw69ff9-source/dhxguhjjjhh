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
public sealed class WhitelistCommand(RosterService rosters, FactionMembers members, Access access, Boards boards, ILogger<WhitelistCommand> logger) : ISlashCommand
{
    public string Name => "whitelist";

    public ApplicationCommandProperties Build()
    {
        SlashCommandOptionBuilder Faction()
        {
            var option = new SlashCommandOptionBuilder()
                .WithName("faction").WithDescription("Which faction")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true);

            // From the registry, so adding a faction does not mean remembering to edit this.
            foreach (var name in rosters.Factions.Names) option.AddChoice(name, name);
            return option;
        }

        /* THE DISCORD USER IS THE HANDLE EVERYWHERE EXCEPT HERE. Adding somebody is the one
           moment the in-game name is genuinely unknown to the bot, so it is asked for once,
           recorded against the account, and never typed again - removal, promotion and
           demotion all take the user. A moderator should not have to look up and spell a
           Pavlov name to demote somebody they can see in the member list. */
        static SlashCommandOptionBuilder Member() =>
            new SlashCommandOptionBuilder()
                .WithName("member").WithDescription("The Discord account")
                .WithType(ApplicationCommandOptionType.User).WithRequired(true);

        static SlashCommandOptionBuilder InGameName() =>
            new SlashCommandOptionBuilder()
                .WithName("ingame_name").WithDescription("Their exact in-game name")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Manage a faction whitelist")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("add").WithDescription("Whitelist Leader - Add a member to a faction")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Faction()).AddOption(Member()).AddOption(InGameName()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove").WithDescription("Whitelist Leader - Remove a member from their faction")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Member()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list").WithDescription("Show a faction's roster")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Faction()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("playtime").WithDescription("Whitelisted members' playtime, highest to lowest")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Faction()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("wipe").WithDescription("Owner - Clear every member from a faction's whitelist")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Faction()))
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sub = command.Data.Options.First();
        var options = sub.Options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal);

        if (sub.Name is "list" or "playtime")
        {
            if (Resolve(options) is not { } readOnly)
            {
                await Reply(command, Theme.Failure("Unknown faction")).ConfigureAwait(false);
                return;
            }

            await Reply(command, sub.Name == "list"
                ? await BuildRosterAsync(readOnly, ct).ConfigureAwait(false)
                : await BuildPlaytimeAsync(readOnly, ct).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }

        if (sub.Name == "wipe")
        {
            await OfferWipeAsync(command, Resolve(options)).ConfigureAwait(false);
            return;
        }

        if (options.GetValueOrDefault("member") is not IUser member)
        {
            await Reply(command, Theme.Failure("No member given")).ConfigureAwait(false);
            return;
        }

        if (sub.Name == "remove")
        {
            await RemoveAsync(command, member, ct).ConfigureAwait(false);
            return;
        }

        if (Resolve(options) is not { } faction)
        {
            await Reply(command, Theme.Failure("Unknown faction")).ConfigureAwait(false);
            return;
        }

        /* Per-faction authority. A faction leader manages every roster; a per-faction role
           manages only its own - which is what stopped one faction's leader adding
           themselves to the NYPD whitelist. */
        if (!access.CanManage(command.User, faction.Name))
        {
            await Reply(command, Theme.Denied("Not your roster",
                $"You do not manage the **{faction.Name}** whitelist.")).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(options.GetValueOrDefault("ingame_name")?.ToString() ?? "");
        if (player.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        var result = await rosters.JoinAsync(faction, player, ct).ConfigureAwait(false);

        /* THE INDEX FOLLOWS THE FILE. Recorded only once the roster write succeeded, so a
           failed add does not leave a membership on record that the game has never heard of -
           /promotion would then act on somebody who is not whitelisted. */
        if (result.Outcome == MembershipOutcome.Allowed)
            await members.RememberAsync(member.Id,
                new FactionMember(faction.Name, player, DateTimeOffset.UtcNow, command.User.Username), ct)
                .ConfigureAwait(false);

        logger.LogInformation("whitelist add | member={Member} | player=\"{Player}\" | faction={Faction} | by={By} | {Outcome}",
            member.Id, player, faction.Name, command.User.Username, result.Outcome);

        await Reply(command, Describe(result, player, faction)).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove somebody by Discord account, using the faction recorded when they were added.
    /// </summary>
    /// <remarks>
    /// A member with no recorded faction was whitelisted by hand or before the index existed.
    /// They are still whitelisted in the game, and saying so is more useful than a bare
    /// failure - the fix is to re-add them through the command, which records the link.
    /// </remarks>
    private async Task RemoveAsync(SocketSlashCommand command, IUser member, CancellationToken ct)
    {
        if (members.Of(member.Id) is not { } recorded)
        {
            await Reply(command, Theme.Failure("No membership on record",
                $"{member.Mention} has no faction recorded against their account. They may still be " +
                "whitelisted in game under a name the bot does not know - re-add them with " +
                "`/whitelist add` to link the two, then remove.")).ConfigureAwait(false);
            return;
        }

        if (rosters.Factions.Get(recorded.Faction) is not { } faction)
        {
            await Reply(command, Theme.Failure("That faction is gone",
                $"{member.Mention} is recorded in **{Sanitize.Code(recorded.Faction)}**, which no longer exists.")).ConfigureAwait(false);
            return;
        }

        if (!access.CanManage(command.User, faction.Name))
        {
            await Reply(command, Theme.Denied("Not your roster",
                $"You do not manage the **{faction.Name}** whitelist.")).ConfigureAwait(false);
            return;
        }

        var result = await rosters.LeaveAsync(faction, recorded.Name, ct).ConfigureAwait(false);

        // Forgotten whatever the roster said. An entry pointing at a name that is not on a
        // list any more is the stale state ForgetMissingAsync exists to clear.
        await members.ForgetAsync(member.Id, ct).ConfigureAwait(false);

        logger.LogInformation("whitelist remove | member={Member} | player=\"{Player}\" | faction={Faction} | by={By} | {Outcome}",
            member.Id, recorded.Name, faction.Name, command.User.Username, result.Outcome);

        await Reply(command, Describe(result, recorded.Name, faction)).ConfigureAwait(false);
    }

    /// <summary>
    /// Ask before emptying a roster. Nothing is written until the button is pressed.
    /// </summary>
    /// <remarks>
    /// OWNER, NOT WHITELIST LEADER. Every other subcommand here moves one person, and a
    /// mistake costs one re-add. This empties a faction, and there is no undo inside the bot:
    /// the game reads the file live, so the members are out the moment it is written. The
    /// per-faction managers who run the rosters day to day deliberately cannot reach it.
    ///
    /// A CONFIRMATION STEP, because the destructive path is otherwise indistinguishable from
    /// the harmless one - `/whitelist list` and `/whitelist wipe` are one arrow key apart in
    /// Discord's picker, take the same single argument, and would both complete instantly.
    /// The count is read now and shown, so the press is made against the real size of what
    /// is about to go rather than against a guess.
    /// </remarks>
    private async Task OfferWipeAsync(SocketSlashCommand command, FactionDefinition? faction)
    {
        if (faction is null)
        {
            await Reply(command, Theme.Failure("Unknown faction")).ConfigureAwait(false);
            return;
        }

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed",
                access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var roster = await rosters.RosterAsync(faction).ConfigureAwait(false);
        if (roster.Count == 0)
        {
            await Reply(command, Theme.Notice("Nothing to wipe",
                $"**{faction.Name}** has no members.")).ConfigureAwait(false);
            return;
        }

        var embed = Theme.Denied($"Wipe the {faction.Name} whitelist?",
            $"This clears **{roster.Count}** member(s) from every **{faction.Name}** roster file, " +
            "including the spawn file. They lose faction access in game as soon as it is written.\n\n" +
            "A copy of each file is kept in the backup directory, so this is recoverable by hand " +
            "on the server - but not from Discord.");

        await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
            m.Components = WhitelistWipe.Controls(faction.Name, command.User.Id);
        }).ConfigureAwait(false);
    }

    private FactionDefinition? Resolve(IReadOnlyDictionary<string, object> options) =>
        rosters.Factions.Get(options.GetValueOrDefault("faction")?.ToString());

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

            lines.Add($"**{rank}** ({members.Count})");
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

        MembershipOutcome.NoChange =>
            Theme.Notice("Nothing to do", $"**{Sanitize.Code(player)}** is already in that state."),

        MembershipOutcome.NotWhitelisted =>
            Theme.Failure("Not whitelisted", $"**{Sanitize.Code(player)}** is not on this roster."),

        _ => MembershipReply.Fallback(decision),
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
    private readonly FactionMembers _members;
    private readonly Access _access;
    private readonly ILogger _logger;
    private readonly int _direction;

    private RankChangeCommand(RosterService rosters, FactionMembers members, Access access, ILogger logger, string name, int direction)
    {
        _rosters = rosters;
        _members = members;
        _access = access;
        _logger = logger;
        Name = name;
        _direction = direction;
    }

    public static RankChangeCommand Promotion(RosterService r, FactionMembers m, Access a, ILogger<RankChangeCommand> l) => new(r, m, a, l, "promotion", +1);
    public static RankChangeCommand Demotion(RosterService r, FactionMembers m, Access a, ILogger<RankChangeCommand> l) => new(r, m, a, l, "demotion", -1);

    public string Name { get; }

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription($"Whitelist Leader - Move a member one rank {(_direction > 0 ? "up" : "down")}")
            .AddOption("member", ApplicationCommandOptionType.User, "The Discord account", isRequired: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Data.Options.FirstOrDefault()?.Value is not IUser member)
        {
            await Reply(command, Theme.Failure("No member given")).ConfigureAwait(false);
            return;
        }

        /* BY ACCOUNT, NOT BY NAME. The index records which in-game name belongs to which
           Discord user at the moment they are whitelisted, so nobody has to look up and spell
           a Pavlov name to move somebody one rank. */
        if (_members.Of(member.Id) is not { } recorded)
        {
            await Reply(command, Theme.Failure("No membership on record",
                $"{member.Mention} has no faction recorded against their account. Re-add them " +
                "with `/whitelist add` to link their in-game name, then try again.")).ConfigureAwait(false);
            return;
        }

        var player = recorded.Name;
        var membership = await _rosters.FindAsync(player, ct).ConfigureAwait(false);

        /* THE ROSTER FILE IS THE AUTHORITY, NOT THE INDEX. An entry can outlive the roster
           it describes - somebody edits a file by hand, or the removal wrote the file and
           then failed. Trusting the index here would promote somebody who is not in the
           faction at all, so the index is treated as a lookup for the name and the file is
           still what says whether they are a member. The stale entry is dropped on the way
           past, which is the only way it ever gets cleaned up. */
        if (membership is null)
        {
            await _members.ForgetAsync(member.Id, ct).ConfigureAwait(false);
            await Reply(command, Theme.Failure("Not whitelisted",
                $"{member.Mention} is recorded as **{Sanitize.Code(player)}**, who is not on any roster. " +
                "The stale record has been cleared.")).ConfigureAwait(false);
            return;
        }

        if (!_access.CanManage(command.User, membership.Faction.Name))
        {
            await Reply(command, Theme.Denied("Not your roster",
                $"You do not manage the **{membership.Faction.Name}** whitelist.")).ConfigureAwait(false);
            return;
        }

        /* A FACTION WITH NO LADDER HAS NOWHERE TO MOVE SOMEBODY. The mafias are spawn access:
           one file, one nominal rank. Refused with the reason rather than reported as a
           no-op, because "nothing happened" reads like a bug and sends somebody to check
           whether the command is broken. */
        if (!membership.Faction.HasRanks)
        {
            await Reply(command, Theme.Failure($"{membership.Faction.Name} has no ranks",
                $"**{membership.Faction.Name}** is spawn access only - there is nothing to " +
                $"{(_direction > 0 ? "promote" : "demote")} {member.Mention} to. Use " +
                "`/whitelist remove` to take their access away.")).ConfigureAwait(false);
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

            _ => MembershipReply.Fallback(decision),
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
