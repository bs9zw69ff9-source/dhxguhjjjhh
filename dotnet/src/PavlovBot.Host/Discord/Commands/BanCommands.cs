using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>Shared plumbing for the commands that issue and lift bans.</summary>
/// <summary>
/// What the server's own ban file says, on the answers where it is the whole question.
/// </summary>
/// <remarks>
/// THE BOT'S BAN STORE IS NOT THE ONLY THING THAT KEEPS A PLAYER OUT. Pavlov reads its own
/// ban file directly, so somebody listed there is refused by the SERVER whatever this bot
/// thinks.
///
/// This used to be a fixed paragraph of guesswork printed under every negative answer -
/// "if they are still refused in game, maybe the file lists them, check the startup log".
/// It was the correct caveat and it was useless: the moderator asking "why is he still
/// banned" got told where they might go and look. The file is now READ, and the answer
/// says which.
///
/// Rendered on the NEGATIVE answers only. A "yes, banned" reply is already the end of the
/// question, and the sync keeps the file matching the store in that case anyway.
/// </remarks>
internal static class BanFileReport
{
    /// <summary>
    /// An extra line to add under "not banned", or null when there is nothing worth saying.
    /// </summary>
    /// <remarks>
    /// TERSE ON THE CLEAN ANSWER. "Not banned, and not in the server file either, at
    /// /home/steam/.../blacklist.txt" is noise: the moderator asked one yes/no question and got
    /// one yes/no answer, so the clean case returns null and the caller says only "not banned".
    /// The line is kept ONLY where it changes the answer: the player IS in the server's own file
    /// (still banned in game) or the file could not be read (the answer is not trustworthy).
    /// </remarks>
    public static string? Describe(BanFileLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        return lookup.Status switch
        {
            // In the server's own file but not the bot's store: still banned in game. Worth saying.
            BanFileStatus.Read when lookup.Entry is { } entry =>
                $"{Theme.Deny} The server's ban file still lists them, so they're banned in game: " +
                $"{Sanitize.Code(Sanitize.RedactPrivate(entry.Reason))} — {Sanitize.Code(entry.Unban)}",

            // Read, not listed: the clean case. Say nothing extra.
            BanFileStatus.Read => null,

            // The file could not be checked, so "not banned" is not trustworthy - kept short.
            BanFileStatus.Missing =>
                $"{Theme.Warn} Couldn't check the server's ban file (missing) - set `BLACKLIST_PATH`.",

            BanFileStatus.Unreadable =>
                $"{Theme.Warn} Couldn't read the server's ban file - check its permissions.",

            _ => null,
        };
    }
}

public abstract class BanCommandBase : ISlashCommand
{
    protected BanCommandBase(
        BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit, ILogger logger,
        MasterNames masters)
    {
        Masters = masters;
        Bans = bans;
        Tracking = tracking;
        Store = store;
        Access = access;
        Audit = audit;
        Logger = logger;
    }

    protected BanService Bans { get; }
    protected MasterNames Masters { get; }
    protected IpTrackingService Tracking { get; }
    protected SerializedStore Store { get; }
    protected Access Access { get; }
    protected AuditLog Audit { get; }
    protected ILogger Logger { get; }

    public abstract string Name { get; }
    public abstract ApplicationCommandProperties Build();
    public abstract Task HandleAsync(SocketSlashCommand command, CancellationToken ct);

    protected static string? Option(SocketSlashCommand command, string name) =>
        command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as string;

