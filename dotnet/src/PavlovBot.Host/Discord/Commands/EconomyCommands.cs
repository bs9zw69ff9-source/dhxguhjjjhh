using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Economy;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/givecaps</c> and <c>/adjustcaps</c> - move money.
/// </summary>
/// <remarks>
/// Two commands rather than one with a sign, because they are different acts. A GIVE is a
/// payout and can only ever add; an ADJUST is a correction and may go either way. Folding
/// them together means a mistyped minus on a payout silently takes money instead, and the
/// audit line cannot tell the two apart afterwards.
///
/// Both go through <see cref="Ledger"/>, so concurrent payouts cannot double-spend.
/// </remarks>
public sealed class CapsCommand : ISlashCommand
{
    private readonly Ledger _ledger;
    private readonly AuditLog _audit;
    private readonly Access _access;
    private readonly ILogger _logger;
    private readonly bool _allowNegative;

    private CapsCommand(Ledger ledger, AuditLog audit, Access access, ILogger logger, string name, bool allowNegative)
    {
        _ledger = ledger;
        _audit = audit;
        _access = access;
        _logger = logger;
        Name = name;
        _allowNegative = allowNegative;
    }

    public static CapsCommand Give(Ledger l, AuditLog a, Access ac, ILogger<CapsCommand> lg) =>
        new(l, a, ac, lg, "givecaps", allowNegative: false);

    public static CapsCommand Adjust(Ledger l, AuditLog a, Access ac, ILogger<CapsCommand> lg) =>
        new(l, a, ac, lg, "adjustcaps", allowNegative: true);

    public string Name { get; }

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription(_allowNegative
                ? "Admin - Correct a player's balance up or down"
                : "Mod - Give money to a player")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player ID or username", isRequired: true, isAutocomplete: true)
            .AddOption("amount", ApplicationCommandOptionType.Integer,
                _allowNegative ? "How much to add, or a negative number to take away" : "How much to give",
                isRequired: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var required = _allowNegative ? RequiredAccess.Admin : RequiredAccess.Mod;
        if (!_access.Allows(required, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(required))).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(command.Data.Options.First(o => o.Name == "playerid").Value as string ?? "");
        var amount = command.Data.Options.First(o => o.Name == "amount").Value as long? ?? 0;

        if (player.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        if (amount == 0)
        {
            await Reply(command, Theme.Failure("Nothing to move", "An amount of zero does nothing.")).ConfigureAwait(false);
            return;
        }

        if (!_allowNegative && amount < 0)
        {
            /* Refused rather than silently taking the absolute value. A moderator who typed
               a minus meant something, and guessing which thing is worse than asking. */
            await Reply(command, Theme.Failure("That is a deduction, not a payout",
                "`/givecaps` only ever adds. Use `/adjustcaps` to correct a balance downwards.")).ConfigureAwait(false);
            return;
        }

        var change = await _ledger.CreditAsync(player, amount, ct).ConfigureAwait(false);

        if (!change.Ok)
        {
            /* Reporting the intended balance after a failed write is how a bot tells a
               player they were paid when they were not. */
            await Reply(command, Theme.Failure("Not applied",
                "The ledger write failed - their balance is unchanged. Check MODSAVE_PATH and permissions.")).ConfigureAwait(false);
            return;
        }

        await _audit.RecordAsync(Name, command.User.Username, player,
            $"{(amount > 0 ? "+" : "")}{amount}", ct).ConfigureAwait(false);

        _logger.LogInformation("{Command} | player=\"{Player}\" | {Before} -> {After} | by={By}",
            Name, player, change.Before, change.After, command.User.Username);

        await Reply(command, Theme.Success($"{Theme.Money} Balance updated",
            $"**{Sanitize.Code(player)}** — {(amount > 0 ? "+" : "")}{Money(amount)}")
            .AddField("Was", Money(change.Before), true)
            .AddField("Now", Money(change.After), true)
            .AddField("By", command.User.Username, true)).ConfigureAwait(false);
    }

    private static string Money(long value) => $"${value.ToString("N0", CultureInfo.GetCultureInfo("en-US"))}";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
