using System.Globalization;

namespace PavlovBot.Core.Network;

/// <summary>How hard the response to repeated detections may go.</summary>
/// <param name="AutoBan">Whether the planner may ban at all. Off, it only alerts.</param>
/// <param name="EscalateAfter">Detection minutes within <paramref name="Window"/> before a ban.</param>
/// <param name="Window">How far back detections of one source are counted together, and one alert covers.</param>
/// <param name="BaseBan">The first ban's length; each repeat within a day doubles it.</param>
/// <param name="LongestBan">The cap on a doubled ban.</param>
/// <param name="MaxBansPerHour">The most bans the planner hands out in an hour, however many sources qualify.</param>
/// <param name="SpoofSources">Sources in one window above which per-address bans stop: the flood is likely spoofed.</param>
public sealed record ResponsePolicy(
    bool AutoBan = true,
    int EscalateAfter = 3,
    TimeSpan? Window = null,
    TimeSpan? BaseBan = null,
    TimeSpan? LongestBan = null,
    int MaxBansPerHour = 30,
    int SpoofSources = 5000)
{
    public TimeSpan CorrelationWindow => Window ?? TimeSpan.FromMinutes(10);

    public TimeSpan FirstBan => BaseBan ?? TimeSpan.FromHours(1);

    public TimeSpan Cap => LongestBan ?? TimeSpan.FromDays(7);
}

/// <summary>What to do about a source this window.</summary>
/// <param name="Alert">True when this starts an incident: the source had been quiet for a whole correlation window. One alert per incident, not per minute.</param>
/// <param name="Ban">A ban to place, or null.</param>
/// <param name="Held">Why a ban that would otherwise be placed was not, for the record.</param>
public sealed record PlannedResponse(string Source, IReadOnlyList<Detection> Detections, bool Alert, TimeSpan? Ban, string? Held, string Incident);

/// <summary>
/// Graduated response: record every detection, alert once per incident, and ban only on
/// evidence that repeats.
/// </summary>
/// <remarks>
/// THE SAFEGUARDS, because a source address on a UDP packet is whatever the sender wrote:
///   - A ban needs <see cref="ResponsePolicy.EscalateAfter"/> separate minutes of detections
///     within the window. One bad minute is an alert.
///   - Players and trusted addresses are never banned by the planner, whatever they did. An
///     attacker spoofing a player's address cannot get that player banned.
///   - When a window has more distinct sources than <see cref="ResponsePolicy.SpoofSources"/>, the
///     flood is likely spoofed and per-address bans would land on whoever's addresses were
///     forged; they are held, and the firewall's total cap does the work.
///   - At most <see cref="ResponsePolicy.MaxBansPerHour"/> bans an hour.
///   - Single addresses only: the planner never bans a range.
///   - Bans expire: the first is <see cref="ResponsePolicy.FirstBan"/>, each repeat within a day
///     doubles it up to <see cref="ResponsePolicy.Cap"/>.
/// The firewall's own per-address block during an attack is separate and immediate; this is
/// the slower path for sources that keep coming back.
///
/// IN MEMORY. A restart forgets the counts, which can only make the next ban later, never sooner.
/// </remarks>
public sealed class ResponsePlanner(ResponsePolicy policy)
{
    private readonly Dictionary<string, List<DateTimeOffset>> _minutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Last, string Id)> _incidents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _bans = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recentBans = new();

    public ResponsePolicy Policy { get; set; } = policy;

    /// <summary>Plan the responses to one window's detections.</summary>
    /// <param name="sourcesInWindow">Distinct sources seen this window, for the spoofing guard.</param>
    /// <param name="exempt">Whether an address is a player or trusted, and so never banned.</param>
    public IReadOnlyList<PlannedResponse> Plan(
        DateTimeOffset now, IReadOnlyList<Detection> detections, int sourcesInWindow, Func<string, bool> exempt)
    {
        ArgumentNullException.ThrowIfNull(detections);
        ArgumentNullException.ThrowIfNull(exempt);
        Expire(now);

        var plans = new List<PlannedResponse>();
        foreach (var group in detections.GroupBy(d => d.Source, StringComparer.Ordinal))
        {
            var source = group.Key;
            var list = group.ToList();

            var alert = false;
            if (!_incidents.TryGetValue(source, out var incident))
            {
                incident = (now, string.Create(CultureInfo.InvariantCulture, $"src-{now.UtcDateTime:yyyyMMddHHmm}-{source}"));
                alert = true;
            }
            // Quiet for a whole window ends an incident; while it keeps going it is one incident.
            _incidents[source] = (now, incident.Id);

            TimeSpan? ban = null;
            string? held = null;
            if (list.Any(d => d.Escalates))
            {
                var minutes = _minutes.TryGetValue(source, out var m) ? m : _minutes[source] = [];
                minutes.Add(now);

                if (minutes.Count >= Policy.EscalateAfter)
                {
                    held = !Policy.AutoBan ? "automatic bans are off"
                        : exempt(source) ? "a player or trusted address is never banned automatically"
                        : sourcesInWindow > Policy.SpoofSources
                            ? string.Create(CultureInfo.InvariantCulture, $"{sourcesInWindow} sources this minute - likely spoofed, so no per-address bans")
                        : _recentBans.Count >= Policy.MaxBansPerHour
                            ? string.Create(CultureInfo.InvariantCulture, $"{Policy.MaxBansPerHour} automatic bans in the last hour already")
                        : null;

                    if (held is null)
                    {
                        var previous = _bans.TryGetValue(source, out var b) ? b : _bans[source] = [];
                        ban = Length(previous.Count);
                        previous.Add(now);
                        _recentBans.Enqueue(now);
                        minutes.Clear();
                    }
                }
            }

            plans.Add(new PlannedResponse(source, list, alert, ban, held, incident.Id));
        }
        return plans;
    }

    /// <summary>The ban for a source banned <paramref name="previous"/> times in the last day.</summary>
    public TimeSpan Length(int previous)
    {
        var length = Policy.FirstBan * Math.Pow(2, Math.Min(previous, 16));
        return length > Policy.Cap ? Policy.Cap : length;
    }

    private void Expire(DateTimeOffset now)
    {
        var window = Policy.CorrelationWindow;
        foreach (var source in _minutes.Keys.ToList())
        {
            _minutes[source].RemoveAll(t => now - t > window);
            if (_minutes[source].Count == 0) _minutes.Remove(source);
        }
        foreach (var source in _incidents.Where(i => now - i.Value.Last > window).Select(i => i.Key).ToList())
            _incidents.Remove(source);
        foreach (var source in _bans.Keys.ToList())
        {
            _bans[source].RemoveAll(t => now - t > TimeSpan.FromDays(1));
            if (_bans[source].Count == 0) _bans.Remove(source);
        }
        while (_recentBans.Count > 0 && now - _recentBans.Peek() > TimeSpan.FromHours(1)) _recentBans.Dequeue();
    }
}
