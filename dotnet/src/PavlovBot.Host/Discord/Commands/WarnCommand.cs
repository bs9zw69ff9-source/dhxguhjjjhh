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

/// <summary>
/// <c>/warn</c> - warnings, and the ban that follows enough of them.
/// </summary>
/// <remarks>
/// THE ESCALATION IS APPLIED HERE, NOT IN <see cref="WarningService"/>. That class decides
/// what a warning count deserves; enforcing it needs RCON, the ban list and the game's own
/// banlist, and burying that behind a storage class would make the decision untestable
/// without a live server.
///
/// RECORD FIRST, THEN ENFORCE - the same order <c>/tempban</c> uses, for the same reason. If
/// the RCON call fails the record still exists, so the ban sweep retries it. The other way
/// round leaves somebody kicked with nothing saying why and nothing to lift.
/// </remarks>
public sealed class WarnCommand(
    WarningService warnings,
    BanService bans,
    IpTrackingService tracking,
    SerializedStore store,
    Access access,
    AuditLog audit,
    ILogger<WarnCommand> logger) : ISlashCommand
{
    public string Name => "warn";

    public ApplicationCommandProperties Build()
    {
        static SlashCommandOptionBuilder Player(bool required) =>
            new SlashCommandOptionBuilder()
                .WithName("playerid").WithDescription("Player ID or username")
                .WithType(ApplicationCommandOptionType.String).WithRequired(required).WithAutocomplete(true);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Moderation - Warn a player, with automatic escalation")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("give").WithDescription("Warn a player")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player(true))
                .AddOption("reason", ApplicationCommandOptionType.String, "Why", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list").WithDescription("A player's warning record")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player(true)))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove").WithDescription("Remove one warning by its id")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player(true))
                .AddOption("id", ApplicationCommandOptionType.String, "The warning id from /warn list", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("clear").WithDescription("Clear a player's whole record")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(Player(true)))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("standings").WithDescription("Who currently has active warnings")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sub = command.Data.Options.First();

        /* Clearing a record needs ADMIN, everything else needs MOD. A mod who can wipe the
           evidence of their own escalation is a mod who can un-ban by the back door - the
           ban lifts on the next sweep once the warnings that justified it are gone. */
        var required = sub.Name == "clear" ? RequiredAccess.Admin : RequiredAccess.Mod;
        if (!access.Allows(required, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(required, command))).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(sub.Options.FirstOrDefault(o => o.Name == "playerid")?.Value as string ?? "");

        var embed = sub.Name switch
        {
            "give" => await GiveAsync(command, player,
                sub.Options.FirstOrDefault(o => o.Name == "reason")?.Value as string ?? "", ct).ConfigureAwait(false),
            "list" => List(player),
            "remove" => await RemoveAsync(player,
                (sub.Options.FirstOrDefault(o => o.Name == "id")?.Value as string ?? "").Trim(), ct).ConfigureAwait(false),
            "clear" => await ClearAsync(command, player, ct).ConfigureAwait(false),
            "standings" => Standings(),
            _ => Theme.Failure("Unknown subcommand"),
        };

        await Reply(command, embed).ConfigureAwait(false);
    }

    private async Task<EmbedBuilder> GiveAsync(
        SocketSlashCommand command, string player, string reason, CancellationToken ct)
    {
        if (player.Length == 0)
            return Theme.Failure("That name has nothing usable in it",
                "After sanitising there was no identifier left to warn.");

        var outcome = await warnings.WarnAsync(player, reason, command.User.Username, ct).ConfigureAwait(false);

        var embed = Theme.Warning($"{Theme.Warn} Warned",
                $"**{Sanitize.Code(player)}** — {Sanitize.Code(outcome.Warning.Reason)}")
            .AddField("Active warnings", $"{outcome.Active} (in the last {(int)WarningService.DecayWindow.TotalDays} days)", true)
            .AddField("On record", outcome.Total.ToString(System.Globalization.CultureInfo.InvariantCulture), true)
            .AddField("Warning id", $"`{outcome.Warning.Id}`", true);

        if (!outcome.Escalated)
        {
            // Telling the moderator how close this player is turns a warning into a
            // decision rather than a note nobody revisits.
            if (WarningService.WarningsUntilBan(outcome.Active) is { } remaining)
            {
                embed.AddField("Next step",
                    $"{(int)WarningService.EscalationBan.TotalHours}h ban at " +
                    $"{WarningService.EscalationThreshold} active warnings ({remaining} more).");
            }
            return embed;
        }

        var applied = await EscalateAsync(command, player, outcome, ct).ConfigureAwait(false);
        embed.AddField($"{Theme.Deny} Escalated", applied);
        return embed;
    }

    /// <summary>Ban the player for a day, because they hit the threshold.</summary>
    private async Task<string> EscalateAsync(
        SocketSlashCommand command, string player, WarningOutcome outcome, CancellationToken ct)
    {
        var span = WarningService.EscalationBan;
        var account = tracking.AccountByName(player);
        var reason = $"Automatic: {outcome.Active} warnings in {(int)WarningService.DecayWindow.TotalDays} days";

        var now = DateTimeOffset.UtcNow;
        var record = new BanRecord
        {
            PlayerId = player,
            Reason = reason,
            Moderator = $"{command.User.Username} (auto)",
            At = now,
            Expires = now + span,
            Permanent = false,
            DurationLabel = EasternTime.TimeLeft(now + span, TimeProvider.System),
            Tier = access.TierOf(command.User),
        };

        // Record first, then enforce. See the class remarks.
        await store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], all =>
        {
            // Replace rather than append: two rows for one player means an unban lifts one
            // and the other quietly re-catches them on the next sweep.
            all.RemoveAll(b => BanRules.SamePlayer(b.PlayerId, player));
            all.Add(record);
            return all;
        }, ct).ConfigureAwait(false);

        var enforcement = await bans.HardEnforceAsync(player, account?.Id, ct: ct).ConfigureAwait(false);

        /* NO ACCOUNT-ID FLAG. Flags have no expiry, so branding one on a temporary
           escalation keeps catching them long after they have served it - and this ban was
           issued by a counter, not by a person looking at what they did. */
        await audit.RecordAsync("warn-ban", command.User.Username, player,
            $"{reason} - {(int)span.TotalHours}h ban", ct).ConfigureAwait(false);

        logger.LogWarning(
            "Warning escalation BANNED {Player} for {Hours}h at {Active} warnings (rcon {Servers} server(s))",
            player, (int)span.TotalHours, outcome.Active, enforcement.Servers);

        return enforcement.Landed
            ? $"{(int)span.TotalHours}h ban at {WarningService.EscalationThreshold} warnings — " +
              $"expires {Theme.Relative(now + span)}."
            : $"{(int)span.TotalHours}h ban recorded at {WarningService.EscalationThreshold} warnings, " +
              "but NO server accepted it. The sweep will retry.";
    }

    private EmbedBuilder List(string player)
    {
        var all = warnings.For(player);
        if (all.Count == 0)
            return Theme.Success($"{Theme.Ok} Clean record", $"**{Sanitize.Code(player)}** has no warnings.");

        var active = warnings.ActiveFor(player);
        var activeIds = active.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);

        // Newest first: the recent ones are the ones a decision is being made about.
        var lines = all.OrderByDescending(w => w.At).Select(w =>
            $"{(activeIds.Contains(w.Id) ? Theme.Warn : Theme.Info)} `{w.Id}` — {Sanitize.Code(w.Reason)}\n" +
            $"　by {Sanitize.Code(w.Moderator)}, {Theme.Relative(w.At)}");

        var embed = Theme.Notice($"{Theme.Warn} Warnings for {Sanitize.Code(player)}",
                string.Join("\n", Theme.Paginate(lines).FirstOrDefault() ?? ""))
            .AddField("Active", $"{active.Count} (counts toward escalation)", true)
            .AddField("On record", all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), true);

        if (WarningService.WarningsUntilBan(active.Count) is { } remaining)
            embed.AddField("Next step", $"{(int)WarningService.EscalationBan.TotalHours}h ban in {remaining}", true);

        return embed;
    }

    private async Task<EmbedBuilder> RemoveAsync(string player, string id, CancellationToken ct)
    {
        if (id.Length == 0) return Theme.Failure("No warning id given", "Run `/warn list` to see the ids.");

        return await warnings.RemoveAsync(player, id, ct).ConfigureAwait(false)
            ? Theme.Success($"{Theme.Ok} Warning removed", $"`{id}` is off **{Sanitize.Code(player)}**'s record.")
            : Theme.Failure("No such warning",
                $"**{Sanitize.Code(player)}** has no warning with id `{Sanitize.Code(id)}`.");
    }

    private async Task<EmbedBuilder> ClearAsync(SocketSlashCommand command, string player, CancellationToken ct)
    {
        var cleared = await warnings.ClearAsync(player, command.User.Username, ct).ConfigureAwait(false);

        return cleared == 0
            ? Theme.Notice("Nothing to clear", $"**{Sanitize.Code(player)}** had no warnings.")
            : Theme.Success($"{Theme.Ok} Record cleared",
                $"Removed {cleared} warning(s) from **{Sanitize.Code(player)}**.\n" +
                "An existing escalation ban is NOT lifted by this - use `/unban` for that.");
    }

    private EmbedBuilder Standings()
    {
        var rows = warnings.Standings();
        if (rows.Count == 0)
            return Theme.Success($"{Theme.Ok} Nobody is warned", "No player has an active warning.");

        var lines = rows.Take(20).Select((r, i) =>
            $"`{i + 1,2}.` **{Sanitize.Code(r.Player)}** — {r.Active} active, {r.Total} on record");

        return Theme.Warning($"{Theme.Warn} Active warnings", string.Join("\n", lines))
            .Brand($"Decays after {(int)WarningService.DecayWindow.TotalDays} days");
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m => m.Embed = embed.Brand().Build());
}
