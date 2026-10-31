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
/// <param name="SevereFactor">A detection this many times over its threshold is banned in its first minute.</param>
/// <param name="PermanentInAttack">During a flood, a source detected in it is banned at once and permanently.</param>
/// <param name="AttackBansPerHour">The hourly cap while a flood is on, when a botnet's worth of sources may qualify.</param>
public sealed record ResponsePolicy(
    bool AutoBan = true,
    int EscalateAfter = 3,
    TimeSpan? Window = null,
    TimeSpan? BaseBan = null,
    TimeSpan? LongestBan = null,
    int MaxBansPerHour = 30,
    int SpoofSources = 5000,
    double SevereFactor = 5,
    bool PermanentInAttack = true,
    int AttackBansPerHour = 300)
{
    /// <summary>A permanent ban's length: ten years, as CrowdSec has no "forever".</summary>
    public static readonly TimeSpan Permanent = TimeSpan.FromDays(3650);

    public TimeSpan CorrelationWindow => Window ?? TimeSpan.FromMinutes(10);

    public TimeSpan FirstBan => BaseBan ?? TimeSpan.FromHours(1);

    public TimeSpan Cap => LongestBan ?? TimeSpan.FromDays(7);
}

/// <summary>What to do about a source this window.</summary>
/// <param name="Alert">True when this starts an incident: the source had been quiet for a whole correlation window. One alert per incident, not per minute.</param>
/// <param name="Ban">A ban to place, or null.</param>
/// <param name="Held">Why a ban that would otherwise be placed was not, for the record.</param>
/// <param name="Permanent">The ban is permanent: the source was detected during a flood.</param>
public sealed record PlannedResponse(string Source, IReadOnlyList<Detection> Detections, bool Alert, TimeSpan? Ban, string? Held, string Incident,
    bool Permanent = false);

