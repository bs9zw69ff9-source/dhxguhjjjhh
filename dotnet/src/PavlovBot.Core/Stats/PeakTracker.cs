namespace PavlovBot.Core.Stats;

/// <param name="Peak">Highest count seen.</param>
/// <param name="PeakAt">When it was seen.</param>
public sealed record PeakEntry(int Peak, DateTimeOffset PeakAt);

/// <param name="Date">The day key this bucket belongs to. A different key means roll over.</param>
public sealed record DailyPeaks(
    string Date,
    IReadOnlyDictionary<string, PeakEntry> PerServer,
    PeakEntry Combined);

public sealed record PeakStats(
    IReadOnlyDictionary<string, PeakEntry> PerServer,
    PeakEntry? Combined,
    DailyPeaks? Daily,
    DateTimeOffset? UpdatedAt)
{
    public static PeakStats Empty { get; } =
        new(new Dictionary<string, PeakEntry>(StringComparer.Ordinal), null, null, null);
}

/// <param name="Changed">
/// False means nothing beat a record, so the caller can skip the write entirely. This is
/// the whole reason the reducer returns it: peaks are sampled every minute and almost
/// never move, so writing unconditionally would be a disk write a minute forever.
/// </param>
public sealed record PeakUpdate(bool Changed, PeakStats Stats);

/// <summary>
/// Peak concurrent players, all-time and per day.
/// </summary>
/// <remarks>
/// A pure reducer: current counts in, updated stats out, no I/O. All-time peaks are
/// monotonic and never reset; daily peaks reset when the day key changes.
///
/// <c>date</c> is an OPAQUE day key supplied by the caller rather than derived here,
/// because "which day is it" is a timezone question and this type has no business having
/// an opinion on it. The bot passes the Eastern calendar day.
/// </remarks>
public static class PeakTracker
{
    /// <param name="counts">Server name -> players online right now.</param>
    /// <param name="current">Previously stored stats.</param>
    /// <param name="date">Day key for daily tracking, or null to skip it.</param>
    public static PeakUpdate Reduce(
        IReadOnlyDictionary<string, int> counts,
        PeakStats? current,
        DateTimeOffset now,
        string? date = null)
    {
        ArgumentNullException.ThrowIfNull(counts);
        current ??= PeakStats.Empty;

        var combined = counts.Values.Sum();
        var perServer = new Dictionary<string, PeakEntry>(current.PerServer, StringComparer.Ordinal);
        var changed = false;

        // ---- all-time ----
        foreach (var (server, count) in counts)
        {
            if (count <= (perServer.GetValueOrDefault(server)?.Peak ?? 0)) continue;
            perServer[server] = new PeakEntry(count, now);
            changed = true;
        }

        var allTimeCombined = current.Combined;
        if (combined > (allTimeCombined?.Peak ?? 0))
        {
            allTimeCombined = new PeakEntry(combined, now);
            changed = true;
        }

        // ---- daily ----
        DailyPeaks? daily = current.Daily;
        if (date is not null)
        {
            var rolledOver = daily is null || !string.Equals(daily.Date, date, StringComparison.Ordinal);
            var dailyPerServer = rolledOver
                ? new Dictionary<string, PeakEntry>(StringComparer.Ordinal)
                : new Dictionary<string, PeakEntry>(daily!.PerServer, StringComparer.Ordinal);
            var dailyCombined = rolledOver ? new PeakEntry(0, now) : daily!.Combined;

            // Persist the day's reset even at zero players, or a quiet midnight leaves
            // yesterday's numbers on display as though they were today's.
            if (rolledOver) changed = true;

            foreach (var (server, count) in counts)
            {
                if (count <= (dailyPerServer.GetValueOrDefault(server)?.Peak ?? 0)) continue;
                dailyPerServer[server] = new PeakEntry(count, now);
                changed = true;
            }
            if (combined > dailyCombined.Peak)
            {
                dailyCombined = new PeakEntry(combined, now);
                changed = true;
            }

            daily = new DailyPeaks(date, dailyPerServer, dailyCombined);
        }

        var stats = new PeakStats(perServer, allTimeCombined, daily, changed ? now : current.UpdatedAt);
        return new PeakUpdate(changed, stats);
    }

    /// <summary>
    /// The combined peak for <paramref name="date"/>, or 0 when the stored bucket is for a
    /// different day. Safe to display without writing - today simply has no record yet.
    /// </summary>
    public static int DailyPeak(PeakStats? stats, string date) =>
        stats?.Daily is { } daily && string.Equals(daily.Date, date, StringComparison.Ordinal)
            ? daily.Combined.Peak
            : 0;
}
