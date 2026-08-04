using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Economy;

/// <param name="Amount">The credit, in the game's currency.</param>
public sealed record Earning(long Amount, DateTimeOffset At);

/// <summary>One player's recent credits.</summary>
public sealed record EarningWindow
{
    private readonly IReadOnlyList<Earning> _entries = [];

    /// <remarks>
    /// The coalesce is in the SETTER, not an initializer. This dataset is written by a
    /// process that may not be this one, and a row missing "entries" deserializes to null
    /// however the property is declared - the same trap that produced the log-tail crash.
    /// </remarks>
    public IReadOnlyList<Earning> Entries
    {
        get => _entries;
        init => _entries = value ?? [];
    }

    /// <summary>When this player was last flagged, so one spree is not reported every tick.</summary>
    public DateTimeOffset? LastAlert { get; init; }
}

/// <param name="Total">Earned inside the window - the number compared against the threshold.</param>
/// <param name="Events">How many separate credits made it up. One huge credit reads differently to fifty.</param>
public sealed record MoneyAlert(string Player, long Total, int Events, TimeSpan Window, DateTimeOffset At);

/// <summary>
/// Flags a player who earns more than a threshold inside a window.
/// </summary>
/// <remarks>
/// WHAT THIS CATCHES. <see cref="MoneyLog"/> already computes a per-player delta every tick
/// and posts it to a feed nobody reads at 4am. A player going from 4,000 to 1,004,000 is a
/// dupe, a mis-set payout, or a staff member with a fat finger - all three are worth knowing
/// about within the minute, and none of them are visible in a scrolling feed.
///
/// A SLIDING WINDOW, NOT A PER-TICK THRESHOLD. The exploit worth catching is rarely one huge
/// credit; it is a hundred small ones in ten minutes, each individually unremarkable. Summing
/// over a window catches both, and the event count in the alert tells staff which they are
/// looking at.
///
/// CREDITS ONLY. Spending is not suspicious and netting the two off would let somebody hide a
/// million in income behind a million in outgoings - which is precisely what laundering looks
/// like.
///
/// THE WINDOW IS PERSISTED. A detector that forgets on restart is one a deploy silently
/// disarms, and this bot gets deployed a lot.
///
/// RE-ALERT COOLDOWN. Once flagged, a player is not flagged again until their window clears
/// or the cooldown passes - otherwise a single spree posts an alert on every tick for as long
/// as it stays inside the window, and staff learn to ignore the channel.
/// </remarks>
public sealed class MoneyAnomalyDetector(
    SerializedStore store,
    ILogger<MoneyAnomalyDetector> logger,
    long threshold,
    TimeSpan window,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Zero or negative disables it, which is the default until somebody picks a number.</summary>
    public bool Enabled => threshold > 0 && window > TimeSpan.Zero;

    public long Threshold => threshold;
    public TimeSpan Window => window;

    /// <summary>How long after an alert before the same player can be flagged again.</summary>
    public static TimeSpan AlertCooldown { get; } = TimeSpan.FromMinutes(30);

    private Dictionary<string, EarningWindow> Load() =>
        store.Read(Datasets.MoneyWindow, new Dictionary<string, EarningWindow>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Feed one tick's changes in and get back whoever crossed the line.
    /// </summary>
    /// <param name="changes">Straight from <see cref="MoneyLog.TickAsync"/>.</param>
    public async Task<IReadOnlyList<MoneyAlert>> ObserveAsync(
        IReadOnlyCollection<(string Player, long Delta)> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!Enabled) return [];

        var now = _time.GetUtcNow();
        var cutoff = now - window;
        var alerts = new List<MoneyAlert>();

        await store.UpdateAsync(Datasets.MoneyWindow,
            new Dictionary<string, EarningWindow>(StringComparer.OrdinalIgnoreCase),
            windows =>
            {
                foreach (var (player, delta) in changes)
                {
                    if (delta <= 0) continue;   // credits only - see the class remarks

                    var existing = windows.GetValueOrDefault(player) ?? new EarningWindow();

                    // Prune as we go. Doing it only on write is what turns a rolling window
                    // into a permanently growing one for a player who earns constantly.
                    var kept = existing.Entries
                        .Where(e => e is not null && e.At > cutoff)
                        .Append(new Earning(delta, now))
                        .ToList();

                    var total = kept.Sum(e => e.Amount);

                    var cooling = existing.LastAlert is { } last && now - last < AlertCooldown;
                    var fired = total >= threshold && !cooling;

                    windows[player] = new EarningWindow
                    {
                        Entries = kept,
                        LastAlert = fired ? now : existing.LastAlert,
                    };

                    if (fired) alerts.Add(new MoneyAlert(player, total, kept.Count, window, now));
                }

                /* Sweep every OTHER player's expired entries too, so a player who earned once
                   and left does not keep a row forever. Cheap: this dataset is one small list
                   per player who has earned inside the window. */
                foreach (var key in windows.Keys.ToList())
                {
                    var entry = windows[key];
                    if (entry is null) { windows.Remove(key); continue; }

                    var live = entry.Entries.Where(e => e is not null && e.At > cutoff).ToList();
                    var recentlyAlerted = entry.LastAlert is { } at && now - at < AlertCooldown;

                    if (live.Count == 0 && !recentlyAlerted) windows.Remove(key);
                    else if (live.Count != entry.Entries.Count) windows[key] = entry with { Entries = live };
                }

                return windows;
            }, ct).ConfigureAwait(false);

        foreach (var alert in alerts)
        {
            logger.LogWarning(
                "MONEY ANOMALY: {Player} earned {Total:N0} across {Events} credit(s) in {Minutes} minutes " +
                "(threshold {Threshold:N0})",
                alert.Player, alert.Total, alert.Events, (int)window.TotalMinutes, threshold);
        }

        return alerts;
    }

    /// <summary>What a player has earned inside the window right now. For <c>/inspect</c>-style lookups.</summary>
    public long EarnedRecently(string player)
    {
        if (!Enabled) return 0;
        var cutoff = _time.GetUtcNow() - window;
        var entry = Load().GetValueOrDefault(player);
        return entry?.Entries.Where(e => e is not null && e.At > cutoff).Sum(e => e.Amount) ?? 0;
    }
}