    protected static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Build();
            // Nothing this bot sends should ever ping. A ban reason containing @everyone
            // would otherwise notify the whole server from inside an audit entry.
            m.AllowedMentions = AllowedMentions.None;
        });

    /// <summary>
    /// Write a ban and enforce it.
    /// </summary>
    /// <remarks>
    /// Order matters and is deliberate: RECORD FIRST, then enforce. If enforcement fails
    /// the record still exists, so the sweep and the reconcile will both retry it. Doing it
    /// the other way round means a failed write leaves somebody kicked with nothing saying
    /// why, and nothing to lift.
    /// </remarks>
    protected async Task<EmbedBuilder> IssueBanAsync(
        SocketSlashCommand command, string player, string reason, TimeSpan? duration, CancellationToken ct)
    {
        var name = Sanitize.Id(player);
        if (name.Length == 0)
            return Theme.Failure("That name has nothing usable in it", "After sanitising there was no identifier left to ban.");

        var account = Tracking.AccountByName(name);
        var tier = Access.TierOf(command.User);
        var now = DateTimeOffset.UtcNow;
        var permanent = duration is null;

        /* A MASTER OWNER'S UNBAN STANDS. Re-banning somebody they let back in is overriding
           them, so only another master owner may. */
        if (tier < StaffTier.MasterOwner && Masters.IsPardoned(name))
        {
            return Theme.Denied("Above your authority",
                $"A **{StaffHierarchy.Name(StaffTier.MasterOwner)}** unbanned **{Sanitize.Code(name)}**. " +
                $"Only a {StaffHierarchy.Name(StaffTier.MasterOwner)} can ban them again.");
        }

        var record = new BanRecord
        {
            PlayerId = name,
            /* The identifier the ban is actually ENFORCED against, so the lift can name the
               same thing. Without it, expiry sent an Unban carrying the display name against
               a ban that landed on an EOS id, and the server lifted nothing. */
            UniqueId = account?.Id,
            Reason = Sanitize.Message(reason),
            Moderator = command.User.Username,
            At = now,
            Expires = duration is { } d ? now + d : null,
            Permanent = permanent,
            DurationLabel = duration is { } label ? EasternTime.TimeLeft(now + label, TimeProvider.System) : "Permanent",
            Tier = tier,
        };

        BanRecord? outranked = null;
        var saved = await Store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            /* REPLACING IS LIFTING. A ban issued above your tier cannot be /unbanned by you, so
               it cannot be swapped for a one-minute ban by you either. Checked inside the
               update so a ban landing concurrently is seen. */
            outranked = BanRules.ProtectedFromReplacement(bans, name, tier, now);
            if (outranked is not null) return null;

            // Replace rather than append: two records for one player means an unban lifts
            // one of them and the other quietly re-catches them on the next sweep.
            bans.RemoveAll(b => BanRules.SamePlayer(b.PlayerId, name));
            bans.Add(record);
            return bans;
        }, ct).ConfigureAwait(false);

        if (outranked is not null)
        {
            return Theme.Denied("Above your authority",
                $"**{Sanitize.Code(name)}** is already banned by a **{StaffHierarchy.Name(outranked.Tier ?? StaffTier.None)}**, " +
                $"and a new ban would replace it. You are **{StaffHierarchy.Name(tier)}**.");
        }

        /* NO RECORD, NO BAN. The write can be refused without throwing - an unreadable ban
           list is never written over - and enforcing anyway put a native ban on the server
           that nothing would ever lift: a temp ban turned permanent, reported as a success. */
        if (!saved.Ok)
        {
            Logger.LogError("ban NOT issued for \"{Player}\": the record could not be saved ({Error})", name, saved.Error);
            return Theme.Failure("Ban not issued",
                $"The ban record could not be saved, so nothing was enforced. ({Sanitize.Code(saved.Error ?? "unknown error")})");
        }

        // A master owner banning a player they had pardoned ends the pardon.
        if (tier == StaffTier.MasterOwner) await Masters.ClearPardonAsync(name, ct).ConfigureAwait(false);

        var enforcement = await Bans.HardEnforceAsync(name, account?.Id, ct: ct).ConfigureAwait(false);

        /* The account id is flagged for a PERMANENT ban only. An id flag has no expiry of
           its own, so branding it on a temp ban keeps catching them after they have served
           it - the single worst outcome this system can produce. */
        var flags = account is not null
            ? await Tracking.RequestFlagAsync(account.Id, flagAccountId: permanent, ct).ConfigureAwait(false)
            : null;

        await Audit.RecordAsync(permanent ? "permban" : "tempban", command.User.Username, name, record.Reason, ct)
            .ConfigureAwait(false);

        Logger.LogInformation(
            "{Kind} ban | player=\"{Player}\" | target=\"{Target}\" | by={Moderator} ({Tier}) | " +
            "rcon={Accepted}/{Total} | flagged: ips={Ips} ids={Ids}{Pending} | at={At}",
            permanent ? "permanent" : "temporary", name, enforcement.Target ?? "none",
            command.User.Username, tier, enforcement.Servers, Bans.ServerCount,
            flags?.Ips.Count ?? 0, flags?.Ids.Count ?? 0,
            flags?.Pending == true ? " (pending confirmation)" : "", EasternTime.Stamp(now));

        var embed = Theme.Punishment(
            permanent ? $"{Theme.Deny} Exiled from {Lore.World}" : $"{Theme.Deny} Run out of {Lore.World}",
            $"**{Sanitize.Code(name)}** — {Sanitize.Code(Sanitize.RedactPrivate(record.Reason ?? "no reason given"))}")
            .AddField("Length", permanent ? "Permanent" : $"{record.DurationLabel} (until {Theme.Relative(record.Expires!.Value)})", true)
            .AddField("Issued by", command.User.Username, true);

        if (!enforcement.Landed)
        {
            /* Said out loud, on the reply the moderator is looking at. A ban that recorded
               but did not enforce looks identical to one that worked - right up until the
               player is still in the game. */
            embed.AddField($"{Theme.Warn} Not enforced",
                "The record was saved but no server accepted the command. The sweep will retry.");
        }
        else if (enforcement.Servers < Bans.ServerCount && enforcement.Servers > 0)
        {
            embed.AddField($"{Theme.Warn} Partially enforced", $"{enforcement.Servers} server(s) accepted it.");
        }

        if (flags?.Pending == true)
        {
            embed.AddField("Address not yet known",
                "They have never been seen disconnecting, so no address is confirmed. " +
                "It will be flagged automatically the moment one is.");
        }

        return embed.Brand();
    }
}

