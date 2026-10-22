using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Economy;
using PavlovBot.Core.Licensing;
using PavlovBot.Core.Text;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Moderation;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/license remove</c> - revoke a player's driver's or firearm license.
/// </summary>
/// <remarks>
/// THE LICENSES LIVE IN THE PLAYER'S SAVE FILE, beside their money, so this writes through
/// <see cref="LedgerFileStore"/> and inherits every rule a payout follows: offline only,
/// never a file the game did not make, never a file in a shape the bot cannot read back.
/// Revoking a license in a file the game is about to overwrite from memory reports success
/// and changes nothing, which is worse than refusing.
///
/// A SUBCOMMAND, so a later <c>/license grant</c> or <c>/license check</c> joins it rather
/// than becoming another top-level command, and <c>COMMANDS_DISABLED=license remove</c> can
/// switch this one off on its own.
/// </remarks>
public sealed class LicenseCommand : ISlashCommand
{
    private const RequiredAccess Required = RequiredAccess.Police;

    /// <summary>What the audit log files it under. Routed to the police log.</summary>
    internal const string AuditAction = "license-remove";

    private readonly LedgerFileStore? _saves;
    private readonly Access _access;
    private readonly AuditLog _audit;
    private readonly ILogger<LicenseCommand> _logger;

    public LicenseCommand(IBalanceStore balances, Access access, AuditLog audit, ILogger<LicenseCommand> logger)
    {
        // Only the file-backed store has licenses to revoke; anything else is refused at run time.
        _saves = balances as LedgerFileStore;
        _access = access;
        _audit = audit;
        _logger = logger;
    }

    public string Name => "license";

    public ApplicationCommandProperties Build()
    {
        var which = new SlashCommandOptionBuilder()
            .WithName("license")
            .WithDescription("Which license to take away")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true);
        foreach (var license in Licenses.All) which.AddChoice(license.Label, license.Choice);
        which.AddChoice("Both", Licenses.Every);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Police - Player licenses")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove")
                .WithDescription("Police - Revoke a player's driver's or firearm license")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("player", ApplicationCommandOptionType.String, "Their in-game name",
                    isRequired: true, isAutocomplete: true)
                .AddOption(which)
                .AddOption("reason", ApplicationCommandOptionType.String, "Why, for the audit log",
                    isRequired: false))
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_access.Allows(Required, command))
        {
            await Reply(command, Theme.Denied("Not allowed", AccessChecks.Refusal(Required))).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault(o => o.Type == ApplicationCommandOptionType.SubCommand);
        if (sub is not { Name: "remove" })
        {
            await Reply(command, Theme.Failure("Unknown subcommand")).ConfigureAwait(false);
            return;
        }

        string? Option(string name) => sub.Options.FirstOrDefault(o => o.Name == name)?.Value?.ToString();

        var player = Sanitize.Id(Option("player") ?? "");
        var licenses = Licenses.Named(Option("license"));
        var reason = Option("reason")?.Trim();

        if (player.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        if (licenses.Count == 0)
        {
            await Reply(command, Theme.Failure("Pick a license",
                $"Choose one of: {string.Join(", ", Licenses.All.Select(l => l.Label))}, or both.")).ConfigureAwait(false);
            return;
        }

        if (_saves is null)
        {
            await Reply(command, Theme.Failure("Not available",
                "This bot is not reading the game's save files, so it has no licenses to change.")).ConfigureAwait(false);
            return;
        }

        var result = _saves.RemoveLicenses(player, licenses);
        var name = Sanitize.Code(player);

        switch (result.Outcome)
        {
            case LicenseOutcome.Removed:
                var removed = string.Join(", ", result.Removed.Select(l => l.Label));
                await _audit.RecordAsync(AuditAction, command.User.Username, player,
                    string.IsNullOrWhiteSpace(reason) ? removed : $"{removed} | {reason}", ct).ConfigureAwait(false);
                _logger.LogInformation("license remove | player=\"{Player}\" | {Licenses} | by={By} | reason={Reason}",
                    player, removed, command.User.Username, reason ?? "");

                var embed = Theme.Success("License revoked", $"**{name}** no longer holds: **{removed}**.")
                    .AddField("By", command.User.Username, true);
                if (!string.IsNullOrWhiteSpace(reason)) embed.AddField("Reason", Sanitize.Code(reason), true);
                await Reply(command, embed).ConfigureAwait(false);
                return;

            case LicenseOutcome.NoneHeld:
                await Reply(command, Theme.Notice("Nothing to revoke",
                    $"**{name}** does not hold {Describe(result.Known)}. Their save file is unchanged.")).ConfigureAwait(false);
                return;

            default:
                await Reply(command, Theme.Failure("Not applied",
                    $"**{name}**'s licenses are unchanged — {result.Reason}.")).ConfigureAwait(false);
                return;
        }
    }

    private static string Describe(IReadOnlyList<License> licenses) =>
        licenses.Count == 1 ? $"a {licenses[0].Label.ToLowerInvariant()}" : "either license";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
