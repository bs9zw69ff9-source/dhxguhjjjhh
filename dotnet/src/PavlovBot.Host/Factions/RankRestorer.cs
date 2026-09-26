using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Factions;

/// <summary>
/// Puts members back at their rank when a <c>/suspendrank</c> runs out.
/// </summary>
/// <remarks>
/// THE RANK IS SET, NOT CLIMBED TO. This used to re-add the player and then promote them one step
/// at a time from the default rank, whatever rank they actually held. Three failures came of that:
///
///   A member re-whitelisted by staff during the suspension was promoted the full distance again
///   from where they already were - past the rank they were suspended from.
///
///   A member who had joined another faction was reported restored, while nothing was.
///
///   Suspensions were cleared only after the whole batch, so one exception part-way through made
///   the next tick, thirty seconds later, promote everybody before it again - every tick, until
///   they reached the top of the ladder.
///
/// Now each suspension is settled on its own and cleared as soon as it is, the target rank is
/// set directly, and a member already at or above it is left alone.
/// </remarks>
public sealed class RankRestorer(SerializedStore store, RosterService rosters, ILogger logger)
{
    /// <summary>Restore every suspension that has run out. Returns how many were settled.</summary>
    public async Task<int> RestoreExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var suspensions = store.Read(Datasets.RankSuspensions,
            new Dictionary<string, RankSuspension>(StringComparer.OrdinalIgnoreCase));

        var settled = 0;
        foreach (var suspension in suspensions.Values.Where(s => s.Until <= now).ToList())
        {
            bool done;
            try
            {
                done = await RestoreAsync(suspension, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not restore {Player}'s rank yet - will retry", suspension.Player);
                continue;
            }

            if (!done) continue;
            await ClearAsync(suspension, ct).ConfigureAwait(false);
            settled++;
        }

        return settled;
    }

    /// <summary>
    /// Restore one member. True when the suspension is finished with (restored, or can never be);
    /// false when the roster could not be written and it should be tried again.
    /// </summary>
    private async Task<bool> RestoreAsync(RankSuspension suspension, CancellationToken ct)
    {
        if (rosters.Factions.Get(suspension.Faction) is not { } faction)
        {
            logger.LogWarning("Rank suspension for {Player}: faction {Faction} no longer exists - dropped",
                suspension.Player, suspension.Faction);
            return true;
        }

        var membership = await rosters.FindAsync(suspension.Player, ct).ConfigureAwait(false);

        if (membership is not null &&
            !string.Equals(membership.Faction.Name, faction.Name, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Rank suspension served, but {Player} is now in {Other}, not {Faction} - NOT restored. " +
                "Move them back by hand if they should be", suspension.Player, membership.Faction.Name, faction.Name);
            return true;
        }

        var current = membership?.Rank;
        if (membership is null)
        {
            var joined = await rosters.JoinAsync(faction, suspension.Player, ct).ConfigureAwait(false);
            if (!joined.IsAllowed) return Settle(joined, suspension);
            current = joined.Rank ?? faction.Default;
        }

        // Already at or above where they were: staff have dealt with it; do not stack a promotion.
        if (faction.IndexOf(current) >= faction.IndexOf(suspension.RestoreTo))
        {
            logger.LogInformation("Rank suspension served: {Player} is already {Rank}, left as is",
                suspension.Player, current);
            return true;
        }

        var set = await rosters.SetRankAsync(faction, suspension.Player, suspension.RestoreTo, ct).ConfigureAwait(false);
        if (!set.IsAllowed) return Settle(set, suspension);

        logger.LogInformation("Rank suspension served: {Player} restored to {Rank}", suspension.Player, suspension.RestoreTo);
        return true;
    }

    /// <summary>A refusal: retry the ones the roster files caused, give up on the rest.</summary>
    private bool Settle(MembershipDecision decision, RankSuspension suspension)
    {
        if (decision.Outcome is MembershipOutcome.RosterUnavailable or MembershipOutcome.WriteFailed)
        {
            logger.LogWarning("Could not restore {Player} to {Rank} ({Outcome}) - will retry",
                suspension.Player, suspension.RestoreTo, decision.Outcome);
            return false;
        }

        logger.LogWarning("Could not restore {Player} to {Rank}: {Outcome}. Needs a manual promotion",
            suspension.Player, suspension.RestoreTo, decision.Outcome);
        return true;
    }

    /// <summary>Remove this suspension - only if it is still the same one, not a newer re-suspension.</summary>
    private Task ClearAsync(RankSuspension suspension, CancellationToken ct) =>
        store.UpdateAsync(Datasets.RankSuspensions,
            new Dictionary<string, RankSuspension>(StringComparer.OrdinalIgnoreCase),
            current =>
            {
                if (current.TryGetValue(suspension.Player, out var stored) && stored.Until == suspension.Until)
                    current.Remove(suspension.Player);
                return current;
            }, ct);
}
