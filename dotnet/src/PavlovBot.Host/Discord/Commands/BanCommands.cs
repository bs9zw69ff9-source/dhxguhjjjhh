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
    /// <summary>The line to add under "not banned by this bot".</summary>
    public static string Describe(BanFileLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var file = lookup.Path is { Length: > 0 } p ? $"`{Sanitize.Code(p)}`" : "the server's ban file";

        return lookup.Status switch
        {
            BanFileStatus.Read when lookup.Entry is { } entry =>
                $"{Theme.Deny} **The server's own ban file lists them** ({file}). Pavlov reads it " +
                $"directly, so they are refused in game whatever this bot says.\n" +
                $"In the file: {Sanitize.Code(entry.Reason)} — {Sanitize.Code(entry.Unban)}",

            BanFileStatus.Read =>
                $"{Theme.Ok} Not in the server's own ban file either ({file}).",

            /* NOT "they are not banned". The file is the half of the answer that could not be
               read, and saying nothing about that is how a wrong path stays invisible for
               weeks while players insist they are still locked out. */
            BanFileStatus.Missing =>
                $"{Theme.Warn} {file} **does not exist**, so this cannot say what the server is " +
                "enforcing. Point `BLACKLIST_PATH` at the real one.",

            BanFileStatus.Unreadable =>
                $"{Theme.Warn} {file} **could not be read** - check its permissions. If they are " +
                "still refused in game, that file is why.",

            _ =>
                $"{Theme.Warn} No ban file is configured, so this cannot see what the server " +
                "enforces. Set `BLACKLIST_PATH`.",
        };
    }
}

public abstract class BanCommandBase : ISlashCommand
{
    protected BanCommandBase(
        BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit, ILogger logger)
    {
        Bans = bans;
        Tracking = tracking;
        Store = store;
        Access = access;
        Audit = audit;
        Logger = logger;
    }

    protected BanService Bans { get; }
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

        await Store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            // Replace rather than append: two records for one player means an unban lifts
            // one of them and the other quietly re-catches them on the next sweep.
            bans.RemoveAll(b => BanRules.SamePlayer(b.PlayerId, name));
            bans.Add(record);
            return bans;
        }, ct).ConfigureAwait(false);

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
            command.User.Username, tier, enforcement.Servers, Bans.LoadBans().Count,
            flags?.Ips.Count ?? 0, flags?.Ids.Count ?? 0,
            flags?.Pending == true ? " (pending confirmation)" : "", EasternTime.Stamp(now));

        var embed = Theme.Punishment(
            permanent ? $"{Theme.Deny} Exiled from {Lore.World}" : $"{Theme.Deny} Run out of {Lore.World}",
            $"**{Sanitize.Code(name)}** — {Sanitize.Code(record.Reason ?? "no reason given")}")
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
        else if (enforcement.Servers < Bans.LoadBans().Count && enforcement.Servers > 0)
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
    BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit, ILogger<TempBanCommand> logger)
    : BanCommandBase(bans, tracking, store, access, audit, logger)
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
    BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit, ILogger<PermBanCommand> logger)
    : BanCommandBase(bans, tracking, store, access, audit, logger)
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
    ServerBanFile banFile, ILogger<UnbanCommand> logger)
    : BanCommandBase(bans, tracking, store, access, audit, logger)
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
                await Reply(command, Theme.Notice("No ban to lift",
                    $"**{Sanitize.Code(player)}** is not banned by this bot.\n\n{BanFileReport.Describe(listed)}"))
                    .ConfigureAwait(false);
                return;
            }

            await Bans.LiftAsync(player, Tracking.AccountByName(player)?.Id, ct).ConfigureAwait(false);
            await Audit.RecordAsync("unban", command.User.Username, player,
                $"Removed from the server's ban file - {listed.Entry!.Reason}", ct).ConfigureAwait(false);

            await Reply(command, Theme.Success("Removed from the server's ban file",
                    $"**{Sanitize.Code(player)}** had no record with this bot. The server's own ban " +
                    $"file listed them, and that entry is now gone.")
                .AddField("Was", $"{Sanitize.Code(listed.Entry.Reason)} — {Sanitize.Code(listed.Entry.Unban)}")
                .AddField("File", $"`{Sanitize.Code(listed.Path ?? "unknown")}`")
                .AddField("Lifted by", command.User.Username, true)
                .Brand()).ConfigureAwait(false);
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
            .AddField("Was", $"{Sanitize.Code(existing.Reason ?? "no reason recorded")} — by {existing.Moderator ?? "unknown"}")
            .AddField("Lifted by", command.User.Username, true);

        if (!result.Landed)
        {
            embed.AddField($"{Theme.Warn} Not lifted on the server",
                "The record was removed but no server accepted the Unban. They may still be natively banned.");
        }

        await Reply(command, embed.Brand()).ConfigureAwait(false);
    }
}

/// <summary><c>/checkban</c> - what, if anything, is on a player.</summary>
public sealed class CheckBanCommand(
    BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit,
    ServerBanFile banFile, ILogger<CheckBanCommand> logger)
    : BanCommandBase(bans, tracking, store, access, audit, logger)
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
            var body = $"**{Sanitize.Code(player)}** is not banned by this bot.\n\n{BanFileReport.Describe(listed)}";

            await Reply(command, listed.Listed
                ? Theme.Punishment($"{Theme.Deny} Exiled by the server", body)
                    .AddField("Lift it", "`/unban` removes them from that file.")
                    .Brand()
                : Theme.Success("No ban on record", body)).ConfigureAwait(false);
            return;
        }

        if (!record.IsActiveAt(now))
        {
            // A record whose time has passed but which the sweep has not cleared yet. Say
            // so plainly rather than reporting them as banned.
            await Reply(command, Theme.Notice("Sentence served",
                $"**{Sanitize.Code(player)}** served their ban; it expired {Theme.Relative(record.Expires!.Value)}.")).ConfigureAwait(false);
            return;
        }

        var embed = Theme.Punishment($"{Theme.Deny} Exiled", $"**{Sanitize.Code(player)}**")
            .AddField("Reason", Sanitize.Code(record.Reason ?? "none recorded"))
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
                ? $"{Sanitize.Code(source.Reason!)} — by {source.Moderator}"
                : "Ban evasion (no earlier record found)");
        }

        await Reply(command, embed.Brand()).ConfigureAwait(false);
    }
}
