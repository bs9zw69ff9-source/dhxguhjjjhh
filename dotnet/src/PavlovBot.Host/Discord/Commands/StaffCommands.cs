using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <summary><c>/staffactivity</c> - audit one staffer's moderation actions.</summary>
public sealed class StaffActivityCommand(AuditLog audit, Access access) : ISlashCommand
{
    public string Name => "staffactivity";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Audit one staff member's actions")
            .AddOption("staff", ApplicationCommandOptionType.User, "Whose actions to list", isRequired: true)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("period").WithDescription("How far back (default: all time)")
                .WithType(ApplicationCommandOptionType.String).WithRequired(false)
                .AddChoice("7 days", "7d").AddChoice("30 days", "30d").AddChoice("All time", "all"))
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var staff = command.Data.Options.First(o => o.Name == "staff").Value as IUser;
        var period = command.Data.Options.FirstOrDefault(o => o.Name == "period")?.Value as string ?? "all";

        if (staff is null)
        {
            await Reply(command, Theme.Failure("Need a staff member")).ConfigureAwait(false);
            return;
        }

        DateTimeOffset? since = period switch
        {
            "7d" => DateTimeOffset.UtcNow.AddDays(-7),
            "30d" => DateTimeOffset.UtcNow.AddDays(-30),
            _ => null,
        };

        var actions = audit.By(staff.Username, since);

        if (actions.Count == 0)
        {
            await Reply(command, Theme.Notice("No actions recorded",
                $"{staff.Mention} has taken no moderation actions{(since is null ? "" : " in that period")}.")).ConfigureAwait(false);
            return;
        }

        // Grouped by KIND first, so "what do they mostly do" is answerable at a glance,
        // then the recent detail for anyone who needs the specifics.
        var byKind = actions.GroupBy(a => a.Action, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => $"`{g.Key}` × {g.Count()}");

        var recent = actions.Take(10)
            .Select(a => $"{Theme.Relative(a.At)} — `{a.Action}` **{Sanitize.Code(a.Player)}**" +
                         (a.Reason is { Length: > 0 } r ? $" — {Sanitize.Code(r)}" : ""));

        await Reply(command, Theme.Notice($"{staff.Username} — {actions.Count} action(s)", string.Join("  ", byKind))
            .AddField("Most recent", string.Join("\n", recent))).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/staffleaderboard</c> - staff ranked by actions taken.</summary>
public sealed class StaffLeaderboardCommand(Boards boards, Access access) : ISlashCommand
{
    public string Name => "staffleaderboard";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder().WithName(Name).WithDescription("Admin - Rank staff by moderation actions").Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        // The same builder the auto-posted board uses. Two views that can disagree is worse
        // than one, because then somebody has to work out which is lying.
        var board = boards.BuildStaffBoard();

        await command.ModifyOriginalResponseAsync(m =>
        {
            if (board is null) m.Embed = Theme.Notice("No actions recorded yet").Brand().Build();
            else m.Embed = board;
            m.AllowedMentions = AllowedMentions.None;
        }).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/banlist</c> - every ban currently in force.</summary>
public sealed class BanListCommand(BanService bans) : ISlashCommand
{
    public string Name => "banlist";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder().WithName(Name).WithDescription("Show every ban currently in force").Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var active = bans.ActiveBans();
        if (active.Count == 0)
        {
            await Reply(command, Theme.Success("Nobody is banned")).ConfigureAwait(false);
            return;
        }

        // Permanent first, then soonest to expire - the ones about to lapse are what a
        // moderator scanning the list is looking for.
        var lines = active
            .OrderByDescending(b => b.Permanent)
            .ThenBy(b => b.Expires ?? DateTimeOffset.MaxValue)
            .Select(b =>
                $"`{Sanitize.Code(b.PlayerId)}` — {Sanitize.Code(b.Reason ?? "no reason")}\n" +
                $"{Theme.Dot} {(b.Permanent ? "**Permanent**" : $"expires {Theme.Relative(b.Expires!.Value)}")}" +
                $" · by {b.Moderator ?? "unknown"}");