/// <summary><c>/tempban</c> - a ban with a length, never a date.</summary>
public sealed class TempBanCommand(
    BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit, ILogger<TempBanCommand> logger,
    MasterNames masters)
    : BanCommandBase(bans, tracking, store, access, audit, logger, masters)
{
    public override string Name => "tempban";

    public override ApplicationCommandProperties Build()
    {
        var duration = new SlashCommandOptionBuilder()
            .WithName("duration").WithDescription("How long the ban lasts")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);

        /* The picker offers LENGTHS, not dates. A moderator has an opinion about "three
           days"; nobody has an opinion about "2026-08-01T14:32Z", and making them compute
           one is how you get bans that are off by a day. */
        foreach (var (value, _) in BanDurations.All) duration.AddChoice(value, value);
        duration.AddChoice("Permanent", BanDurations.Permanent);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Ban a player for a set length of time")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player ID or username", isRequired: true, isAutocomplete: true)
            .AddOption("reason", ApplicationCommandOptionType.String, "Why they are being banned", isRequired: true)
            .AddOption(duration)
            .Build();
    }

    public override async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (!Access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", Access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var player = Option(command, "playerid") ?? "";
        var reason = Option(command, "reason") ?? "No reason given";
        var choice = Option(command, "duration") ?? "";

        var duration = BanDurations.Length(choice);
        if (duration is null && !string.Equals(choice, BanDurations.Permanent, StringComparison.OrdinalIgnoreCase))
        {
            await Reply(command, Theme.Failure("That duration makes no sense",
                "Use a length like `1d`, `3d 4h` or `1mo`. Calendar dates are not accepted.")).ConfigureAwait(false);
            return;
        }

        await Reply(command, await IssueBanAsync(command, player, reason, duration, ct).ConfigureAwait(false)).ConfigureAwait(false);
    }
}