/// <summary>
/// Graduated response: record every detection, alert once per incident, and ban only on
/// evidence that repeats.
/// </summary>
/// <remarks>
/// THE SAFEGUARDS, because a source address on a UDP packet is whatever the sender wrote:
///   - A ban needs <see cref="ResponsePolicy.EscalateAfter"/> separate minutes of detections
///     within the window. One bad minute is an alert - unless it was SEVERE, at least
///     <see cref="ResponsePolicy.SevereFactor"/> times over its threshold for the whole minute
///     (a sustained 1,500 UDP packets a second from a non-player, 600 connections a minute),
///     which no legitimate client does; that is banned at once, under every rule below.
///   - Players and trusted addresses are never banned by the planner, whatever they did. An
///     attacker spoofing a player's address cannot get that player banned.
///   - When a window has more distinct sources than <see cref="ResponsePolicy.SpoofSources"/>, the
///     flood is likely spoofed and per-address bans would land on whoever's addresses were
///     forged; they are held, and the firewall's total cap does the work.
///   - At most <see cref="ResponsePolicy.MaxBansPerHour"/> bans an hour.
///   - Single addresses only: the planner never bans a range.
///   - Bans expire: the first is <see cref="ResponsePolicy.FirstBan"/>, each repeat within a day
///     doubles it up to <see cref="ResponsePolicy.Cap"/>.
///
/// DURING A FLOOD (<see cref="ResponsePolicy.PermanentInAttack"/>) a source detected in it - over
/// the UDP rate, a SYN burst, a port scan, or blocked by the firewall's per-address limit - is
/// banned in that first minute, permanently. Every guard above still holds: never a player or a
/// trusted address, nothing when the flood looks spoofed, never a range. Only the hourly cap is
/// raised, to <see cref="ResponsePolicy.AttackBansPerHour"/>, because a botnet is hundreds of
/// sources. The risk this accepts: a botnet's addresses are mostly home connections, reassigned
/// to someone else in days or weeks, so a permanent ban can outlive its attacker. /antiddos unban
/// lifts one.
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

    /// <summary>Permanent bans this planner placed, until CrowdSec's own list shows them (one correlation window).</summary>
    private readonly Dictionary<string, DateTimeOffset> _placedPermanent = new(StringComparer.Ordinal);

    public ResponsePolicy Policy { get; set; } = policy;

    /// <summary>Plan the responses to one window's detections.</summary>
    /// <param name="sourcesInWindow">Distinct sources seen this window, for the spoofing guard.</param>
    /// <param name="exempt">Whether an address is a player or trusted, and so never banned.</param>
    /// <param name="inAttack">Whether a flood is on (or just ended): detections then are part of it.</param>
    /// <param name="bannedForGood">Whether an address is already under a permanent ban, so is not banned again.</param>
    public IReadOnlyList<PlannedResponse> Plan(
        DateTimeOffset now, IReadOnlyList<Detection> detections, int sourcesInWindow, Func<string, bool> exempt, bool inAttack = false,
        Func<string, bool>? bannedForGood = null)
    {
        var permanent = inAttack && Policy.PermanentInAttack;
        var hourlyCap = permanent ? Math.Max(Policy.MaxBansPerHour, Policy.AttackBansPerHour) : Policy.MaxBansPerHour;
        ArgumentNullException.ThrowIfNull(detections);
        ArgumentNullException.ThrowIfNull(exempt);
        Expire(now);

        var plans = new List<PlannedResponse>();
        foreach (var group in detections.GroupBy(d => d.Source, StringComparer.Ordinal))
        {
            var source = group.Key;
            var list = group.ToList();

            // Still counted while the bouncer drops it (the bot's accounting runs first): already
            // banned for good is neither a new ban every minute nor a new alert.
            if (_placedPermanent.ContainsKey(source) || bannedForGood?.Invoke(source) == true)
            {
                _incidents[source] = (now, _incidents.TryGetValue(source, out var open) ? open.Id
                    : string.Create(CultureInfo.InvariantCulture, $"src-{now.UtcDateTime:yyyyMMddHHmm}-{source}"));
                continue;
            }

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

                var severe = list.Any(d => d.Escalates && d.Threshold > 0 && d.Observed >= d.Threshold * Policy.SevereFactor);
                if (minutes.Count >= Policy.EscalateAfter || severe || permanent)
                {
                    held = !Policy.AutoBan ? "automatic bans are off"
                        : exempt(source) ? "a player or trusted address is never banned automatically"
                        : sourcesInWindow > Policy.SpoofSources
                            ? string.Create(CultureInfo.InvariantCulture, $"{sourcesInWindow} sources this minute - likely spoofed, so no per-address bans")
                        : _recentBans.Count >= hourlyCap
                            ? string.Create(CultureInfo.InvariantCulture, $"{hourlyCap} automatic bans in the last hour already")
                        : null;

                    if (held is null)
                    {
                        var previous = _bans.TryGetValue(source, out var b) ? b : _bans[source] = [];
                        ban = permanent ? ResponsePolicy.Permanent : Length(previous.Count);
                        if (permanent) _placedPermanent[source] = now;
                        previous.Add(now);
                        _recentBans.Enqueue(now);
                        minutes.Clear();
                    }
                }
            }

            plans.Add(new PlannedResponse(source, list, alert, ban, held, incident.Id, ban is not null && permanent));
        }
        return plans;
    }

    /// <summary>
    /// Remember a ban placed before a restart, so a repeat offender's next ban still doubles.
    /// </summary>
    public void RememberBan(string source, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(source);
        var previous = _bans.TryGetValue(source, out var b) ? b : _bans[source] = [];
        previous.Add(at);
    }

    /// <summary>Forget a source's bans, after it was unbanned by hand, so a later flood can ban it again.</summary>
    public void Forget(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _placedPermanent.Remove(source);
        _bans.Remove(source);
        _minutes.Remove(source);
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
        foreach (var source in _placedPermanent.Where(p => now - p.Value > window).Select(p => p.Key).ToList())
            _placedPermanent.Remove(source);
    }
}
