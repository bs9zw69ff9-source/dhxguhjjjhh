using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Economy;
using PavlovBot.Core.Text;
using PavlovBot.Host.Economy;
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

    /// <summary>
    /// The file store, purely so a refusal can SAY WHY. Nothing is written through it.
    /// </summary>
    /// <remarks>
    /// Optional: <see cref="IBalanceStore"/> is the contract, and only the file-backed one
    /// can explain itself. A test using an in-memory store gets the generic message.
    /// </remarks>
    private readonly LedgerFileStore? _balances;

    private CapsCommand(
        Ledger ledger, AuditLog audit, Access access, ILogger logger, string name, bool allowNegative,
        IBalanceStore? balances = null)
    {
        _ledger = ledger;
        _audit = audit;
        _access = access;
        _logger = logger;
        Name = name;
        _allowNegative = allowNegative;
        _balances = balances as LedgerFileStore;
    }

    public static CapsCommand Give(Ledger l, AuditLog a, Access ac, ILogger<CapsCommand> lg, IBalanceStore? b = null) =>
        new(l, a, ac, lg, "givecaps", allowNegative: false, balances: b);

    public static CapsCommand Adjust(Ledger l, AuditLog a, Access ac, ILogger<CapsCommand> lg, IBalanceStore? b = null) =>
        new(l, a, ac, lg, "adjustcaps", allowNegative: true, balances: b);

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
               player they were paid when they were not.

               The REASON matters here, because the most common one is temporary and the
               moderator only needs to wait: the game holds a connected player's balance in
               memory and rewrites the file from it, so anything written while they are in
               game is overwritten on their next save. */
            var why = _balances?.Refusal(player)
                ?? "their ledger did not accept the write";

            await Reply(command, Theme.Failure("Not applied",
                $"**{Sanitize.Code(player)}**'s balance is unchanged — {why}.")).ConfigureAwait(false);
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

/// <summary>
/// <c>/wages</c> - what an officer has earned on duty, banked and unbanked.
/// </summary>
/// <remarks>
/// EXISTS BECAUSE THE PAY RULE IS NOT SELF-EVIDENT. Wages are paid in WHOLE periods of
/// observed on-duty time, with the remainder carried: forty-two minutes at 500 per thirty
/// pays 500 now and the second 500 eighteen minutes later. From the inside that is
/// indistinguishable from being underpaid, and the only answer staff could give was "trust
/// it". This shows the carry, so the question answers itself.
///
/// The state was already there - Owed, Earned and the run history - with nothing reading it.
/// Numbers a bot keeps and never shows are numbers nobody can check.
/// </remarks>
public sealed class WagesCommand(Payroll payroll) : ISlashCommand
{
    public string Name => "wages";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("What an on-duty officer has earned, banked and unbanked")
            .AddOption("player", ApplicationCommandOptionType.String, "Their in-game name",
                isRequired: true, isAutocomplete: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!payroll.Enabled)
        {
            await Reply(command, Theme.Warning("Payroll is off",
                "`PAYROLL_AMOUNT` is unset or the faction rosters are unreachable, so nothing " +
                "is being earned.")).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(command.Data.Options.FirstOrDefault()?.Value?.ToString() ?? "");
        if (player.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        var owed = payroll.OwedTo(player);
        var carried = payroll.EarnedTowardNextPeriod(player);
        var lifetime = payroll.TotalPaidTo(player);

        var embed = Theme.Notice($"{Theme.Money} Wages — {Sanitize.Code(player)}")
            .AddField("Earned, not yet banked", Money(owed), true)
            .AddField("Banked to date", Money(lifetime), true)
            .AddField("Toward the next payment",
                $"{carried.TotalMinutes:0} of {payroll.Period.TotalMinutes:0} minutes", true);

        /* SAYING WHY, not just how much. "Unbanked" reads like money that has gone missing
           unless it comes with the reason it is being held. */
        embed.AddField("How this works",
            $"On duty pays **{Money(payroll.Amount)}** per **{payroll.Period.TotalMinutes:0}** minutes " +
            "of observed time. Part periods are not rounded up or thrown away — they carry " +
            "forward and pay out once they complete.\n\n" +
            "Earned pay is banked to the in-game balance on the first check **after they log " +
            "off**. It cannot be written while they are connected: the server holds that " +
            "balance in memory and overwrites the file from it.");

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static string Money(long value) => $"${value.ToString("N0", CultureInfo.GetCultureInfo("en-US"))}";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
