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
        var anyStale = false;

        foreach (var server in rcon.Servers)
        {
            var roster = rcon.Roster(server);

            /* A roster that has never been fetched is NOT an empty server. Saying "nobody is
               on" because the sweep has not run yet is a lie the board used to tell every
               time the bot restarted, and it is worth keeping the fix. */
            if (roster.TakenAt == DateTimeOffset.MinValue)
            {
                var why = rcon.RosterProblem(server);
                sections.Add($"**{Sanitize.Message(server)}** — *no roster yet*" +
                             (why is null ? "" : $"\n{Theme.Bad} {Sanitize.Message(why)}"));
                continue;
            }

            reported++;
            total += roster.Players.Count;

            var names = roster.Players
                .Select(p => p.Name).Where(n => n.Length > 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => $"`{Sanitize.Code(n)}`");

            /* HOW OLD IT IS, whenever it is not current. A frozen roster and a quiet server
               produce exactly the same list, and this command was presenting the one as the
               other - so "so-and-so is online" could be an hour out of date and read as live.
               The names are still shown: an old roster is worth something as long as you know
               that is what you are looking at. */
            var age = DateTimeOffset.UtcNow - roster.TakenAt;
            var stale = !rcon.RosterIsFresh(server);
            anyStale |= stale;

            var heading = $"**{Sanitize.Message(server)}** — {roster.Players.Count} online";
            if (stale)
            {
                heading += $"  {Theme.Warn} **as of {Describe(age)} ago**";
                if (rcon.RosterProblem(server) is { } problem)
                    heading += $"\n{Theme.Bad} {Sanitize.Message(problem)}";
            }

            sections.Add(heading + "\n" +
                         (roster.Players.Count > 0 ? string.Join(" ", names) : "*nobody*"));
        }

        var title = reported > 0
            ? $"Online — {total} player(s){(anyStale ? " (some data is stale)" : "")}"
            : "Online";

        /* PAGED, because a full server's roster can exceed an embed's limits and the board
           this replaces simply truncated. Paged sends a plain embed when it fits on one
           page, so the buttons only appear when there is a second page to reach. */
        return paged.SendAsync(command, title,
            Theme.Paginate(sections.Count > 0 ? sections : ["*No servers are configured.*"]), ct);
    }

    /// <summary>"4 minutes", "2 hours" - enough to judge whether the list is worth believing.</summary>
    internal static string Describe(TimeSpan age) => age switch
    {
        { TotalSeconds: < 90 } => $"{age.TotalSeconds:0} seconds",
        { TotalMinutes: < 90 } => $"{age.TotalMinutes:0} minutes",
        { TotalHours: < 36 } => $"{age.TotalHours:0} hours",
        _ => $"{age.TotalDays:0} days",
    };
}
