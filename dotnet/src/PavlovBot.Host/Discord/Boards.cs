using System.Globalization;
using Discord;
using PavlovBot.Core.Data;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord;

/// <param name="Minutes">Total minutes played, accumulated by the playtime ticker.</param>
public sealed record PlaytimeEntry(string Player, long Minutes, DateTimeOffset? LastSeen);

/// <summary>
/// The content of the auto-posted boards.
/// </summary>
/// <remarks>
/// Separate from <see cref="AutoPost"/>, which owns WHERE a board lives and how it is kept
/// up to date. This owns only what it says. Splitting them is what let the posting
/// machinery be tested without a gateway and these be tested without a channel.
///
/// EVERY BUILDER RETURNS NULL WHEN THERE IS NOTHING TO SHOW, and null means "skip this
/// cycle". An empty board would overwrite yesterday's real one with "no data" during a
/// restart or a transient read failure - showing stale numbers is strictly better.
/// </remarks>
public sealed class Boards(SerializedStore store, RconRegistry rcon)
{
    private const int TopRows = 15;

    public Dictionary<string, PlaytimeEntry> Playtime() =>
        store.Read(Datasets.Playtime, new Dictionary<string, PlaytimeEntry>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Add a minute to everyone currently online.
    /// </summary>
    /// <remarks>
    /// Driven by the roster the RCON sweep already fetched rather than a fetch of its own -
    /// the numbers are then consistent with the player list, and it costs no round trip.
    /// </remarks>
    public Task TickPlaytimeAsync(TimeSpan elapsed, CancellationToken ct = default)
    {
        var online = rcon.AllOnlinePlayers();
        if (online.Count == 0) return Task.CompletedTask;

        var now = DateTimeOffset.UtcNow;
        var minutes = (long)Math.Round(elapsed.TotalMinutes);
        if (minutes <= 0) return Task.CompletedTask;

        return store.UpdateAsync(Datasets.Playtime,
            new Dictionary<string, PlaytimeEntry>(StringComparer.OrdinalIgnoreCase),
            playtime =>
            {
                foreach (var player in online)
                {
                    var existing = playtime.GetValueOrDefault(player);
                    playtime[player] = new PlaytimeEntry(player, (existing?.Minutes ?? 0) + minutes, now);
                }
                return playtime;
            }, ct);
    }

    /// <summary>Players ranked by time on the server.</summary>
    public Embed? BuildPlaytimeBoard()
    {
        var rows = Playtime().Values
            .Where(e => e.Minutes > 0)
            .OrderByDescending(e => e.Minutes)
            .Take(TopRows)
            .ToList();

        if (rows.Count == 0) return null;

        var top = rows[0].Minutes;
        var lines = rows.Select((e, i) =>
            $"`{i + 1,2}.` {Medal(i)} **{Sanitize.Code(e.Player)}** — {Hours(e.Minutes)}\n" +
            $"{Theme.Bar(e.Minutes, top)}");

        return Theme.Notice($"{Theme.Rank} Playtime leaderboard", string.Join("\n", lines))
            .Brand($"Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
            .Build();
    }

    /// <summary>Players ranked by jail time served - the "most wanted" board.</summary>
    public Embed? BuildArrestBoard()
    {
        var arrests = store.Read(Datasets.Arrests, new Dictionary<string, List<Arrest>>(StringComparer.OrdinalIgnoreCase));

        var rows = arrests
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => (Player: kv.Value[0].Player, Minutes: kv.Value.Sum(a => a.JailMinutes), Count: kv.Value.Count))
            // Jail time first, arrest count as the tiebreak: one long sentence outranks
            // several short ones, which is what "most wanted" is supposed to mean.
            .OrderByDescending(r => r.Minutes).ThenByDescending(r => r.Count)
            .Take(TopRows)
            .ToList();

        if (rows.Count == 0) return null;

        var top = rows[0].Minutes;
        var lines = rows.Select((r, i) =>
            $"`{i + 1,2}.` {Medal(i)} **{Sanitize.Code(r.Player)}** — {r.Minutes} min over {r.Count} arrest(s)\n" +
            $"{Theme.Bar(r.Minutes, top)}");

        return Theme.Punishment($"{Theme.Deny} Most wanted", string.Join("\n", lines))
            .Brand($"Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
            .Build();
    }

    /// <summary>Who is online right now, per server.</summary>
    public Embed? BuildPlayerList()
    {
        var sections = new List<string>();
        var total = 0;

        foreach (var server in rcon.Servers)
        {
            var roster = rcon.Roster(server);

            /* A roster that has never been fetched is NOT an empty server. Saying "nobody
               is on" because the sweep has not run yet is a lie the board would tell every
               time the bot restarts. */
            if (roster.TakenAt == DateTimeOffset.MinValue) continue;

            total += roster.Players.Count;
            var names = roster.Players
                .Select(p => p.Name).Where(n => n.Length > 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => $"`{Sanitize.Code(n)}`");

            sections.Add($"**{server}** — {roster.Players.Count} online\n" +
                         (roster.Players.Count > 0 ? string.Join(" ", names) : "*nobody*"));
        }

        if (sections.Count == 0) return null;

        return Theme.Notice($"{Theme.Up} Online — {total} player(s)", string.Join("\n\n", sections))
            .Brand($"Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
            .Build();
    }

    /// <summary>Staff ranked by moderation actions taken.</summary>
    public Embed? BuildStaffBoard()
    {
        var log = store.Read<List<ModAction>>(Datasets.ModLog, []);
        if (log.Count == 0) return null;

        var rows = log
            .GroupBy(a => a.Moderator, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Staff: g.Key, Count: g.Count(), Last: g.Max(a => a.At)))
            .OrderByDescending(r => r.Count)
            .Take(TopRows)
            .ToList();

        var lines = rows.Select((r, i) =>
            $"`{i + 1,2}.` **{Sanitize.Code(r.Staff)}** — {r.Count} action(s), last {Theme.Relative(r.Last)}");

        return Theme.Notice("Staff activity", string.Join("\n", lines))
            .Brand($"{log.Count} recorded action(s)")
            .Build();
    }

    private static string Medal(int index) => index switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => Theme.Dot };

    private static string Hours(long minutes) =>
        minutes >= 60
            ? $"{(minutes / 60).ToString("N0", CultureInfo.GetCultureInfo("en-US"))}h {minutes % 60}m"
            : $"{minutes}m";
}

/// <param name="Action">What was done - ban, unban, kick, arrest.</param>
public sealed record ModAction(string Action, string Moderator, string Player, string? Reason, DateTimeOffset At);
