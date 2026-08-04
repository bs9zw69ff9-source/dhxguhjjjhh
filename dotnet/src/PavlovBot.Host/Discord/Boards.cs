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
public sealed class Boards(SerializedStore store, RconRegistry rcon, string? ledgerDirectory = null)
{
    private const int TopRows = 15;

    /// <summary>The whole economy: every ledger, richest first.</summary>
    /// <param name="Total">Across EVERY ledger, not just the rows shown.</param>
    public sealed record CashStanding(IReadOnlyList<(string Player, long Balance)> Top, long Total, int Ledgers);

    /// <summary>
    /// Read every player's balance out of the ModSave directory.
    /// </summary>
    /// <remarks>
    /// Straight from the files the game itself writes, exactly as the Node bot does it.
    /// There is no cached copy to go stale, and a balance the bot invented would be worse
    /// than no board at all.
    ///
    /// <c>banlist.txt</c> lives in the same directory and is not a ledger.
    /// </remarks>
    public CashStanding? ReadCash()
    {
        if (string.IsNullOrWhiteSpace(ledgerDirectory) || !Directory.Exists(ledgerDirectory)) return null;

        var balances = new List<(string Player, long Balance)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(ledgerDirectory, "*.txt"))
            {
                var player = Path.GetFileNameWithoutExtension(file);
                if (string.Equals(player, "banlist", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var text = File.ReadAllText(file).Trim();
                    // A ledger the game is mid-write on parses as nothing. Skipping it drops
                    // one row for one cycle; guessing zero would announce that somebody lost
                    // all their money.
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var balance))
                        balances.Add((player, balance));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        balances.Sort((a, b) => b.Balance.CompareTo(a.Balance));
        return new CashStanding(balances.Take(TopRows).ToList(), balances.Sum(b => b.Balance), balances.Count);
    }

