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
        var tier = Access.TierOf(command.User as IGuildUser);
        var now = DateTimeOffset.UtcNow;
        var permanent = duration is null;

        var record = new BanRecord
        {
            PlayerId = name,
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
            permanent ? $"{Theme.Deny} Permanently banned" : $"{Theme.Deny} Banned",
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
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Mod))).ConfigureAwait(false);
            return;
        }

        var player = Option(command, "playerid") ?? "";
        var reason = Option(command, "reason") ?? "No reason given";
        var choice = Option(command, "duration") ?? "";

        var duration = BanDurations.Length(choice);
        if (duration is null && !string.Equals(choice, BanDurations.Permanent, StringComparison.OrdinalIgnoreCase))
        {
            await Reply(command, Theme.Failure("That duration makes no sense",
                "Use a length like `1d`, `3d 4h` or `1mo`. Calendar dates are not accepted - " +
                "a ban is a length of time, not a deadline.")).ConfigureAwait(false);
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
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Admin))).ConfigureAwait(false);
            return;
        }

        var embed = await IssueBanAsync(command, Option(command, "playerid") ?? "",
            Option(command, "reason") ?? "No reason given", duration: null, ct).ConfigureAwait(false);
        await Reply(command, embed).ConfigureAwait(false);
    }
}

/// <summary><c>/unban</c> - lift a ban, subject to the staff hierarchy.</summary>
public sealed class UnbanCommand(
    BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit, ILogger<UnbanCommand> logger)
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
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(RequiredAccess.Mod))).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(Option(command, "playerid") ?? "");
        var existing = Bans.LoadBans().FirstOrDefault(b => BanRules.SamePlayer(b.PlayerId, player));

        if (existing is null)
        {
            await Reply(command, Theme.Notice("No ban to lift", $"**{Sanitize.Code(player)}** is not banned.")).ConfigureAwait(false);
            return;
        }

        /* The hierarchy check. Moderation authority is the one thing a moderator can attack
           with permissions they legitimately hold - without this, any mod can quietly unban
           whoever the owner banned and the audit log records it as routine. */
        var actor = Access.TierOf(command.User as IGuildUser);
        if (!StaffHierarchy.CanOverride(actor, existing.Tier))
        {
            await Reply(command, Theme.Denied("Above your authority",
                $"That ban was issued by **{StaffHierarchy.Name(existing.Tier ?? StaffTier.None)}**. " +
                $"You are **{StaffHierarchy.Name(actor)}**.")).ConfigureAwait(false);
            return;
        }

        await Store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            bans.RemoveAll(b => BanRules.SamePlayer(b.PlayerId, player));
            return bans;
        }, ct).ConfigureAwait(false);

        var account = Tracking.AccountByName(player);
        if (account is not null) await Tracking.ClearFlagsAsync(account.Id, ct).ConfigureAwait(false);

        var result = await Bans.UnbanEverywhereAsync(player, account?.Id, ct).ConfigureAwait(false);
        await Audit.RecordAsync("unban", command.User.Username, player, existing.Reason, ct).ConfigureAwait(false);

        var embed = Theme.Success("Ban lifted", $"**{Sanitize.Code(player)}** may reconnect.")
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
    BanService bans, IpTrackingService tracking, SerializedStore store, Access access, AuditLog audit, ILogger<CheckBanCommand> logger)
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
            await Reply(command, Theme.Success("No ban on record", $"**{Sanitize.Code(player)}** is not banned.")).ConfigureAwait(false);
            return;
        }

        if (!record.IsActiveAt(now))
        {
            // A record whose time has passed but which the sweep has not cleared yet. Say
            // so plainly rather than reporting them as banned.
            await Reply(command, Theme.Notice("Ban served",
                $"**{Sanitize.Code(player)}** served their ban; it expired {Theme.Relative(record.Expires!.Value)}.")).ConfigureAwait(false);
            return;
        }

        var embed = Theme.Punishment($"{Theme.Deny} Banned", $"**{Sanitize.Code(player)}**")
            .AddField("Reason", Sanitize.Code(record.Reason ?? "none recorded"))
            .AddField("By", record.Moderator ?? "unknown", true)
            .AddField("Since", record.At is { } at ? Theme.Relative(at) : "unknown", true)
            .AddField("Expires", record.Permanent ? "Never" : Theme.Relative(record.Expires!.Value), true);

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
