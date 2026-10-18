using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Factions;

/// <summary>
/// Re-files records kept under a faction's former name (NYPD) under its current one (GWPD).
/// </summary>
/// <remarks>
/// A FACTION'S NAME IS A KEY IN FOUR DATASETS: the Discord-to-player membership index, rank
/// suspensions, delegated roster roles and the payroll clock. Renaming the faction in code
/// leaves every one of those filed under a name nothing looks up any more. Nobody loses game
/// access - the roster FILES keep their names and are the source of truth - but /promotion
/// by user stops finding members, a served suspension is never restored, a delegated role
/// stops granting anything and the pay clock restarts. All of it silently.
///
/// ONCE, AT STARTUP, AND IDEMPOTENT. After the first run there is nothing under the old name,
/// every mutator vetoes, and nothing is written. An instance whose factions declare no former
/// names - the Fallout bot - does not touch a single dataset.
///
/// THE CURRENT NAME WINS A COLLISION. A delegated role already set under the new name was set
/// deliberately, after the rename, and is kept over the one inherited from the old name.
///
/// NOTHING HERE THROWS. A dataset that cannot be rewritten is logged and left exactly as it
/// was - <see cref="SerializedStore"/> refuses to write over unreadable data - and the bot
/// starts anyway, because a stale index is a bookkeeping problem and a bot that will not start
/// is an outage.
/// </remarks>
public static class FactionRenames
{
    /// <summary>Carry every renamed faction's records over. Returns how many datasets changed.</summary>
    public static async Task<int> CarryOverAsync(
        SerializedStore store, FactionSet factions, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factions);
        ArgumentNullException.ThrowIfNull(logger);

        var renames = factions.Renames;
        if (renames.Count == 0) return 0;

        var changed = 0;

        changed += await ApplyAsync(logger, Datasets.FactionMembers, () =>
            store.UpdateMapAsync(Datasets.FactionMembers,
                new Dictionary<string, FactionMember>(StringComparer.Ordinal),
                map => Changed(RenameValues(map, renames, m => m.Faction, (m, now) => m with { Faction = now }), map),
                ct), renames).ConfigureAwait(false);

        changed += await ApplyAsync(logger, Datasets.RankSuspensions, () =>
            store.UpdateMapAsync(Datasets.RankSuspensions,
                new Dictionary<string, RankSuspension>(StringComparer.OrdinalIgnoreCase),
                map => Changed(RenameValues(map, renames, s => s.Faction, (s, now) => s with { Faction = now }), map),
                ct), renames).ConfigureAwait(false);

        changed += await ApplyAsync(logger, Datasets.Roles, () =>
            store.UpdateAsync(Datasets.Roles, RoleMap.Empty, roles =>
            {
                var map = new Dictionary<string, ulong>(roles.FactionRoles, StringComparer.OrdinalIgnoreCase);
                return RenameKeys(map, renames) > 0 ? roles with { FactionRoles = map } : null;
            }, ct), renames).ConfigureAwait(false);

        changed += await ApplyAsync(logger, Datasets.PayrollState, () =>
            store.UpdateAsync(Datasets.PayrollState, PayrollState.New(), state =>
                RenameKeys(state.LastPaid, renames) + RenameKeys(state.Previous, renames) > 0 ? state : null,
                ct), renames).ConfigureAwait(false);

        return changed;
    }

    /// <summary>
    /// Re-point every value filed under a former name. Returns how many moved.
    /// </summary>
    internal static int RenameValues<T>(
        Dictionary<string, T> map,
        IReadOnlyDictionary<string, string> renames,
        Func<T, string> faction,
        Func<T, string, T> refile)
    {
        var moved = 0;
        foreach (var key in map.Keys.ToList())
        {
            if (!renames.TryGetValue(faction(map[key]) ?? "", out var now)) continue;
            map[key] = refile(map[key], now);
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Move every entry KEYED by a former name to the current one. The current name's own
    /// entry, when there is one, wins and the old one is dropped. Returns how many moved.
    /// </summary>
    internal static int RenameKeys<T>(Dictionary<string, T> map, IReadOnlyDictionary<string, string> renames)
    {
        var moved = 0;
        foreach (var key in map.Keys.ToList())
        {
            if (!renames.TryGetValue(key, out var now)) continue;
            if (!map.ContainsKey(now)) map[now] = map[key];
            map.Remove(key);
            moved++;
        }
        return moved;
    }

    private static Dictionary<string, T>? Changed<T>(int moved, Dictionary<string, T> map) =>
        moved > 0 ? map : null;

    private static async Task<int> ApplyAsync<T>(
        ILogger logger, string dataset, Func<Task<UpdateResult<T>>> update,
        IReadOnlyDictionary<string, string> renames)
    {
        try
        {
            var result = await update().ConfigureAwait(false);
            if (result.Ok)
            {
                logger.LogInformation("Faction rename: {Dataset} re-filed from {Renames}",
                    dataset, string.Join(", ", renames.Select(r => $"{r.Key} -> {r.Value}")));
                return 1;
            }

            if (!result.Vetoed)
            {
                logger.LogWarning("Faction rename: {Dataset} was NOT re-filed - {Error}. Records under {Old} " +
                    "will not be found until it is", dataset, result.Error, string.Join(", ", renames.Keys));
            }
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Faction rename: {Dataset} could not be re-filed", dataset);
            return 0;
        }
    }
}