/// <summary><c>/permban</c> - no expiry, and the account id is flagged too.</summary>
public sealed class PermBanCommand(
    BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit, ILogger<PermBanCommand> logger,
    MasterNames masters)
    : BanCommandBase(bans, tracking, store, access, audit, logger, masters)
{
    public override string Name => "permban";

    public override ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Ban a player permanently")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player ID or username", isRequired: true, isAutocomplete: true)
            .AddOption("reason", ApplicationCommandOptionType.String, "Why they are being banned", isRequired: true)
            .Build();

    public override async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (!Access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", Access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var embed = await IssueBanAsync(command, Option(command, "playerid") ?? "",
            Option(command, "reason") ?? "No reason given", duration: null, ct).ConfigureAwait(false);
        await Reply(command, embed).ConfigureAwait(false);
    }
}

/// <summary><c>/unban</c> - lift a ban, subject to the staff hierarchy.</summary>
public sealed class UnbanCommand(
    BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit,
    ServerBanFile banFile, ILogger<UnbanCommand> logger, MasterNames masters)
    : BanCommandBase(bans, tracking, store, access, audit, logger, masters)
{
    public override string Name => "unban";

    public override ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Lift a ban")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player to pardon", isRequired: true, isAutocomplete: true)
            .Build();

    public override async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (!Access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", Access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(Option(command, "playerid") ?? "");
        var existing = Bans.LoadBans().FirstOrDefault(b => BanRules.SamePlayer(b.PlayerId, player));

        if (existing is null)
        {
            /* NO RECORD IS NOT THE SAME AS NOT BANNED. The server reads its ban file itself,
               so a name listed there is refused whatever this bot's store says - and an
               in-game ban that the importer never picked up (a wrong path, a sync that has
               not run) lives ONLY in that file. This used to reply "not banned by this bot"
               and stop, which is a true sentence that leaves the player locked out.

               Lifting anyway is what the moderator asked for. LiftAsync writes the unban
               tombstone, sends the native unban and rewrites the file from the store - and
               the store does not list them, so the rewrite is what removes them. */
            var listed = await banFile.FindAsync(player, ct).ConfigureAwait(false);

            if (!listed.Listed)
            {
                var note = BanFileReport.Describe(listed);
                await Reply(command, Theme.Notice("No ban to lift",
                    $"**{Sanitize.Code(player)}** is not banned{(note is null ? "." : $".\n\n{note}")}"))
                    .ConfigureAwait(false);
                return;
            }

            await Bans.LiftAsync(player, Tracking.AccountByName(player)?.Id, ct).ConfigureAwait(false);
            await Audit.RecordAsync("unban", command.User.Username, player,
                $"Removed from the server's ban file - {listed.Entry!.Reason}", ct).ConfigureAwait(false);

            var fileEmbed = Theme.Success("Removed from the server's ban file",
                    $"**{Sanitize.Code(player)}** had no record with this bot. The server's own ban " +
                    $"file listed them, and that entry is now gone.")
                .AddField("Was", $"{Sanitize.Code(Sanitize.RedactPrivate(listed.Entry.Reason))} — {Sanitize.Code(listed.Entry.Unban)}")
                .AddField("File", $"`{Sanitize.Code(listed.Path ?? "unknown")}`")
                .AddField("Lifted by", command.User.Username, true);
            await PardonIfMasterAsync(command, player, fileEmbed, ct).ConfigureAwait(false);

            await Reply(command, fileEmbed.Brand()).ConfigureAwait(false);
            return;
        }

        /* The hierarchy check. Moderation authority is the one thing a moderator can attack
           with permissions they legitimately hold - without this, any mod can quietly unban
           whoever the owner banned and the audit log records it as routine. */
        var actor = Access.TierOf(command.User);
        if (!StaffHierarchy.CanOverride(actor, existing.Tier))
        {
            await Reply(command, Theme.Denied("Above your authority",
                $"That ban was issued by **{StaffHierarchy.Name(existing.Tier ?? StaffTier.None)}**. " +
                $"You are **{StaffHierarchy.Name(actor)}**.")).ConfigureAwait(false);
            return;
        }

        /* THE WHOLE LIFT, through the one path that does all of it. This used to remove the
           record, clear the flags and send the Unban here, and grant no exemption - while the
           expiry sweep granted an exemption and never cleared the flags. Two half-lifts with
           different halves missing. */
        var result = await Bans.LiftAsync(player, existing.UniqueId, ct).ConfigureAwait(false);

        await Audit.RecordAsync("unban", command.User.Username, player, existing.Reason, ct).ConfigureAwait(false);

        var embed = Theme.Success("Exile lifted", $"**{Sanitize.Code(player)}** may walk back in.")
            .AddField("Was", $"{Sanitize.Code(Sanitize.RedactPrivate(existing.Reason ?? "no reason recorded"))} — by {existing.Moderator ?? "unknown"}")
            .AddField("Lifted by", command.User.Username, true);

        if (!result.Landed)
        {
            embed.AddField($"{Theme.Warn} Not lifted on the server",
                "The record was removed but no server accepted the Unban. They may still be natively banned.");
        }

        await PardonIfMasterAsync(command, player, embed, ct).ConfigureAwait(false);
        await Reply(command, embed.Brand()).ConfigureAwait(false);
    }

    /// <summary>
    /// A master owner's unban becomes a pardon: nobody below them, and no automated path, can
    /// ban the player again until a master owner does.
    /// </summary>
    private async Task PardonIfMasterAsync(SocketSlashCommand command, string player, EmbedBuilder embed, CancellationToken ct)
    {
        if (Access.TierOf(command.User) != StaffTier.MasterOwner) return;

        if (await Masters.PardonAsync(player, ct).ConfigureAwait(false))
        {
            Logger.LogWarning("MASTER PARDON | player=\"{Player}\" | by={By}", player, command.User.Username);
            embed.AddField("Master Owner pardon",
                "Nobody below a Master Owner can ban them again, and automatic bans will not touch them.");
        }
        else
        {
            Logger.LogError("Master pardon for \"{Player}\" could not be saved - they are unbanned but NOT protected", player);
            embed.AddField($"{Theme.Warn} Pardon not saved",
                "They are unbanned, but the pardon could not be written, so staff can still ban them.");
        }
    }
}

