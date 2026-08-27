namespace PavlovBot.Core.Provisioning;

/// <summary>The server layout the bot is currently running, read from configuration.</summary>
/// <param name="RconIndices">The RCON slot numbers in use, e.g. <c>[1, 2]</c>. May arrive unsorted.</param>
/// <param name="Units">The explicit <c>PAVLOV_UNITS</c> entries, in order. Empty when unset.</param>
/// <param name="Bases">The explicit <c>PAVLOV_BASES</c> entries, in order. Empty when unset.</param>
public sealed record ServerLayout(
    IReadOnlyList<int> RconIndices,
    IReadOnlyList<string> Units,
    IReadOnlyList<string> Bases);

/// <summary>Where a new server slots in, and the full lists once it has.</summary>
/// <param name="Slot">The 1-based number the new server takes: <c>server{Slot}</c>.</param>
/// <param name="FinalUnits">The complete <c>PAVLOV_UNITS</c> list with the new unit appended.</param>
/// <param name="FinalBases">The complete <c>PAVLOV_BASES</c> list with the new base appended.</param>
public sealed record SlotPlan(int Slot, IReadOnlyList<string> FinalUnits, IReadOnlyList<string> FinalBases);

/// <summary>
/// Deciding which server number a new install becomes, and refusing when the layout is ambiguous.
/// </summary>
/// <remarks>
/// This is the sharp edge of wiring a server into the bot. The RCON side is INDEXED -
/// <c>RCON_HOST_{n}</c>, gaps allowed - but <c>PAVLOV_UNITS</c> and <c>PAVLOV_BASES</c> are
/// POSITIONAL comma lists, where the unit for server N is the Nth entry
/// (<c>ServiceControl.UnitFor(N) = Units[N-1]</c>). Appending a positional entry only stays
/// aligned if the existing RCON slots are contiguous <c>1..M</c> and each list already has
/// exactly M explicit entries. Anything else - a gap, a shorter list, an unset list - and
/// appending would silently bind the new unit to the wrong server, so this refuses and says why
/// rather than guessing. That is the same contract SECOND-BOT.md states in prose.
/// </remarks>
public static class ServerSlotPlanner
{
    /// <summary>
    /// Plan the next slot, or return every reason the layout cannot take one.
    /// </summary>
    /// <param name="layout">What the bot is running now.</param>
    /// <param name="newUnit">The unit name for the new server.</param>
    /// <param name="newBase">The install root for the new server.</param>
    /// <param name="maxServers">The highest slot the bot will scan. Defaults to the RCON maximum.</param>
    public static (SlotPlan? Plan, IReadOnlyList<string> Problems) Plan(
        ServerLayout layout, string newUnit, string newBase, int maxServers = ProvisionValidation.MaxServers)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var problems = new List<string>();
        var indices = layout.RconIndices.Distinct().OrderBy(i => i).ToList();
        var count = indices.Count;

        // Contiguous 1..M. A gap (1 and 3 with no 2) means "server N" no longer equals the Nth
        // positional entry, so a positional append cannot be placed safely.
        var contiguous = count > 0 && indices[0] == 1 && indices[^1] == count;
        if (!contiguous)
        {
            problems.Add(count == 0
                ? "No RCON servers are configured yet - set RCON_HOST_1/PORT_1/PASSWORD_1 for the first server before provisioning a second."
                : $"The RCON slots in use are {string.Join(", ", indices)}, which are not a gapless 1..{count}. " +
                  "Fill the gap first; positional PAVLOV_UNITS/PAVLOV_BASES cannot be aligned to a gapped layout.");
        }

        // The positional lists must be explicitly set and exactly as long as the RCON layout, or
        // an append lands at the wrong position.
        if (contiguous && layout.Units.Count != count)
            problems.Add(
                $"PAVLOV_UNITS must list exactly {count} unit(s), one per RCON server in order, before a new one can be appended " +
                $"(it currently lists {layout.Units.Count}). Set it explicitly - see SECOND-BOT.md.");
        if (contiguous && layout.Bases.Count != count)
            problems.Add(
                $"PAVLOV_BASES must list exactly {count} install path(s), one per RCON server in order, before a new one can be appended " +
                $"(it currently lists {layout.Bases.Count}). Set it explicitly - see SECOND-BOT.md.");

        var slot = count + 1;
        if (contiguous && slot > maxServers)
            problems.Add($"This box already runs {count} server(s); the bot scans at most {maxServers} RCON slots, so there is no room for another.");

        if (problems.Count > 0) return (null, problems);

        var finalUnits = new List<string>(layout.Units) { newUnit };
        var finalBases = new List<string>(layout.Bases) { newBase };
        return (new SlotPlan(slot, finalUnits, finalBases), []);
    }
}
