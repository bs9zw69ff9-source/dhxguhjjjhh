using PavlovBot.Core.Events;

namespace PavlovBot.Host.Events;

/// <param name="Window">The period these numbers describe.</param>
/// <param name="UniquePlayers">Distinct players seen joining.</param>
/// <param name="Joins">Join events. Not sessions - see the remarks on the service.</param>
/// <param name="BusiestHour">The UTC hour with the most joins, and how many.</param>
/// <param name="Servers">Joins per server, busiest first.</param>
/// <param name="PerDay">Joins per UTC day, oldest first.</param>
public sealed record ServerAnalytics(
    TimeSpan Window,
    long UniquePlayers,
    long Joins,
    (string Hour, long Count)? BusiestHour,
    IReadOnlyList<(string Server, long Count)> Servers,
    IReadOnlyList<(string Day, long Count)> PerDay,
    long StaffActions,
    long SecurityDetections,
    long Bans)
{
    public static ServerAnalytics Empty(TimeSpan window) =>
        new(window, 0, 0, null, [], [], 0, 0, 0);

    /// <summary>Mean joins per day across the window. Zero for a window under a day.</summary>
    public double JoinsPerDay => Window.TotalDays >= 1 ? Joins / Window.TotalDays : 0;
}

/// <param name="Actions">What they did, most frequent first.</param>
/// <param name="Total">Everything they did in the window.</param>
/// <param name="BusiestHour">Their busiest hour, for the anomaly check.</param>
public sealed record StaffActivity(
    string Moderator,
    long Total,
    IReadOnlyList<(string Action, long Count)> Actions,
    (string Hour, long Count)? BusiestHour);

/// <summary>
/// Aggregate questions answered against the event table.
/// </summary>
/// <remarks>
/// EVERY NUMBER HERE IS COMPUTED IN SQL. That is not an optimisation, it is the reason the
/// event table exists: "unique players in thirty days" answered by fetching thirty days of
/// rows and counting them in memory would be the full scan the table was built to avoid, and
/// it would get slower every week while looking like it worked.
///
/// WHAT THESE NUMBERS ARE NOT. The brief asks for sessions, session duration, retention and
/// average concurrent players. None of those are derivable from what the bot records today:
///
///   THERE IS NO LEAVE EVENT. The tracker raises Joined and Confirmed; a disconnect is not
///   surfaced as an event, so a session has a start and no end. Session count and duration
///   are therefore not computed here rather than being estimated - a made-up duration in an
///   analytics panel is worse than an absent one, because nobody can tell it was made up.
///
///   AVERAGE CONCURRENT PLAYERS needs a sampled player count, which is a different shape of
///   data from an event log - a gauge, not a stream. PlayerCountChannels already polls it;
///   turning that into a stored series is its own piece of work.
///
///   RETENTION needs first-seen per player, which IpTrackingService HAS but the event table
///   does not. Joining the two per player is an N+1 over the account store, so it is left
///   undone rather than done badly.
///
/// What IS here is everything the timeline can answer honestly, and the panel says which
/// window it covers so a number is never read as all-time.
/// </remarks>
public sealed class AnalyticsService(IEventStore events)
{
    /// <summary>Most rows any grouped query returns. Well past what an embed can show.</summary>
    private const int GroupLimit = 50;

    /// <summary>The picture for one window, optionally narrowed to one server.</summary>
    public ServerAnalytics Overview(TimeSpan window, string? server = null, DateTimeOffset? now = null)
    {
        var since = (now ?? DateTimeOffset.UtcNow) - window;

        EventQuery For(EventCategory? category = null, string? kind = null) =>
            new(GroupLimit, since, Category: category, Server: server);

        var joins = new EventQuery(GroupLimit, since, Category: EventCategory.Player, Server: server);

        var perHour = events.CountBy(EventGrouping.Hour, joins);
        var busiest = perHour.Count > 0 ? perHour.MaxBy(h => h.Count) : default;

        return new ServerAnalytics(
            window,
            events.CountDistinct(EventGrouping.Player, joins),
            perHour.Sum(h => h.Count),
            perHour.Count > 0 ? (busiest.Key, busiest.Count) : null,
            events.CountBy(EventGrouping.Server, joins),

            // Chronological rather than by size: this one is read as a trend line.
            [.. events.CountBy(EventGrouping.Day, joins).OrderBy(d => d.Key, StringComparer.Ordinal)],

            events.CountBy(EventGrouping.Kind, For(EventCategory.Staff)).Sum(k => k.Count),
            events.CountBy(EventGrouping.Kind, For(EventCategory.Security)).Sum(k => k.Count),
            events.CountBy(EventGrouping.Kind, For(EventCategory.Staff))
                .Where(k => k.Key.Contains("ban", StringComparison.OrdinalIgnoreCase))
                .Sum(k => k.Count));
    }

    /// <summary>Who has been doing the moderating, busiest first.</summary>
    /// <remarks>
    /// AUTOMATED ACTORS ARE EXCLUDED. The VPN responder and the evasion responder both record
    /// as "auto", and counting them would put a phantom at the top of every leaderboard and
    /// make the anomaly check below meaningless.
    /// </remarks>
    public IReadOnlyList<(string Moderator, long Actions)> StaffLeaderboard(TimeSpan window, DateTimeOffset? now = null)
    {
        var since = (now ?? DateTimeOffset.UtcNow) - window;

        return
        [
            .. events.CountBy(EventGrouping.Actor, new EventQuery(GroupLimit, since))
                .Where(row => !EventMapping.IsAutomated(row.Key))
                .Select(row => (row.Key, row.Count)),
        ];
    }

    /// <summary>What one staff member did in the window.</summary>
    public StaffActivity ActivityOf(string moderator, TimeSpan window, DateTimeOffset? now = null)
    {
        var since = (now ?? DateTimeOffset.UtcNow) - window;
        var query = new EventQuery(GroupLimit, since, Actor: moderator);

        var actions = events.CountBy(EventGrouping.Kind, query);
        var perHour = events.CountBy(EventGrouping.Hour, query);
        var busiest = perHour.Count > 0 ? perHour.MaxBy(h => h.Count) : default;

        return new StaffActivity(
            moderator,
            actions.Sum(a => a.Count),
            actions,
            perHour.Count > 0 ? (busiest.Key, busiest.Count) : null);
    }
}