    /// <summary>
    /// Players ranked by account balance - what <c>LEADERBOARD_CHANNEL</c> is for.
    /// </summary>
    /// <remarks>
    /// THIS CHANNEL IS THE CASH BOARD. The port originally posted the playtime board here,
    /// which put a playtime leaderboard in a channel called cash-leaderboard while the Node
    /// bot posted the real one alongside it. Playtime is still tracked for
    /// <c>/whitelist playtime</c>; it is simply not what this board shows.
    /// </remarks>
    public Embed? BuildCashBoard()
    {
        var standing = ReadCash();

        /* Null here means the path is wrong or unreadable, which is a configuration problem
           the owner has to see. Returning null would skip the cycle silently and leave a
           board that just never updates. */
        if (standing is null)
        {
            return Theme.Failure($"{Theme.Money} Richest players",
                    "Economy records are unreadable. `MODSAVE_PATH` is not set, or the bot cannot read it.")
                .Brand($"Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
                .Build();
        }

        /* An explicit empty board, NOT null. Null means "skip this cycle and leave what is
           already posted", which is right for a board whose content is merely stale - and
           wrong here, because what is already posted is the PLAYTIME board this one
           replaced. Skipping would leave a playtime leaderboard sitting in the cash channel
           indefinitely, looking for all the world like it was still being updated. */
        if (standing.Ledgers == 0)
        {
            return Theme.Notice($"{Theme.Money} Richest players",
                    "No ledgers on file yet. Balances appear here once players have money.")
                .Brand($"Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
                .Build();
        }

        var top = standing.Top.Count > 0 ? Math.Max(standing.Top[0].Balance, 1) : 1;
        var lines = standing.Top.Select((row, i) =>
            $"`{i + 1,2}.` {Medal(i)} **{Sanitize.Code(row.Player)}** — ${row.Balance.ToString("N0", CultureInfo.InvariantCulture)}" +
            (i < 5 ? $"\n{Theme.Bar(Math.Max(row.Balance, 0), top)}" : ""));

        return Theme.Notice($"{Theme.Money} Richest players — top {standing.Top.Count}",
                $"Combined **${standing.Total.ToString("N0", CultureInfo.InvariantCulture)}** across " +
                $"**{standing.Ledgers}** ledger(s)\n\n{string.Join("\n", lines)}")
            .Brand($"Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
            .Build();
    }

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

    /* THERE IS NO PLAYTIME BOARD, deliberately. It was removed from this bot before the
       port, and the port reintroduced it by wiring it to LEADERBOARD_CHANNEL - which is the
       cash board's channel.

       Playtime is still ACCUMULATED above, because `/whitelist playtime` reports it and the
       Node bot reads the same dataset. Tracking it costs one write a minute; stopping would
       leave that command answering with numbers frozen at the cutover. */

    /// <summary>Players ranked by jail time served - the "most wanted" board.</summary>
    public Embed? BuildArrestBoard()
    {
        var arrests = store.Read(Datasets.Arrests, new Dictionary<string, List<Arrest>>(StringComparer.OrdinalIgnoreCase));

        var rows = arrests
            .Where(kv => kv.Value.Count > 0)
            /* The DISPLAY-CASE name off the entries, falling back to the map key - which is
               lowercased. Node does the same, and an entry whose name did not read leaves a
               blank row rather than an obviously-wrong one. */
            .Select(kv => (
                Player: kv.Value[^1].Player is { Length: > 0 } named ? named : kv.Key,
                Minutes: kv.Value.Sum(a => a.JailMinutes),
                Count: kv.Value.Count))
            // Jail time first, arrest count as the tiebreak: one long sentence outranks
            // several short ones, which is what "most wanted" is supposed to mean.
            .OrderByDescending(r => r.Minutes).ThenByDescending(r => r.Count)
            .Take(TopRows)
            .ToList();

        /* An explicit empty board, NOT null. Null means "skip this cycle and leave what is
           already posted" - and on a fresh cutover what is already posted is the Node bot's
           most-wanted board, which then sits frozen in the channel forever while this one
           never appears. */
        if (rows.Count == 0)
        {
            return Theme.Notice($"{Theme.Deny} Most wanted", "No arrests on record yet.")
                .Brand($"Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
                .Build();
        }

        var top = rows[0].Minutes;
        var lines = rows.Select((r, i) =>
            $"`{i + 1,2}.` {Medal(i)} **{Sanitize.Code(r.Player)}** — {r.Minutes} min over {r.Count} arrest(s)\n" +
            $"{Theme.Bar(r.Minutes, top)}");

        return Theme.Punishment($"{Theme.Deny} Most wanted", string.Join("\n", lines))
            .Brand($"Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
            .Build();
    }

    /// <summary>
    /// Everyone with an outstanding warrant, oldest first.
    /// </summary>
    /// <remarks>
    /// OLDEST FIRST, which is the opposite of every other board here. The rest rank by size
    /// because they are leaderboards; this one is a WORK QUEUE, and the warrant nobody has
    /// served in three days is the one that needs attention, not the one issued a minute ago.
    ///
    /// An EMPTY board rather than null, same as the arrest board: "no warrants" is real
    /// information on a police server and posting nothing would leave whatever was there
    /// before sitting frozen and wrong.
    ///
    /// The reason is included because a warrant board that only lists names makes every
    /// officer ask in chat what it was for.
    /// </remarks>
    public Embed? BuildWarrantBoard()
    {
        var warrants = store.Read(Datasets.Warrants, new Dictionary<string, List<Warrant>>(StringComparer.OrdinalIgnoreCase));

        var rows = warrants
            .Where(kv => kv.Value is { Count: > 0 })
            .Select(kv => (
                // The display-case name off the entries, falling back to the map key, which
                // is lowercased. Same fallback the arrest board uses.
                Player: kv.Value[^1].Player is { Length: > 0 } named ? named : kv.Key,
                Count: kv.Value.Count,
                Oldest: kv.Value.Min(w => w.At),
                Latest: kv.Value[^1]))
            .OrderBy(r => r.Oldest)
            .Take(TopRows)
            .ToList();

        if (rows.Count == 0)
        {
            return Theme.Success($"{Theme.Ok} No active warrants", "Nobody is currently wanted.")
                .Brand($"Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
                .Build();
        }

        var lines = rows.Select(r =>
            $"{Theme.Deny} **{Sanitize.Code(r.Player)}**{(r.Count > 1 ? $" ×{r.Count}" : "")}\n" +
            $"　{Sanitize.Code(r.Latest.Reason)} — issued {Theme.Relative(r.Oldest)} by {Sanitize.Code(r.Latest.IssuedBy)}");

        var total = warrants.Where(kv => kv.Value is { Count: > 0 }).Sum(kv => kv.Value.Count);

        var embed = Theme.Punishment($"{Theme.Deny} Active warrants", string.Join("\n", lines))
            .AddField("Wanted", rows.Count.ToString(CultureInfo.InvariantCulture), true)
            .AddField("Warrants", total.ToString(CultureInfo.InvariantCulture), true);

        if (warrants.Count(kv => kv.Value is { Count: > 0 }) > TopRows)
            embed.AddField("Not shown", $"{warrants.Count(kv => kv.Value is { Count: > 0 }) - TopRows} more wanted.");

        return embed
            .Brand($"Oldest first · Updated {EasternTime.Stamp(DateTimeOffset.UtcNow)} Eastern")
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