/// <summary><c>/checkban</c> - what, if anything, is on a player.</summary>
public sealed class CheckBanCommand(
    BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit,
    ServerBanFile banFile, ILogger<CheckBanCommand> logger, MasterNames masters)
    : BanCommandBase(bans, tracking, store, access, audit, logger, masters)
{
    public override string Name => "checkban";

    public override ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Check whether a player is banned")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player ID or username", isRequired: true, isAutocomplete: true)
            .Build();

    public override async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var player = Sanitize.Id(Option(command, "playerid") ?? "");
        var now = DateTimeOffset.UtcNow;
        var record = Bans.LoadBans().FirstOrDefault(b => BanRules.SamePlayer(b.PlayerId, player));

        if (record is null)
        {
            /* The file is read rather than described. "Not banned by this bot" was answering a
               narrower question than the one being asked, every time. */
            var listed = await banFile.FindAsync(player, ct).ConfigureAwait(false);
            var note = BanFileReport.Describe(listed);
            var body = $"**{Sanitize.Code(player)}** is not banned{(note is null ? "." : $".\n\n{note}")}";

            await Reply(command, WithHistory(listed.Listed
                ? Theme.Punishment($"{Theme.Deny} Exiled by the server", body)
                    .AddField("Lift it", "`/unban` removes them from that file.")
                    .Brand()
                : Theme.Success("No ban on record", body), player)).ConfigureAwait(false);
            return;
        }

        if (!record.IsActiveAt(now))
        {
            // A record whose time has passed but which the sweep has not cleared yet. Say
            // so plainly rather than reporting them as banned.
            await Reply(command, WithHistory(Theme.Notice("Sentence served",
                $"**{Sanitize.Code(player)}** served their ban; it expired {Theme.Relative(record.Expires!.Value)}."), player))
                .ConfigureAwait(false);
            return;
        }

        var embed = Theme.Punishment($"{Theme.Deny} Exiled", $"**{Sanitize.Code(player)}**")
            // Redacted: an auto-ban reason embeds the address or account id that triggered it
            // ("blacklisted ip 1.2.3.4"), and /checkban is not an address-viewing command.
            .AddField("Reason", Sanitize.Code(Sanitize.RedactPrivate(record.Reason ?? "none recorded")))
            .AddField("By", record.Moderator ?? "unknown", true)
            .AddField("Since", record.At is { } at ? Theme.Relative(at) : "unknown", true)
            .AddField("Expires", record.Permanent ? "Never" : Theme.Relative(record.Expires!.Value), true);

        /* WHAT THE MODERATOR FIELD MEANS. "in-game" and "auto" are opaque, and the difference
           between them is the difference between a ban this bot decided on and one it merely
           read out of the game's own file - which is exactly the question somebody is asking
           when a ban has no explanation they recognise. A reason can also OUTLIVE its cause:
           the export writes it into banlist.txt and the import reads it back, so a verdict
           from months ago goes on being quoted long after whatever produced it was cleared. */
        embed.AddField("Where this came from", record.Moderator switch
        {
            "in-game" =>
                "The game's own ban file, not this bot - so the reason above may be old. `/unban` " +
                "takes them out of the file as well as the store.",
            "auto" =>
                "This bot, automatically - VPN screening or ban evasion. The reason above says " +
                "which. Nobody typed it.",
            _ => "A moderator, using the ban commands.",
        }, inline: false);

        /* The ORIGINAL offence, when this record is an auto-ban. Otherwise /checkban answers
           "why are they banned" with "because they were banned", which helps nobody
           handling an appeal. */
        if (!BanRules.IsRealBan(record))
        {
            var account = Tracking.AccountByName(player);
            var source = BanRules.SourceBanFor(Bans.LoadBans(), player, account?.Id,
                n => Tracking.AccountByName(n)?.Id, account?.Names);

            embed.AddField("Original offence", source is not null
                ? $"{Sanitize.Code(Sanitize.RedactPrivate(source.Reason!))} — by {source.Moderator}"
                : "Ban evasion (no earlier record found)");
        }

        await Reply(command, WithHistory(embed, player).Brand()).ConfigureAwait(false);
    }

    /// <summary>Most past bans shown. Older ones are counted, not listed.</summary>
    internal const int HistoryShown = 8;

    /// <summary>The longest a single reason may run before it is cut, so the field fits Discord's 1024.</summary>
    private const int HistoryReasonLength = 70;

    /// <summary>The bans staff issued, and how each reads. These are what the history lists.</summary>
    private static readonly IReadOnlyDictionary<string, string> HistoryLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["permban"] = "Permanent ban",
            ["tempban"] = "Temp ban",
            ["warn-ban"] = "Ban (warning limit)",
        };

    /// <summary>
    /// Automated bans, from both bots. Never listed - one evader can log dozens - but they still
    /// take part in the matching, so the /unban that lifted one is not charged to a staff ban.
    /// </summary>
    private static readonly HashSet<string> AutomatedBans =
        new(["autoban", "auto-ipban", "vpnban", "auto-vpnban"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A staff member lifting a ban early.</summary>
    private const string ManualUnban = "unban";

    /// <summary>A ban ending on its own, as the bots logged it.</summary>
    private static readonly HashSet<string> Releases =
        new(["autoban-released", "auto-unban"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The staff bans that count, oldest first: ones still in force or that ran their course. A
    /// ban a staff member lifted early with /unban does not count.
    /// </summary>
    /// <remarks>
    /// Walked in log order. Each /unban lifts the most recent ban still standing at that moment -
    /// which may be an automated one, and then no staff ban is affected. A release (a ban that
    /// ran out) ends the most recent standing ban without discounting it.
    /// </remarks>
    private static List<ModAction> CountedBans(IReadOnlyList<ModAction> actions)
    {
        var standing = new List<ModAction>();   // bans not yet lifted or released, oldest first
        var counted = new List<ModAction>();
        var lifted = new HashSet<ModAction>(ReferenceEqualityComparer.Instance);

        foreach (var action in actions)         // the audit log is chronological
        {
            if (HistoryLabels.ContainsKey(action.Action) || AutomatedBans.Contains(action.Action))
            {
                standing.Add(action);
            }
            else if (string.Equals(action.Action, ManualUnban, StringComparison.OrdinalIgnoreCase) ||
                     Releases.Contains(action.Action))
            {
                if (standing.Count == 0) continue;
                var ended = standing[^1];
                standing.RemoveAt(standing.Count - 1);
                if (!Releases.Contains(action.Action)) lifted.Add(ended);
            }
        }

        foreach (var action in actions)
        {
            if (HistoryLabels.ContainsKey(action.Action) && !lifted.Contains(action)) counted.Add(action);
        }
        return counted;
    }

    /// <summary>Add the player's ban history - under any name their account has used - to a reply.</summary>
    private EmbedBuilder WithHistory(EmbedBuilder embed, string player)
    {
        var names = (Tracking.AccountByName(player)?.Names ?? []).Append(player).ToList();
        var history = BanHistory(Audit.All(), names, player);
        return history is null ? embed : embed.AddField("Ban history", history);
    }

    /// <summary>
    /// Every past ban and unban against any of these names, newest first, as one field. Null when
    /// there are none.
    /// </summary>
    /// <remarks>
    /// FROM THE AUDIT LOG, not the ban store: a ban that was lifted or served is gone from the
    /// store, and that is exactly the part of somebody's record an appeal or a repeat offence
    /// needs. Reasons are redacted the same way the current ban's is - an auto-ban reason carries
    /// the address that caught them.
    /// </remarks>
    internal static string? BanHistory(IEnumerable<ModAction> log, IReadOnlyCollection<string> names, string asked)
    {
        var entries = CountedBans([.. log.Where(a => names.Contains(a.Player, StringComparer.OrdinalIgnoreCase))])
            .AsEnumerable().Reverse()                              // newest first; the list is chronological so ties keep their order
            .OrderByDescending(a => a.At)
            .ToList();
        if (entries.Count == 0) return null;

        var lines = entries.Take(HistoryShown).Select(a =>
        {
            var reason = Sanitize.RedactPrivate(a.Reason ?? "");
            if (reason.Length > HistoryReasonLength) reason = reason[..HistoryReasonLength] + "…";
            var alias = string.Equals(a.Player, asked, StringComparison.OrdinalIgnoreCase) ? "" : $" as **{Sanitize.Code(a.Player)}**";
            var why = reason.Length > 0 ? $" - {Sanitize.Code(reason)}" : "";
            return $"{Theme.Dot} **{HistoryLabels[a.Action]}**{alias} by {Sanitize.Code(a.Moderator)}{why} {Theme.Relative(a.At)}";
        }).ToList();

        var header = $"{entries.Count} ban(s) on record.";

        /* FITTED, not estimated: a long moderator name or reason must not push the field past
           Discord's limit, which fails the whole reply rather than trimming it. Oldest go first. */
        string Render() =>
            $"{header}\n{string.Join("\n", lines)}" +
            (entries.Count > lines.Count ? $"\n…and {entries.Count - lines.Count} older." : "");

        var text = Render();
        while (text.Length > EmbedFieldBuilder.MaxFieldValueLength && lines.Count > 1)
        {
            lines.RemoveAt(lines.Count - 1);
            text = Render();
        }
        return text.Length <= EmbedFieldBuilder.MaxFieldValueLength ? text : text[..(EmbedFieldBuilder.MaxFieldValueLength - 1)] + "…";
    }
}
