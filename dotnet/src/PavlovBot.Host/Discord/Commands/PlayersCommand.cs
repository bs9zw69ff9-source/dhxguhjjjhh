using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Text;
using PavlovBot.Host.Rcon;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/players</c> - who is online right now, by name.
/// </summary>
/// <remarks>
/// The names half of the player-list board that the voice-channel counters replaced. The
/// counters answer "how busy is it" from the sidebar without publishing who is on; this
/// answers "is so-and-so on" for whoever actually asks.
///
/// Distinct from <c>/serverinfo</c>, which also reports the map, the mode and whether each
/// server is reachable. This one is the roster and nothing else, so it stays short enough
/// to read on a phone.
/// </remarks>
public sealed class PlayersCommand(RconRegistry rcon, Paged paged) : ISlashCommand
{
    public string Name => "players";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Who is online right now")
            .Build();

    public Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sections = new List<string>();
        var total = 0;
        var reported = 0;

        foreach (var server in rcon.Servers)
        {
            var roster = rcon.Roster(server);

            /* A roster that has never been fetched is NOT an empty server. Saying "nobody is
               on" because the sweep has not run yet is a lie the board used to tell every
               time the bot restarted, and it is worth keeping the fix. */
            if (roster.TakenAt == DateTimeOffset.MinValue)
            {
                sections.Add($"**{Sanitize.Message(server)}** — *no roster yet*");
                continue;
            }

            reported++;
            total += roster.Players.Count;

            var names = roster.Players
                .Select(p => p.Name).Where(n => n.Length > 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => $"`{Sanitize.Code(n)}`");

            sections.Add($"**{Sanitize.Message(server)}** — {roster.Players.Count} online\n" +
                         (roster.Players.Count > 0 ? string.Join(" ", names) : "*nobody*"));
        }

        var title = reported > 0 ? $"Online — {total} player(s)" : "Online";

        /* PAGED, because a full server's roster can exceed an embed's limits and the board
           this replaces simply truncated. Paged sends a plain embed when it fits on one
           page, so the buttons only appear when there is a second page to reach. */
        return paged.SendAsync(command, title,
            Theme.Paginate(sections.Count > 0 ? sections : ["*No servers are configured.*"]), ct);
    }
}
