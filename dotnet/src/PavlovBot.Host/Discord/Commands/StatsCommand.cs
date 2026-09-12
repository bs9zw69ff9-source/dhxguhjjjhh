using System.Globalization;
using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Intelligence;
using PavlovBot.Core.Text;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Intelligence;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/stats</c> - one player's card: faction, playtime, K/D, caps.
/// </summary>
/// <remarks>
/// THE PLAYER-FACING HALF OF <c>/player</c>. That command is a moderation tool - addresses,
/// alts, risk signals, ban history - and it is gated accordingly. This is the same underlying
/// profile with none of that in it: what somebody would ask another player, answered without
/// needing a moderator.
///
/// NO NAME NEEDED when the caller was whitelisted through <c>/whitelist add</c>: the faction
/// index already maps their Discord account to the in-game name they play under, so the
/// common case is typing four characters. Anybody can look up anybody - a scoreboard nobody
/// else can read is not a scoreboard.
///
/// EVERY NUMBER HERE IS ONE THE BOT ACTUALLY HAS. Kills and deaths are counted off Pavlov.log
/// by <see cref="Stats.KillStats"/>; playtime is the minute ticker; the faction and rank come
/// from the roster files the game reads. Nothing is estimated, and a player the bot has never
/// seen is told that rather than shown a wall of zeroes.
/// </remarks>
public sealed class StatsCommand(
    PlayerIntelligenceService intelligence, FactionMembers members, Access access) : ISlashCommand
{
    public string Name => "stats";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Playtime, K/D and faction for a player")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("player")
                .WithDescription("In-game name. Defaults to the name you were whitelisted under")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .WithAutocomplete(true))
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Public, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Public, command)))
                .ConfigureAwait(false);
            return;
        }

        var typed = command.Data.Options.FirstOrDefault(o => o.Name == "player")?.Value as string;
        var player = Sanitize.Id(typed ?? "");

        if (player.Length == 0)
        {
            // Their own name, if the bot knows it. Asking somebody to type a name the bot
            // already has on file is the kind of friction that stops a command being used.
            player = Sanitize.Id(members.Of(command.User.Id)?.Name ?? "");

            if (player.Length == 0)
            {
                await Reply(command, Theme.Failure("Which player?",
                    "Give an in-game name. Yours is filled in automatically once you have been " +
                    "whitelisted with `/whitelist add`.")).ConfigureAwait(false);
                return;
            }
        }

        /* THE NARROW VIEW, and the redaction is the point rather than a formality: this
           command is open to everybody, and the profile underneath carries addresses and
           linked accounts. Asking for the redacted visibility means the embed physically
           cannot print what it must not - the card below shows a subset of even that. */
        var profile = await intelligence.ProfileAsync(player, ProfileVisibility.Moderation, ct).ConfigureAwait(false);

        await Reply(command, Card(profile)).ConfigureAwait(false);
    }

    /// <summary>
    /// The card.
    /// </summary>
    /// <remarks>
    /// PURE, so what it says is testable without a gateway or a live profile service.
    /// </remarks>
    internal static EmbedBuilder Card(PlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var who = Sanitize.Message(profile.Identity.Current);

        /* A STRANGER IS TOLD SO. Zeroes across the board read as "this player is terrible",
           not as "the bot has never seen this person", and the two need different answers. */
        if (!profile.Known)
        {
            return Theme.Notice($"{Lore.Rads} {who}",
                "Never seen on any server. Check the spelling - names are exactly as they are in game.");
        }

        var combat = profile.Combat ?? ProfileCombat.None;
        var faction = profile.Faction.Name is { Length: > 0 } name
            ? $"**{Sanitize.Markdown(name)}**" +
              (profile.Faction.Rank is { Length: > 0 } rank ? $" — {Sanitize.Markdown(rank)}" : "")
            : "*unaffiliated*";

        var embed = Theme.Notice($"{Lore.Rads} {who}", faction)
            .WithColor(profile.Activity.Online ? Theme.Green : Theme.Blue);

        embed.AddField("Playtime", Hours(profile.Activity.PlaytimeMinutes), inline: true);

        embed.AddField("K/D", combat.Any
            ? $"**{combat.Ratio.ToString("0.00", CultureInfo.InvariantCulture)}**"
            : "*nothing recorded*", inline: true);

        embed.AddField("Kills / deaths", combat.Any
            ? $"{combat.Kills.ToString("N0", CultureInfo.InvariantCulture)} / " +
              $"{combat.Deaths.ToString("N0", CultureInfo.InvariantCulture)}" +
              // Named rather than folded into deaths: it is the difference between being
              // outgunned and standing on your own grenade.
              (combat.Suicides > 0 ? $"  ({combat.Suicides} self)" : "")
            : "—", inline: true);

        if (profile.Economy.Balance is { } balance)
            embed.AddField("Caps", Lore.Amount(balance), inline: true);

        embed.AddField("Status", profile.Activity.Online
            ? $"{Theme.Up} online now" + (profile.Activity.Servers.Count > 0
                ? $" on {Sanitize.Markdown(string.Join(", ", profile.Activity.Servers))}"
                : "")
            : profile.Activity.LastSeen is { } seen
                ? $"last seen {Theme.Relative(seen)}"
                : "not seen recently", inline: true);

        if (profile.Activity.FirstSeen is { } first)
            embed.AddField("First seen", Theme.Relative(first), inline: true);

        return embed;
    }

    /// <summary>"3h 20m", "45m" - a playtime somebody can read at a glance.</summary>
    internal static string Hours(long minutes) =>
        minutes >= 60
            ? $"{(minutes / 60).ToString("N0", CultureInfo.InvariantCulture)}h {minutes % 60}m"
            : $"{minutes}m";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
