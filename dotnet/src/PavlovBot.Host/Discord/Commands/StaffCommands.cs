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
                         (a.Reason is { Length: > 0 } r ? $" — {Sanitize.Code(Sanitize.RedactPrivate(r))}" : ""));

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
                $"`{Sanitize.Code(b.PlayerId)}` — {Sanitize.Code(Sanitize.RedactPrivate(b.Reason ?? "no reason"))}\n" +
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
            .Where(p => p.Name.Length > 0 && !masters.IsMaster(p.Name) &&
                        !masters.IsProtected(p.Name) && !masters.IsExempt(p.Name))
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

        /* ONE CHOICE PER FACTION AND SUB-CLASS, labelled with the faction and grouped by it.
           Driven off the registry, so adding a sub-class to the data adds it to the picker.

           NOT MERGED BY NAME ANY MORE. Two factions defining the same name (BoS and Enclave
           both have Recon) used to collapse into one "BoS/Enclave - Recon" choice filed under
           the first faction, so the second faction's looked missing. The faction now travels
           in the VALUE, which keeps every label and value unique - Discord rejects the whole
           registration over a duplicate, taking every command off the picker. */
        var choices = SubclassChoices(rosters.Factions);
        if (choices.Count > MaxChoices)
        {
            logger.LogWarning(
                "/subclass has {Count} sub-classes but Discord allows {Max} choices - these are left off the picker: {Dropped}",
                choices.Count, MaxChoices, string.Join(", ", choices.Skip(MaxChoices).Select(c => c.Label)));
        }
        foreach (var choice in choices.Take(MaxChoices))
            subclass.AddChoice(choice.Label, choice.Value);

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

    /// <summary>Discord's limit on the choices of one option.</summary>
    internal const int MaxChoices = 25;

    /// <summary>Discord's limit on a choice's label and on a string value.</summary>
    private const int MaxChoiceLength = 100;

    /// <summary>Separates the faction from the sub-class in a choice value.</summary>
    private const char ValueSeparator = ':';

    /// <summary>One picker entry: a sub-class of one faction.</summary>
    internal sealed record SubclassChoice(string Faction, string Name)
    {
        /// <summary>"Enclave - Recon", cut to Discord's limit.</summary>
        public string Label => Truncate($"{Faction} - {Name}");

        /// <summary>"Enclave:Recon". Parsed back by <see cref="ParseValue"/>.</summary>
        public string Value => Truncate($"{Faction}{ValueSeparator}{Name}");

        private static string Truncate(string text) => text.Length <= MaxChoiceLength ? text : text[..MaxChoiceLength];
    }

    /// <summary>Every sub-class of every faction, one entry each.</summary>
    /// <remarks>
    /// Ordered by faction and then by name, so the picker reads as one faction's sub-classes
    /// followed by the next rather than as registry order - which is insertion order and means
    /// nothing to whoever is looking at the list. Deduplicated on label and value: a clash can
    /// only come from truncating absurdly long names, and one would sink the registration.
    /// </remarks>
    internal static IReadOnlyList<SubclassChoice> SubclassChoices(FactionSet factions)
    {
        ArgumentNullException.ThrowIfNull(factions);

        var labels = new HashSet<string>(StringComparer.Ordinal);
        var values = new HashSet<string>(StringComparer.Ordinal);
        var choices = new List<SubclassChoice>();

        foreach (var faction in factions.All.Values)
        {
            foreach (var name in faction.Subclasses.Keys.Order(StringComparer.OrdinalIgnoreCase))
            {
                var choice = new SubclassChoice(faction.Name, name);
                if (labels.Contains(choice.Label) || values.Contains(choice.Value)) continue;

                labels.Add(choice.Label);
                values.Add(choice.Value);
                choices.Add(choice);
            }
        }

        return choices;
    }

    /// <summary>
    /// The faction (null when the value names none) and sub-class a choice value stands for.
    /// </summary>
    /// <remarks>
    /// A bare name is still accepted: a Discord client holding the previous registration sends
    /// one until it refreshes, and that should keep working rather than fail.
    /// </remarks>
    internal static (string? Faction, string Name) ParseValue(string value)
    {
        var at = value.IndexOf(ValueSeparator, StringComparison.Ordinal);
        return at < 0 ? (null, value.Trim()) : (value[..at].Trim(), value[(at + 1)..].Trim());
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        /* GATED BEFORE ANYTHING IS READ OR WRITTEN. The per-faction check further down is
           the one that decides whether this roster is yours - but it cannot run until the
           member has been resolved, and resolving them discloses their in-game name and, for
           a stale entry, DELETES it. Both happened for anybody who could type the command.

           FactionLeader here means "manages at least one faction", so a faction's own role
           still passes and is then refused by name if the member is not theirs. Somebody who
           manages nothing gets no further, because no outcome was ever possible for them. */
        if (!access.Allows(RequiredAccess.FactionLeader, command))
        {
            await Reply(command, Theme.Denied("Not allowed",
                access.Refusal(RequiredAccess.FactionLeader, command))).ConfigureAwait(false);
            return;
        }

        var (chosenFaction, subclass) = ParseValue(command.Data.Options.First(o => o.Name == "subclass").Value as string ?? "");
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

        /* Two factions can share a sub-class name, so the name alone would put a BoS "Recon"
           pick into the Enclave's file for an Enclave member. The choice says whose it is. */
        if (chosenFaction is not null &&
            !string.Equals(chosenFaction, membership.Faction.Name, StringComparison.OrdinalIgnoreCase))
        {
            await Reply(command, Theme.Failure("Wrong faction's sub-class",
                $"That is a **{chosenFaction}** sub-class, and **{Sanitize.Code(player)}** is in " +
                $"**{membership.Faction.Name}**. Pick the {membership.Faction.Name} one.")).ConfigureAwait(false);
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
            $"{member.Mention} no longer has menu access.")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
