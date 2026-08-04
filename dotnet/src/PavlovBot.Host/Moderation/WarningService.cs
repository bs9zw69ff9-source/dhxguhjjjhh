using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Text;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <param name="Id">Short, stable, and shown - so one warning can be removed without clearing the record.</param>
public sealed record PlayerWarning(string Id, string Player, string Reason, string Moderator, DateTimeOffset At);

/// <summary>
/// What issuing a warning led to.
/// </summary>
/// <param name="Active">Warnings inside the decay window - the number escalation is judged on.</param>
/// <param name="Total">Every warning on record, including decayed ones.</param>
/// <param name="Escalated">True when this warning triggered the automatic ban.</param>
public sealed record WarningOutcome(
    PlayerWarning Warning, int Active, int Total, bool Escalated);

/// <summary>
/// Warnings, and the automatic punishment that follows enough of them.
/// </summary>
/// <remarks>
/// WHY WARNINGS DECAY BUT ARE NEVER DELETED. A player who earned three warnings across two
/// years is not the player who earned three in a night, and a counter that only goes up
/// eventually bans everyone who has ever played. So escalation is judged on warnings inside
/// a window, while the full history stays on record for staff to read - the decision and the
/// evidence are different questions and want different retentions.
///
/// ONE RULE, NOT A LADDER. Three active warnings is a one-day ban. There is no kick step and
/// no longer step above it, because a tiered ladder answers a question nobody asked: every
/// extra rung is another threshold to explain to a player and another way for the counter to
/// surprise a moderator. Nothing here ever issues a permanent ban - that stays a decision a
/// person makes while looking at what happened.
///
/// THE PUNISHMENT IS NOT APPLIED HERE. This decides; the caller enforces. Ban enforcement
/// needs RCON, the ban list and the game's own banlist file, and putting that behind a
/// storage class would make warnings untestable without a server.
/// </remarks>
public sealed class WarningService(
    SerializedStore store, AuditLog audit, ILogger<WarningService> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// How long a warning counts toward escalation.
    /// </summary>
    /// <remarks>
    /// Thirty days is long enough that a bad week accumulates and short enough that a bad
    /// night two years ago does not. It is not a deletion - see the class remarks.
    /// </remarks>
    public static TimeSpan DecayWindow { get; } = TimeSpan.FromDays(30);

    /// <summary>Active warnings that trigger the automatic ban.</summary>
    public static int EscalationThreshold { get; } = 3;

    /// <summary>How long the automatic ban lasts.</summary>
    public static TimeSpan EscalationBan { get; } = TimeSpan.FromDays(1);

    private Dictionary<string, List<PlayerWarning>> Load() =>
        store.Read(Datasets.Warnings, new Dictionary<string, List<PlayerWarning>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Every warning on record for a player, oldest first.</summary>
    public IReadOnlyList<PlayerWarning> For(string player) =>
        Load().GetValueOrDefault(player.Trim()) is { } list
            ? list.Where(w => w is not null).OrderBy(w => w.At).ToList()
            : [];

    /// <summary>The warnings that still count toward escalation.</summary>
    public IReadOnlyList<PlayerWarning> ActiveFor(string player)
    {
        var cutoff = _time.GetUtcNow() - DecayWindow;
        return For(player).Where(w => w.At > cutoff).ToList();
    }

    /// <summary>
    /// Record a warning and report what it triggered.
    /// </summary>
    /// <remarks>
    /// The escalation decision is made from the list AS WRITTEN, inside the same update, so
    /// two moderators warning the same player at once cannot both read "2 active" and both
    /// decide nothing happens.
    /// </remarks>
    public async Task<WarningOutcome> WarnAsync(
        string player, string reason, string moderator, CancellationToken ct = default)
    {
        var name = Sanitize.Id(player);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var now = _time.GetUtcNow();
        var warning = new PlayerWarning(
            NewId(), name, Sanitize.Message(reason), Sanitize.Message(moderator), now);

        var active = 0;
        var total = 0;

        await store.UpdateAsync(Datasets.Warnings,
            new Dictionary<string, List<PlayerWarning>>(StringComparer.OrdinalIgnoreCase),
            warnings =>
            {
                var list = warnings.TryGetValue(name, out var existing) && existing is not null
                    ? existing.Where(w => w is not null).ToList()
                    : [];

                list.Add(warning);
                warnings[name] = list;

                var cutoff = now - DecayWindow;
                active = list.Count(w => w.At > cutoff);
                total = list.Count;
                return warnings;
            }, ct).ConfigureAwait(false);

        /* FIRES ON THE CROSSING, NOT ON EVERY WARNING PAST IT. `active == threshold` rather
           than `>=`, so a fourth and fifth warning inside the window do not each issue a
           fresh day. Without that, a player at the threshold is re-banned for a day every
           time anybody warns them - which is a permanent ban assembled one day at a time. */
        var escalated = active == EscalationThreshold;

        await audit.RecordAsync("warn", moderator, name,
            $"{warning.Reason} (warning {active} of {EscalationThreshold})", ct).ConfigureAwait(false);

        if (escalated)
        {
            logger.LogWarning("{Player} reached {Active} active warning(s) - escalating to a {Hours}h ban",
                name, active, (int)EscalationBan.TotalHours);
        }

        return new WarningOutcome(warning, active, total, escalated);
    }

    /// <summary>
    /// Remove ONE warning by id.
    /// </summary>
    /// <returns>False when no warning with that id exists for that player.</returns>
    public async Task<bool> RemoveAsync(string player, string id, CancellationToken ct = default)
    {
        var name = Sanitize.Id(player);
        var removed = false;

        await store.UpdateAsync(Datasets.Warnings,
            new Dictionary<string, List<PlayerWarning>>(StringComparer.OrdinalIgnoreCase),
            warnings =>
            {
                if (!warnings.TryGetValue(name, out var list) || list is null) return null;

                var kept = list.Where(w => w is not null &&
                    !string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();

                if (kept.Count == list.Count) return null;   // veto: nothing to write

                removed = true;
                if (kept.Count == 0) warnings.Remove(name); else warnings[name] = kept;
                return warnings;
            }, ct).ConfigureAwait(false);

        if (removed) await audit.RecordAsync("warn-remove", "staff", name, $"warning {id}", ct).ConfigureAwait(false);
        return removed;
    }

    /// <summary>Clear a player's whole record. Returns how many went.</summary>
    public async Task<int> ClearAsync(string player, string moderator, CancellationToken ct = default)
    {
        var name = Sanitize.Id(player);
        var cleared = 0;

        await store.UpdateAsync(Datasets.Warnings,
            new Dictionary<string, List<PlayerWarning>>(StringComparer.OrdinalIgnoreCase),
            warnings =>
            {
                if (!warnings.TryGetValue(name, out var list) || list is null || list.Count == 0) return null;
                cleared = list.Count;
                warnings.Remove(name);
                return warnings;
            }, ct).ConfigureAwait(false);

        if (cleared > 0)
            await audit.RecordAsync("warn-clear", moderator, name, $"{cleared} warning(s)", ct).ConfigureAwait(false);

        return cleared;
    }

    /// <summary>Players with at least one active warning, most first.</summary>
    public IReadOnlyList<(string Player, int Active, int Total)> Standings()
    {
        var cutoff = _time.GetUtcNow() - DecayWindow;

        return Load()
            .Where(kv => kv.Value is { Count: > 0 })
            .Select(kv => (
                Player: kv.Key,
                Active: kv.Value.Count(w => w is not null && w.At > cutoff),
                Total: kv.Value.Count(w => w is not null)))
            .Where(r => r.Active > 0)
            .OrderByDescending(r => r.Active).ThenByDescending(r => r.Total)
            .ToList();
    }

    /// <summary>Warnings still to go before the ban, or null once they are past it.</summary>
    /// <remarks>
    /// Shown on the reply so a warning is a decision rather than a note nobody revisits - the
    /// moderator can see they are one away from banning somebody before they press it.
    /// </remarks>
    public static int? WarningsUntilBan(int active) =>
        active < EscalationThreshold ? EscalationThreshold - active : null;

    // Short enough to type back in a command, wide enough not to collide within one player.
    private static string NewId() => Guid.NewGuid().ToString("N")[..6];
}