        var pages = Theme.Paginate(lines);
        await Reply(command, Theme.Punishment($"{Theme.Deny} {active.Count} active ban(s)", pages[0])
            .Brand(pages.Count > 1 ? $"Page 1 of {pages.Count}" : null)).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary>
/// <c>/flush</c> - kick one randomly chosen player.
/// </summary>
/// <remarks>
/// Used to free a slot on a full server. Random rather than "the newest" or "the worst
/// connection", because any rule for choosing turns into an accusation of favouritism -
/// and a coin toss nobody controls is easier to defend than a heuristic nobody can audit.
///
/// Master and exempt names are never chosen: flushing the owner off their own full server
/// is a distinctly unhelpful way to free a slot.
/// </remarks>
public sealed class FlushCommand(
    RconRegistry rcon, BanService bans, IMasterNames masters, AuditLog audit, Access access, ILogger<FlushCommand> logger) : ISlashCommand
{
    public string Name => "flush";

    public ApplicationCommandProperties Build()
    {
        var server = new SlashCommandOptionBuilder()
            .WithName("server").WithDescription("Which server to free a slot on")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);

        foreach (var name in rcon.Servers) server.AddChoice(name, name);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Kick one randomly chosen player to free a slot")
            .AddOption(server)
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

        var server = command.Data.Options.First().Value as string ?? "";
        var roster = rcon.Roster(server);

        if (roster.TakenAt == DateTimeOffset.MinValue)
        {
            await Reply(command, Theme.Failure("No roster yet",
                $"**{server}** has not reported who is online, so there is nobody to choose from.")).ConfigureAwait(false);
            return;
        }

        var candidates = roster.Players
            .Where(p => p.Name.Length > 0 && !masters.IsMaster(p.Name) && !masters.IsExempt(p.Name))
            .ToList();

        if (candidates.Count == 0)
        {
            await Reply(command, Theme.Notice("Nobody to flush",
                "Everyone online is protected or the server is empty.")).ConfigureAwait(false);
            return;
        }

        var chosen = candidates[System.Security.Cryptography.RandomNumberGenerator.GetInt32(candidates.Count)];
        var result = await bans.HardEnforceAsync(chosen.Name,
            chosen.UniqueId is { Length: > 0 } id ? id : null, ban: false, kick: true, ct).ConfigureAwait(false);

        await audit.RecordAsync("flush", command.User.Username, chosen.Name, $"slot freed on {server}", ct).ConfigureAwait(false);
        logger.LogInformation("flush | server={Server} | chosen=\"{Player}\" | by={By} | accepted={Accepted}",
            server, chosen.Name, command.User.Username, result.Servers);

        await Reply(command, result.Landed
            ? Theme.Success("Slot freed", $"**{Sanitize.Code(chosen.Name)}** was chosen at random and kicked from **{server}**.")
                .AddField("Chosen from", $"{candidates.Count} eligible player(s)", true)
            : Theme.Failure("Not kicked", "No server accepted the command.")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/subclass</c> - assign or remove an NYPD sub-class.</summary>
public sealed class SubclassCommand(
    RosterService rosters, FactionMembers members, Access access, ILogger<SubclassCommand> logger) : ISlashCommand
{
    public string Name => "subclass";

    public ApplicationCommandProperties Build()
    {
        var subclass = new SlashCommandOptionBuilder()
            .WithName("subclass").WithDescription("Which sub-class")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);

        // Driven off the registry, so adding a sub-class to the data adds it to the picker.
        foreach (var name in FactionRegistry.All.Values.SelectMany(f => f.Subclasses.Keys).Distinct(StringComparer.Ordinal))
            subclass.AddChoice(name, name);

        /* BY DISCORD ACCOUNT, like promotion, demotion and removal. The in-game name is asked
           for exactly once, at /whitelist add, and recorded against the account; every command
           that acts on an existing member takes the account instead. Typing the Pavlov name
           again here was the last place that rule was broken, and it is the one that fails
           worst - a misspelling matches no roster and answers "not whitelisted", which reads
           as a membership problem rather than a typo. */
        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Whitelist Leader - Assign or remove a sub-class")
            .AddOption("member", ApplicationCommandOptionType.User, "The Discord account", isRequired: true)
            .AddOption(subclass)
            .AddOption("remove", ApplicationCommandOptionType.Boolean, "Remove it instead of assigning", isRequired: false)
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var subclass = command.Data.Options.First(o => o.Name == "subclass").Value as string ?? "";
        var removing = command.Data.Options.FirstOrDefault(o => o.Name == "remove")?.Value as bool? ?? false;

        if (command.Data.Options.FirstOrDefault(o => o.Name == "member")?.Value is not IUser member)
        {
            await Reply(command, Theme.Failure("No member given")).ConfigureAwait(false);
            return;
        }

        if (members.Of(member.Id) is not { } recorded)
        {
            await Reply(command, Theme.Failure("No membership on record",
                $"{member.Mention} has no faction recorded against their account. Re-add them " +
                "with `/whitelist add` to link their in-game name, then try again.")).ConfigureAwait(false);
            return;
        }

        var player = recorded.Name;
        var membership = await rosters.FindAsync(player, ct).ConfigureAwait(false);

        /* THE ROSTER FILE IS THE AUTHORITY, NOT THE INDEX - the same rule /promotion follows.
           An entry outlives the roster it describes whenever somebody edits a file by hand, and
           trusting it here would give a sub-class to a person who is in no faction at all. The
           stale entry is dropped on the way past, which is the only way it gets cleaned up. */
        if (membership is null)
        {
            await members.ForgetAsync(member.Id, ct).ConfigureAwait(false);
            await Reply(command, Theme.Failure("Not whitelisted",
                $"{member.Mention} is recorded as **{Sanitize.Code(player)}**, who is not on any roster. " +
                "Sub-classes are additive to a rank. The stale record has been cleared.")).ConfigureAwait(false);
            return;
        }

        if (!access.CanManage(command.User, membership.Faction.Name))
        {
            await Reply(command, Theme.Denied("Not your roster",
                $"You do not manage the **{membership.Faction.Name}** whitelist.")).ConfigureAwait(false);
            return;
        }

        var decision = await rosters.ChangeSubclassAsync(membership.Faction, player, subclass, removing, ct).ConfigureAwait(false);

        logger.LogInformation("subclass {Action} | player=\"{Player}\" | {Subclass} | by={By} | {Outcome}",
            removing ? "remove" : "assign", player, subclass, command.User.Username, decision.Outcome);

        var embed = decision.Outcome switch
        {
            MembershipOutcome.Allowed => Theme.Success(removing ? "Sub-class removed" : "Sub-class assigned",
                $"**{Sanitize.Code(player)}** — {subclass}\nThey keep their rank of **{membership.Rank}**."),

            MembershipOutcome.AlreadyHasSubclass => Theme.Denied("They already hold one",
                $"**{Sanitize.Code(player)}** is **{decision.Conflict}**. A member may hold at most one sub-class."),

            MembershipOutcome.NoSuchSubclass => Theme.Failure("Not a sub-class of that faction",
                $"**{membership.Faction.Name}** does not define `{Sanitize.Code(subclass)}`."),

            MembershipOutcome.NoChange => Theme.Notice("Nothing to do",
                removing ? "They do not hold it." : "They already hold it."),

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

/// <summary><c>/stripmenu</c> - remove menu access without touching the name binding.</summary>
public sealed class StripMenuCommand(
    RconRegistry rcon, SerializedStore store, Access access, ILogger<StripMenuCommand> logger) : ISlashCommand
{
    public string Name => "stripmenu";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Remove a member's RCON menu access")
            .AddOption("member", ApplicationCommandOptionType.User, "Which Discord member", isRequired: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var member = command.Data.Options.First().Value as IUser;
        if (member is null)
        {
            await Reply(command, Theme.Failure("Need a member")).ConfigureAwait(false);
            return;
        }

        var id = member.Id.ToString(CultureInfo.InvariantCulture);
        var grants = store.Read(Datasets.MenuGrants, new Dictionary<string, string>(StringComparer.Ordinal));

        if (!grants.TryGetValue(id, out var player))
        {
            await Reply(command, Theme.Notice("No menu access", $"{member.Mention} does not hold a menu.")).ConfigureAwait(false);
            return;
        }

        foreach (var server in rcon.Servers)
        {
            try
            {
                // RemoveMenu, not "StripMenu" - RCON+ has no such verb, so revoking looked
                // like it worked and left the menu in place.
                foreach (var line in RconMenu.Revoke(player, wasHighStaff: true))
                    await rcon.SendAsync(server, line, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("StripMenu failed on {Server}: {Message}", server, ex.Message);
            }
        }

        await store.UpdateAsync(Datasets.MenuGrants,
            new Dictionary<string, string>(StringComparer.Ordinal),
            g => { g.Remove(id); return g; }, ct).ConfigureAwait(false);

        logger.LogInformation("stripmenu | member={Member} | player=\"{Player}\" | by={By}", id, player, command.User.Username);

        await Reply(command, Theme.Success("Menu access removed",
            $"{member.Mention} no longer has menu access.")
            .AddField("Name binding", "Unchanged - releasing frees the ACCESS, never the name. Use `/unlinkname` for that.")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
