using System.Text;
using System.Text.RegularExpressions;
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
/// ONE INLINE FIELD PER SERVER, which is what puts the servers SIDE BY SIDE: Discord lays
/// inline fields out three to a row, so a three-server install reads as three columns of
/// names. That is the whole shape of the board and the reason the fields are not a
/// fixed-width table like the leaderboards - there is no column of numbers to line up, and a
/// fenced block would not wrap inside a third of the width.
///
/// NAMES ARE PRINTED PLAIN, not in inline code. Three columns of backticked names is a wall
/// of grey boxes at phone width; the cost is that a name can format the text around it, which
/// is what <see cref="Sanitize.Markdown"/> is for.
///
/// A FULL SERVER DOES NOT FIT IN A FIELD, and that is the one hazard specific to this board.
/// Sixty players at a name, a tag and a bullet each is comfortably past Discord's 1024, so a
/// list that long CONTINUES INTO ANOTHER COLUMN rather than being cut - and if it outruns
/// even that, the last column says how many names it could not show. Silently showing the
/// alphabetical first forty as though they were everybody is the failure worth ruling out:
/// "is so-and-so on" would answer no for half the server.
///
/// PURE. It is handed what is known and renders it, so the interesting cases - a stale
/// roster, a server that has never answered, a roster too long for an embed - are testable
/// without an RCON connection or a gateway. <see cref="Boards.BuildPlayerBoardAsync"/> is
/// what gathers the inputs.
/// </remarks>
internal static partial class PlayerBoard
{
    /// <summary>Room kept back for the "and N more" line, so it always fits once needed.</summary>
    private const int TailAllowance = 28;

    /// <summary>
    /// How many columns one server's list may spill across before it is cut short.
    /// </summary>
    /// <remarks>
    /// Four fields is around 170 names, well past any Pavlov server's capacity, and the real
    /// limit is the embed's own budget long before this bites. It exists so one absurd roster
    /// cannot take every column on the board and leave the other servers with none.
    /// </remarks>
    private const int MaxColumnsPerServer = 4;


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

        const string title = "Live Player List";
        var description = servers.Count == 0
            ? "No servers are configured."
            : answered.Count == 0
                ? "No server has answered yet."
                // A quote block, so the count reads as a caption under the title rather than
                // as the first line of the list.
                : $"> **{total}** *{Lore.Roaming(total)}*";

        var embed = new EmbedBuilder()
            .WithColor(answered.Count == 0 ? Theme.Grey : stale || broken ? Theme.Amber : Theme.Green)
            .WithTitle(title)
            .WithDescription(description);

        var budget = new EmbedBudget(embed, title.Length + description.Length + EmbedBudget.FooterAllowance);
        var dropped = 0;

        foreach (var server in servers)
        {
            /* The notes come first in the same list as the names, so a stale marker and a
               sweep failure are never what gets squeezed out - they are what makes the list
               underneath them believable. */
            var columns = Columns(Lines(server));

            for (var column = 0; column < columns.Count; column++)
            {
                var label = column == 0 ? Label(server) : $"{Display(server.Server)} (cont.)";

                // Out of embed budget entirely: the rest of this server's columns, and every
                // server after it, will not fit either. Say so in the footer rather than
                // leaving a board that is quietly short a server.
                if (!budget.Add(label, columns[column], inline: true))
                {
                    dropped++;
                    break;
                }
            }
        }

        var footer = $"Updated {EasternTime.Stamp(at)} Eastern" +
                     (dropped > 0 ? $" · {dropped} column(s) would not fit" : "");

        return embed.Brand(footer).Build();
    }

    private static string Label(BoardRoster server) =>
        server.Age is null
            ? $"{Display(server.Server)} (no roster yet)"
            : $"{Display(server.Server)} ({server.Players.Count})";

    [GeneratedRegex(@"^server\s*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InternalName { get; }

    /// <summary>
    /// "Server 2" - what a player calls the server, not what the config calls it.
    /// </summary>
    /// <remarks>
    /// The configured names are <c>server1</c>, <c>server2</c>, <c>server3</c>: a key, chosen
    /// by BotOptions from the RCON_HOST_n index, never typed by anybody. The player-count
    /// voice channels already spell that "Server 2" in public, and a board beside them saying
    /// "server2" looks like two different things being named.
    ///
    /// ANYTHING ELSE IS PRINTED AS IT STANDS. A name that is not the generated pattern was
    /// deliberately chosen and is not this function's to restyle.
    /// </remarks>
    private static string Display(string server) =>
        InternalName.Match(server ?? "") is { Success: true } match
            ? $"Server {match.Groups[1].Value}"
            : Sanitize.Message(server);

    /// <summary>
    /// One server's whole column: how old the list is, what went wrong, then the names.
    /// </summary>
    private static List<string> Lines(BoardRoster server)
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

        if (server.Age is null) return lines;

        var names = server.Players
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => $"{Theme.Dot} {Sanitize.Markdown(p.Name)}" +
                         (string.IsNullOrWhiteSpace(p.Faction) ? "" : $" — {Sanitize.Markdown(p.Faction)}"))
            .ToList();

        lines.Add(names.Count == 0 ? "*nobody online*" : names[0]);
        lines.AddRange(names.Skip(1));
        return lines;
    }

    /// <summary>
    /// Split a column that is too long for one field across several.
    /// </summary>
    /// <remarks>
    /// Discord caps a field at 1024 characters and THROWS from <c>Build()</c> past it, so
    /// something has to give on a full server. Continuing into the next column keeps every
    /// name; only a roster past <see cref="MaxColumnsPerServer"/> columns is cut, and then the
    /// last column says how many names went rather than ending mid-list.
    /// </remarks>
    private static List<string> Columns(IReadOnlyList<string> lines)
    {
        var columns = new List<string>();
        var current = new StringBuilder();
        var index = 0;

        while (index < lines.Count && columns.Count < MaxColumnsPerServer)
        {
            var last = columns.Count == MaxColumnsPerServer - 1;
            var line = lines[index];
            var needed = line.Length + (current.Length == 0 ? 0 : 1);

            /* On the last column allowed, hold back room for the "and N more" line while
               there is still a name after this one, so the count of what was cut is
               guaranteed somewhere to go. */
            var reserve = last && index + 1 < lines.Count ? TailAllowance : 0;

            if (current.Length + needed + reserve <= EmbedBudget.FieldLimit)
            {
                if (current.Length > 0) current.Append('\n');
                current.Append(line);
                index++;
                continue;
            }

            // Nothing fits in an empty column only if one line is longer than a whole field,
            // and Sanitize caps every one of them at 200 characters.
            columns.Add(current.ToString());
            current.Clear();
        }

        if (index < lines.Count)
        {
            if (current.Length > 0) current.Append('\n');
            current.Append($"*… and {lines.Count - index} more*");
        }

        if (current.Length > 0) columns.Add(current.ToString());
        return columns;
    }
}
