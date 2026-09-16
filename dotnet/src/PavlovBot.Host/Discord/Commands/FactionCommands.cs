using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Core.Text;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Moderation;

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
public sealed class WhitelistCommand(RosterService rosters, FactionMembers members, Access access, Boards boards, AuditLog audit, ILogger<WhitelistCommand> logger) : ISlashCommand
{
    public string Name => "whitelist";

    /// <summary>Only the person who ran it sees the reply.</summary>
    /// <remarks>
    /// ROSTER WORK IS ADMIN TRAFFIC, not something a channel needs a copy of. Every
    /// subcommand here names a player and what was done to them, and running a few of them
    /// in a row filled whatever channel the moderator happened to be standing in.
    ///
    /// THE AUDIT TRAIL IS NOT WHAT THIS HIDES, which is the only reason it is safe to do.
    /// AuditLog records every change and the staff log channels still receive them, so what
    /// stops being public is the operator's own console output - not the record of it. A
    /// moderator who wants to show somebody the result can say so; one who does not should
    /// not have to.
    ///
    /// The wipe confirmation goes ephemeral with everything else. Its component carries the
    /// id of whoever opened it and re-checks access on the click, so it was never the public
    /// message that made it safe.
    /// </remarks>
    public bool Ephemeral => true;

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
                .AddOption(Faction()).AddOption(Member()).AddOption(InGameName())
                /* OPTIONAL, and named "rank" so it shares the autocomplete setrank already
                   uses - the gateway dispatches on the focused option's name, so any
                   subcommand with a rank field gets the right suggestions for free.

                   Left out, somebody is added at the bottom of the ladder, which is what
                   this has always done and is right for an actual recruit. Given, they land
                   where they were hired, without a second command. */
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("rank").WithDescription("Start them here instead of the bottom rank")
                    .WithType(ApplicationCommandOptionType.String).WithRequired(false).WithAutocomplete(true))
                .AddOption("hold_ranks", ApplicationCommandOptionType.Boolean,
                    "Keep every rank at or below theirs on promotion, not just the one they hold",
                    isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("setrank").WithDescription("Whitelist Leader - Put a member at a rank directly")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Faction()).AddOption(Member())
                /* AUTOCOMPLETED, NOT A CHOICE LIST, because the legal ranks depend on the
                   faction picked in the option beside this one and Discord has no way to
                   express that in a static list. A flat list of every rank in every faction
                   would also run past the 25-choice cap the moment a faction is added. */
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("rank").WithDescription("Which rank - suggestions follow the faction you picked")
                    .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true)))
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

        if (sub.Name == "setrank")
        {
            await SetRankAsync(command, member, Resolve(options),
                options.GetValueOrDefault("rank")?.ToString() ?? "", ct).ConfigureAwait(false);
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

        /* CHECKED BEFORE ANYTHING IS WRITTEN. A bad rank found after the join would leave
           them added at the bottom with the command reporting a failure - half done, and the
           half that happened is the half nobody was told about. */
        var requestedRank = (options.GetValueOrDefault("rank")?.ToString() ?? "").Trim();
        if (requestedRank.Length > 0)
        {
            if (!faction.HasRanks)
            {
                await Reply(command, Theme.Failure($"{faction.Name} has no ranks",
                    $"**{faction.Name}** is spawn access only - there is no ladder to start " +
                    "anybody on. Leave the rank out.")).ConfigureAwait(false);
                return;
            }

            if (!faction.Order.Any(r => string.Equals(r, requestedRank, StringComparison.OrdinalIgnoreCase)))
            {
                await Reply(command, Theme.Failure("No such rank",
                    $"**{faction.Name}** has no rank called **{Sanitize.Code(requestedRank)}**.\n\n" +
                    $"Its ranks are: {string.Join(", ", faction.Order.Select(r => $"**{r}**"))}.")).ConfigureAwait(false);
                return;
            }
        }

        var result = await rosters.JoinAsync(faction, player, ct).ConfigureAwait(false);

        /* THE INDEX FOLLOWS THE FILE. Recorded only once the roster write succeeded, so a
           failed add does not leave a membership on record that the game has never heard of -
           /promotion would then act on somebody who is not whitelisted. */
        /* NULL MEANS NOT SUPPLIED, and the difference is the whole of this. hold_ranks is an
           OPTIONAL boolean that used to be read as `?? false`, so leaving the box unticked
           wrote false over a preference somebody had already set - and every later add for
           that member turned it off silently. Fixing a typo in an in-game name does that. So
           does using the rank option to place them, because both go through here.

           The symptom is not the flag failing to work. It is a promotion stripping the ranks
           below, long after anybody ticked the box, with nothing in between that looks like
           it touched the setting. The same rule /setroles and /setrconroles already follow:
           only what was named is changed. */
        var holdRanksOption = options.GetValueOrDefault("hold_ranks") as bool?;
        var holdRanks = holdRanksOption ?? members.Of(member.Id)?.HoldsAllRanks ?? false;

        /* RECORDED ON A NO-CHANGE TOO, which is what makes the flag settable at all. Re-running
           add for somebody already whitelisted answers NoChange and writes no roster file - but
           the preference is not roster state, and refusing to update it would leave no way to
           turn this on for an existing member short of removing and re-adding them. */
        if (result.Outcome is MembershipOutcome.Allowed or MembershipOutcome.NoChange)
        {
            var existing = members.Of(member.Id);
            await members.RememberAsync(member.Id,
                new FactionMember(
                    faction.Name,
                    result.Outcome == MembershipOutcome.Allowed ? player : existing?.Name ?? player,
                    existing?.At ?? DateTimeOffset.UtcNow,
                    existing?.By ?? command.User.Username)
                {
                    HoldsAllRanks = holdRanks,
                }, ct).ConfigureAwait(false);
        }

        /* AFTER THE MEMBERSHIP IS RECORDED, and that order is load-bearing. SetRankAsync
           consults the hold-all-ranks preference through a callback that reads this index, so
           applying the rank first would write the roster files against a flag that is not
           saved yet - and somebody added with hold_ranks would silently get only their top
           rank. */
        var rank = result.Rank;
        var moved = false;
        if (requestedRank.Length > 0 && result.Outcome is MembershipOutcome.Allowed or MembershipOutcome.NoChange)
        {
            var placed = await rosters.SetRankAsync(faction, player, requestedRank, ct).ConfigureAwait(false);

            /* NoChange here means they were already at the requested rank, which is a
               success for this command even though the rank write did nothing. */
            if (placed.Outcome is MembershipOutcome.Allowed or MembershipOutcome.NoChange)
            {
                rank = placed.Rank;
                moved = placed.Outcome == MembershipOutcome.Allowed;
            }
            else
            {
                /* THE ADD STILL STOOD. Reporting only the rank failure would leave somebody
                   on the roster believing nothing happened - and the ranks were validated
                   above, so reaching here means the roster write itself failed. */
                logger.LogWarning("whitelist add | player=\"{Player}\" | added to {Faction} but could not be placed at {Rank}: {Outcome}",
                    player, faction.Name, requestedRank, placed.Outcome);

                await Reply(command, MembershipReply.Fallback(placed)
                    .AddField($"{Theme.Warn} They were still added",
                        $"**{Sanitize.Code(player)}** is on the **{faction.Name}** roster at " +
                        $"**{result.Rank}**. Set the rank with `/whitelist setrank`.")).ConfigureAwait(false);
                return;
            }
        }

        logger.LogInformation("whitelist add | member={Member} | player=\"{Player}\" | faction={Faction} | rank={Rank} | by={By} | {Outcome}",
            member.Id, player, faction.Name, rank ?? "-", command.User.Username, result.Outcome);

        /* AUDITED ONLY WHEN THE ROSTER ACTUALLY CHANGED. A refusal is already answered to the
           person who ran it, and recording one as a staff action would put "added to NCR" in
           the log for somebody who was never added.

           THIS WAS MISSING ENTIRELY. Roster changes went to the application log and nowhere
           else - not the audit store, not the staff channels, not the timeline - even though
           EventMapping has categorised whitelist-add and whitelist-remove as Faction events
           all along, waiting for a call that was never written. Who put whom on a roster is
           exactly the question a log exists to answer. */
        // Audited when the roster changed EITHER WAY - the join, or only the rank. A move on
        // somebody already whitelisted is still somebody's rank being set by a staff member.
        if (result.Outcome == MembershipOutcome.Allowed || moved)
        {
            await audit.RecordAsync("whitelist-add", command.User.Username, player,
                $"{faction.Name} {rank}".Trim(), ct).ConfigureAwait(false);
        }

        /* THE RANK THEY ENDED AT, not the one the join landed on. Reporting the default
           after placing them somewhere else is how somebody runs the command twice.

           AND "NOTHING TO DO" HAS TO STOP BEING TRUE when a rank was applied. Re-running add
           on an existing member answers NoChange for the join, and if the rank moved them
           then something plainly did happen - saying otherwise sends somebody to check
           whether the command is broken. */
        var reply = moved && result.Outcome == MembershipOutcome.NoChange
            ? Theme.Success("Whitelist updated", $"**{Sanitize.Code(player)}** — {faction.Name} **{rank}**")
            : Describe(result with { Rank = rank }, player, faction);

        /* STATED EITHER WAY, not only when it is on. Off is the state somebody is looking at
           when they report that holding ranks is not working, and a field that simply is not
           there says nothing about why - it reads the same as the feature not existing. */
        if (result.Outcome is MembershipOutcome.Allowed or MembershipOutcome.NoChange)
        {
            reply.AddField(holdRanks ? "Holds every rank" : "Holds one rank",
                holdRanks
                    ? "They keep the ranks below too, so they hold every loadout up to their own. " +
                      "A demotion still takes back anything above."
                    : "A promotion moves them up and takes the rank below away. " +
                      "Run this again with `hold_ranks:true` to keep them all.");
        }

        await Reply(command, reply).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove somebody by Discord account, using the faction recorded when they were added.
    /// </summary>
    /// <remarks>
    /// A member with no recorded faction was whitelisted by hand or before the index existed.
    /// They are still whitelisted in the game, and saying so is more useful than a bare
    /// failure - the fix is to re-add them through the command, which records the link.
    /// </remarks>
    /// <summary>
    /// Put a member at a named rank in one step.
    /// </summary>
    /// <remarks>
    /// THE GAP /promotion AND /demotion LEFT. Both move somebody exactly one place, which is
    /// right for the ordinary case and wrong for the two that come up most - hiring straight
    /// into a rank, and dropping somebody several at once. Either meant running the same
    /// command four or five times, rewriting the roster files on each pass, with the member
    /// briefly holding every rank on the way.
    ///
    /// THE FACTION IS ASKED FOR rather than read off the membership index, which is what the
    /// rank commands do. Setting a rank is the operation somebody reaches for when the
    /// records and the files disagree, so making it depend on the index would mean it could
    /// not fix the case it exists for. The index is still corrected on the way past.
    /// </remarks>
    private async Task SetRankAsync(
        SocketSlashCommand command, IUser member, FactionDefinition? faction, string rank, CancellationToken ct)
    {
        /* GATED FIRST, before anything is read or written - the same reason as the rank
           commands. Resolving the member discloses their in-game name and, for a stale
           entry, deletes it, and neither should happen for somebody who manages nothing. */
        if (!access.Allows(RequiredAccess.FactionLeader, command))
        {
            await Reply(command, Theme.Denied("Not allowed",
                access.Refusal(RequiredAccess.FactionLeader, command))).ConfigureAwait(false);
            return;
        }

        if (faction is null)
        {
            await Reply(command, Theme.Failure("Unknown faction")).ConfigureAwait(false);
            return;
        }

        if (!access.CanManage(command.User, faction.Name))
        {
            await Reply(command, Theme.Denied("Not your roster",
                $"You do not manage the **{faction.Name}** whitelist.")).ConfigureAwait(false);
            return;
        }

        if (!faction.HasRanks)
        {
            await Reply(command, Theme.Failure($"{faction.Name} has no ranks",
                $"**{faction.Name}** is spawn access only - there is no ladder to place " +
                $"{member.Mention} on.")).ConfigureAwait(false);
            return;
        }

        if (members.Of(member.Id) is not { } recorded)
        {
            await Reply(command, Theme.Failure("No membership on record",
                $"{member.Mention} has no faction on record. Add them with `/whitelist add` first.")).ConfigureAwait(false);
            return;
        }

        var player = recorded.Name;
        var before = await rosters.FindAsync(player, ct).ConfigureAwait(false);

        /* THE ROSTER FILE IS THE AUTHORITY, NOT THE INDEX - same rule as the rank commands.
           An index entry outlives the roster it describes whenever a file is edited by hand,
           and acting on it would place somebody who is not in the faction at all. */
        if (before is null)
        {
            await members.ForgetAsync(member.Id, ct).ConfigureAwait(false);
            await Reply(command, Theme.Failure("Not whitelisted",
                $"{member.Mention} is recorded as **{Sanitize.Code(player)}**, who is not on any roster. " +
                "The stale record has been cleared.")).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(before.Faction.Name, faction.Name, StringComparison.OrdinalIgnoreCase))
        {
            /* REFUSED RATHER THAN MOVED. A player belongs to one faction, and silently
               switching them would strip their old roster as a side effect of what reads
               like a rank change. Removing and re-adding is the deliberate way to do it. */
            await Reply(command, Theme.Failure("Wrong faction",
                $"{member.Mention} is in **{before.Faction.Name}**, not **{faction.Name}**. " +
                "Use `/whitelist remove` then `/whitelist add` to move them.")).ConfigureAwait(false);
            return;
        }

        var decision = await rosters.SetRankAsync(faction, player, rank, ct).ConfigureAwait(false);

        logger.LogInformation("setrank | player=\"{Player}\" | faction={Faction} | by={By} | {Outcome} -> {Rank}",
            player, faction.Name, command.User.Username, decision.Outcome, decision.Rank ?? "-");

        if (decision.Outcome == MembershipOutcome.Allowed)
        {
            await audit.RecordAsync("setrank", command.User.Username, player,
                $"{faction.Name} {before.Rank} -> {decision.Rank}", ct).ConfigureAwait(false);
        }

        var embed = decision.Outcome switch
        {
            MembershipOutcome.Allowed => Theme.Success($"{Theme.Rank} Rank set",
                $"**{Sanitize.Code(player)}** — {before.Rank} → **{decision.Rank}**"),

            MembershipOutcome.NoChange => Theme.Notice("Already there",
                $"**{Sanitize.Code(player)}** is already **{decision.Rank}**. Nothing was written."),

            MembershipOutcome.NoSuchRank => Theme.Failure("No such rank",
                $"**{faction.Name}** has no rank called **{Sanitize.Code(rank)}**.\n\n" +
                $"Its ranks are: {string.Join(", ", faction.Order.Select(r => $"**{r}**"))}."),

            _ => MembershipReply.Fallback(decision),
        };

        await Reply(command, embed).ConfigureAwait(false);
    }

    private async Task RemoveAsync(SocketSlashCommand command, IUser member, CancellationToken ct)
    {
        /* Gated up front for the same reason the rank commands are: the per-faction check
           cannot run until the member is resolved, and resolving them tells the caller
           whether that person is whitelisted and in which faction. No write happens before
           it here, but the disclosure did. */
        if (!access.Allows(RequiredAccess.FactionLeader, command))
        {
            await Reply(command, Theme.Denied("Not allowed",
                access.Refusal(RequiredAccess.FactionLeader, command))).ConfigureAwait(false);
            return;
        }

        if (members.Of(member.Id) is not { } recorded)
        {
            await Reply(command, Theme.Failure("No membership on record",
                $"{member.Mention} has no faction on record. They may still be whitelisted in game " +
                "under a name the bot does not know — `/whitelist add` links the two, then " +
                "remove.")).ConfigureAwait(false);
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

        if (result.Outcome == MembershipOutcome.Allowed)
        {
            await audit.RecordAsync("whitelist-remove", command.User.Username, recorded.Name,
                faction.Name, ct).ConfigureAwait(false);
        }

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
            $"Clears **{roster.Count}** member(s) from every **{faction.Name}** roster file, spawn " +
            "file included. They lose access in game as soon as it is written.\n\n" +
            "Backups are kept on the server, so it is recoverable by hand — not from Discord.");

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

        // Their sub-classes, read once for the whole roster. A member with one is tagged; a
        // member without shows as before, so the list stays legible for factions with none.
        var subclasses = await rosters.SubclassesAsync(faction, ct).ConfigureAwait(false);

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
            lines.Add(string.Join(", ", members.Select(m =>
                subclasses.TryGetValue(m.Player, out var subclass)
                    ? $"`{Sanitize.Code(m.Player)}` ({Sanitize.Markdown(subclass)})"
                    : $"`{Sanitize.Code(m.Player)}`")));
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
    private readonly AuditLog _audit;
    private readonly ILogger _logger;
    private readonly int _direction;

    private RankChangeCommand(RosterService rosters, FactionMembers members, Access access, AuditLog audit, ILogger logger, string name, int direction)
    {
        _rosters = rosters;
        _members = members;
        _access = access;
        _audit = audit;
        _logger = logger;
        Name = name;
        _direction = direction;
    }

    public static RankChangeCommand Promotion(RosterService r, FactionMembers m, Access a, AuditLog d, ILogger<RankChangeCommand> l) => new(r, m, a, d, l, "promotion", +1);
    public static RankChangeCommand Demotion(RosterService r, FactionMembers m, Access a, AuditLog d, ILogger<RankChangeCommand> l) => new(r, m, a, d, l, "demotion", -1);

    public string Name { get; }

    /// <summary>Only the person who ran it sees the reply.</summary>
    /// <remarks>
    /// THE SAME CALL AS /whitelist, for the same reason. A rank change is admin traffic: it
    /// names a player and what was done to them, and running a few in a row filled whatever
    /// channel the moderator happened to be standing in. These two do exactly the work
    /// /whitelist setrank does, and it made no sense for one to be quiet and the others loud.
    ///
    /// THE AUDIT TRAIL IS NOT WHAT THIS HIDES, which is the only reason it is safe. Every
    /// change is still recorded by AuditLog and still reaches the staff log channels, so what
    /// stops being public is the operator's own console output - not the record of it.
    ///
    /// BOTH DIRECTIONS, because this is one class and a demotion is if anything the one you
    /// would rather not announce in the channel the member is reading.
    /// </remarks>
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription($"Whitelist Leader - Move a member one rank {(_direction > 0 ? "up" : "down")}")
            .AddOption("member", ApplicationCommandOptionType.User, "The Discord account", isRequired: true)
            .Build();

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
        if (!_access.Allows(RequiredAccess.FactionLeader, command))
        {
            await Reply(command, Theme.Denied("Not allowed",
                _access.Refusal(RequiredAccess.FactionLeader, command))).ConfigureAwait(false);
            return;
        }

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

        // Same gap, same fix: a rank change is a staff action and was recorded nowhere a
        // human reads. Name is "promotion" or "demotion", both already mapped as Faction.
        if (decision.Outcome == MembershipOutcome.Allowed)
        {
            await _audit.RecordAsync(Name, command.User.Username, player,
                $"{membership.Faction.Name} {membership.Rank} -> {decision.Rank}", ct).ConfigureAwait(false);
        }

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
