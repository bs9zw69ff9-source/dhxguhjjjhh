using System.Text;
using Discord;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Discord.Commands;

namespace PavlovBot.Host.Discord;

/// <param name="Faction">Their faction, or null when they are on no roster.</param>
internal sealed record BoardPlayer(string Name, string? Faction);

/// <summary>One server's contribution to the player board.</summary>
/// <param name="Age">
/// How old the list is, or NULL when no sweep has landed yet. The two are not the same thing
/// and the board must not present one as the other.
/// </param>
/// <param name="Stale">True when the sweep is overdue. The names are still shown, marked.</param>
/// <param name="Problem">Why the last sweep failed, when it did.</param>
internal sealed record BoardRoster(
    string Server,
    IReadOnlyList<BoardPlayer> Players,
    TimeSpan? Age,
    bool Stale,
    string? Problem);

/// <summary>
/// The live player board: who is on, which server, and which faction.
/// </summary>
/// <remarks>
/// THE BOARD THE NODE BOT POSTED TO <c>PLAYERLIST_CHANNEL</c>, which the port dropped in
/// favour of the player-count voice channels. The counters answer "how busy is it" from the
/// sidebar and carry no names; this answers "who is on, and are they one of ours" without
/// anybody having to run a command.
///
/// STYLED AS THE CONNECTION CARD, not as the leaderboards: one field per server rather than a
/// fixed-width table, because the rows here are names and tags and there is no column of
/// numbers to line up. It shares the connection card's field budget for the same reason the
/// card has one - see <see cref="EmbedBudget"/>.
///
/// A FULL SERVER DOES NOT FIT IN A FIELD, and that is the one hazard specific to this board.
/// Sixty players at a name, a tag and a bullet each is comfortably past Discord's 1024, so
/// rows are dropped until the list fits and the field SAYS HOW MANY went. Silently showing
/// the alphabetical first twenty as though they were everybody is the failure worth ruling
/// out: "is so-and-so on" would answer no for half the server.
///
/// PURE. It is handed what is known and renders it, so the interesting cases - a stale
/// roster, a server that has never answered, a roster too long for an embed - are testable
/// without an RCON connection or a gateway. <see cref="Boards.BuildPlayerBoardAsync"/> is
/// what gathers the inputs.
/// </remarks>
internal static class PlayerBoard
{
    /// <summary>Room kept back for the "and N more" line, so it always fits once needed.</summary>
    private const int TailAllowance = 28;

    /// <summary>
    /// The board, or null when there is nothing yet worth replacing what is already posted.
    /// </summary>
    /// <remarks>
    /// NULL IS THE RESTART WINDOW AND NOTHING ELSE. Services start before the first RCON
    /// sweep lands, so for up to a minute every server reads "no roster yet" - and posting
    /// that overwrites a perfectly good list with a worse one, every time the bot restarts.
    ///
    /// A sweep that is FAILING is not that case: it never recovers on its own, so the board
    /// posts and says so per server. The distinction is the whole reason
    /// <see cref="BoardRoster.Problem"/> is carried this far in.
    /// </remarks>
    public static Embed? Build(IReadOnlyList<BoardRoster> servers, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var answered = servers.Where(s => s.Age is not null).ToList();
        var broken = servers.Any(s => s.Problem is { Length: > 0 });

        if (servers.Count > 0 && answered.Count == 0 && !broken) return null;

        var total = answered.Sum(s => s.Players.Count);
        var stale = answered.Any(s => s.Stale);

        const string title = "Players online";
        var description = servers.Count == 0
            ? "No servers are configured."
            : answered.Count == 0
                ? "No server has answered yet."
                : $"**{total}** online across {answered.Count} server(s).";

        var embed = new EmbedBuilder()
            .WithColor(answered.Count == 0 ? Theme.Grey : stale || broken ? Theme.Amber : Theme.Green)
            .WithTitle(title)
            .WithDescription(description);

        var budget = new EmbedBudget(embed, title.Length + description.Length + EmbedBudget.FooterAllowance);
        var dropped = 0;

        foreach (var server in servers)
        {
            var label = Label(server);
            var notes = Notes(server);

            /* The names get whatever the notes leave, so a stale marker and a sweep failure
               are never the lines squeezed out - they are what makes the list believable. */
            var room = budget.Room(label) - notes.Length - (notes.Length > 0 ? 1 : 0);
            var names = server.Age is null ? "" : Roll(server.Players, room);

            if (!budget.Add(label, Join(notes, names))) dropped++;
        }

        var footer = $"Player board — {EasternTime.Stamp(at)} Eastern" +
                     (dropped > 0 ? $" · {dropped} server(s) would not fit" : "");

        return embed.Brand(footer).Build();
    }

    private static string Label(BoardRoster server) =>
        server.Age is null
            ? $"{Sanitize.Message(server.Server)} — no roster yet"
            : $"{Sanitize.Message(server.Server)} — {server.Players.Count} online";

    /// <summary>How old the list is and what went wrong, when either needs saying.</summary>
    private static string Notes(BoardRoster server)
    {
        var lines = new List<string>();

        /* Only when nothing went wrong. A sweep that FAILED has run, and saying both reads
           as a contradiction to the person trying to work out which it was. */
        if (server.Age is null && server.Problem is not { Length: > 0 })
        {
            lines.Add("The sweep has not run yet.");
        }
        else if (server.Age is not null && server.Stale)
        {
            // A frozen roster and a quiet server look identical. /players learnt this the
            // hard way: an hour-old list was reading as live.
            lines.Add($"{Theme.Warn} **as of {PlayersCommand.Describe(server.Age.Value)} ago**");
        }

        if (server.Problem is { Length: > 0 } problem)
            lines.Add($"{Theme.Bad} {Sanitize.Message(problem)}");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The names, with a faction tag each, trimmed to the room available.
    /// </summary>
    /// <param name="room">Characters left in the field once the notes have had theirs.</param>
    private static string Roll(IReadOnlyList<BoardPlayer> players, int room)
    {
        if (players.Count == 0) return "*nobody online*";

        var lines = players
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => $"{Theme.Dot} `{Sanitize.Code(p.Name)}`" +
                         (string.IsNullOrWhiteSpace(p.Faction) ? "" : $" — {Sanitize.Message(p.Faction)}"))
            .ToList();

        if (lines.Count == 0) return "*nobody online*";

        var text = new StringBuilder();
        var shown = 0;

        foreach (var line in lines)
        {
            // Hold back the tail while there is still a name after this one, so the count of
            // what was dropped is guaranteed a place to go.
            var reserve = shown + 1 < lines.Count ? TailAllowance : 0;
            var needed = line.Length + (shown == 0 ? 0 : 1);

            if (text.Length + needed + reserve > room) break;

            if (shown > 0) text.Append('\n');
            text.Append(line);
            shown++;
        }

        if (shown < lines.Count)
        {
            if (shown > 0) text.Append('\n');
            text.Append($"*… and {lines.Count - shown} more*");
        }

        return text.ToString();
    }

    private static string Join(string notes, string names) =>
        notes.Length == 0 ? names : names.Length == 0 ? notes : $"{notes}\n{names}";
}
