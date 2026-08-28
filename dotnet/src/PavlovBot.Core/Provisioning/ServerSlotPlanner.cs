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
    /// Which slot to actually build: the lowest configured one with NO install on disk, or the
    /// next new slot when every configured server is really there.
    /// </summary>
    /// <remarks>
    /// BEING IN <c>.env</c> IS NOT THE SAME AS EXISTING. The slot used to be "however many RCON
    /// entries are configured, plus one", which is only right when each of those entries has a
    /// real install behind it. On a box where <c>RCON_HOST_1</c> was set but
    /// <c>/home/steam/pavlovserver</c> had never been installed, that produced server 2 while
    /// server 1 remained a hole - the operator asked for their first server and got their second,
    /// with the missing one still missing and now permanently skipped.
    ///
    /// So the disk decides. A configured slot with nothing installed is the slot to build, and
    /// only when all of them are genuinely present does this move on to a new one.
    /// </remarks>
    /// <param name="configuredCount">How many RCON slots are set in configuration.</param>
    /// <param name="slotsWithInstalls">The slots that have a real install on disk.</param>
    public static int TargetSlot(int configuredCount, IReadOnlyCollection<int> slotsWithInstalls)
    {
        ArgumentNullException.ThrowIfNull(slotsWithInstalls);

        for (var slot = 1; slot <= configuredCount; slot++)
            if (!slotsWithInstalls.Contains(slot)) return slot;

        return configuredCount + 1;
    }

    /// <summary>
    /// Plan a slot, or return every reason the layout cannot take it.
    /// </summary>
    /// <param name="layout">What the bot is running now.</param>
    /// <param name="targetSlot">The slot to build - an existing one to fill in, or the next new one.</param>
    /// <param name="newUnit">The unit name for the server.</param>
    /// <param name="newBase">The install root for the server.</param>
    /// <param name="maxServers">The highest slot the bot will scan. Defaults to the RCON maximum.</param>
    public static (SlotPlan? Plan, IReadOnlyList<string> Problems) Plan(
        ServerLayout layout, int targetSlot, string newUnit, string newBase,
        int maxServers = ProvisionValidation.MaxServers)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var problems = new List<string>();
        var indices = layout.RconIndices.Distinct().OrderBy(i => i).ToList();
        var count = indices.Count;

        /* FILLING IN A SLOT THAT IS ALREADY CONFIGURED. Its unit and base are already at their
           position in the lists, so this REPLACES them rather than appending - the lists keep
           their length and every other server keeps its position. */
        if (targetSlot >= 1 && targetSlot <= count)
        {
            if (layout.Units.Count != count || layout.Bases.Count != count)
            {
                return (null,
                [
                    $"PAVLOV_UNITS and PAVLOV_BASES must each list exactly {count} entries, one per RCON server in order, " +
                    $"before server {targetSlot} can be rebuilt (they list {layout.Units.Count} and {layout.Bases.Count}). " +
                    "Set them explicitly - see SECOND-BOT.md.",
                ]);
            }

            var replacedUnits = layout.Units.ToList();
            var replacedBases = layout.Bases.ToList();
            replacedUnits[targetSlot - 1] = newUnit;
            replacedBases[targetSlot - 1] = newBase;
            return (new SlotPlan(targetSlot, replacedUnits, replacedBases), []);
        }

        /* Contiguous 1..M. A gap (1 and 3 with no 2) means "server N" no longer equals the Nth
           positional entry, so a positional append cannot be placed safely.

           AN EMPTY LAYOUT IS CONTIGUOUS. Appending slot 1 to nothing is perfectly well defined -
           one unit, one base, no positions to get wrong - and this used to refuse it, on the
           assumption that server 1 always already existed. Deleting the last server makes that
           false, and refusing here would leave a box with no servers unable to grow one. */
        var contiguous = count == 0 || (indices[0] == 1 && indices[^1] == count);
        if (!contiguous)
        {
            problems.Add(
                $"The RCON slots in use are {string.Join(", ", indices)}, which are not a gapless 1..{count}. " +
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
