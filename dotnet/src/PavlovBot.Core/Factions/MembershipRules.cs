namespace PavlovBot.Core.Factions;

/// <summary>Why a membership change was refused, or that it was allowed.</summary>
public enum MembershipOutcome
{
    Allowed,

    /// <summary>Already at the bottom of the ladder; there is nothing below.</summary>
    AlreadyLowest,

    /// <summary>Already at the top of the ladder.</summary>
    AlreadyHighest,

    /// <summary>The player is not whitelisted anywhere.</summary>
    NotWhitelisted,

    /// <summary>A player may belong to exactly one faction.</summary>
    AlreadyInAnotherFaction,

    /// <summary>That faction does not define this sub-class.</summary>
    NoSuchSubclass,

    /// <summary>A member may hold at most one sub-class.</summary>
    AlreadyHasSubclass,

    /// <summary>They already hold it / already do not hold it.</summary>
    NoChange,

    /// <summary>No such faction.</summary>
    UnknownFaction,

    /// <summary>
    /// The roster files could not be reached: the directory is unset, missing, or unreadable.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="UnknownFaction"/> ON PURPOSE. Both used to be reported as
    /// "UnknownFaction", which sent operators looking for a spelling mistake in a faction name
    /// picked from a dropdown, while the actual cause was FACTION_ROLES_PATH being unset. A
    /// refusal that names the wrong cause is worse than one that names none.
    /// </remarks>
    RosterUnavailable,

    /// <summary>The rules allowed it, but the roster file could not be written.</summary>
    /// <remarks>
    /// Distinct from <see cref="RosterUnavailable"/>: the files were readable, so this is a
    /// permissions problem, a full disk, or the write guard refusing a destructive rewrite.
    /// Nothing was changed either way.
    /// </remarks>
    WriteFailed,
}

/// <param name="Outcome">Allowed, or the specific reason it was refused.</param>
/// <param name="Rank">Destination rank, on an allowed rank change.</param>
/// <param name="Conflict">The faction or sub-class already held, on a conflict.</param>
public sealed record MembershipDecision(
    MembershipOutcome Outcome,
    string? Rank = null,
    string? Conflict = null)
{
    public bool IsAllowed => Outcome == MembershipOutcome.Allowed;

    public static MembershipDecision Allow(string? rank = null) => new(MembershipOutcome.Allowed, rank);
}

/// <summary>
/// The whitelist rules: one faction, one rank, at most one sub-class.
/// </summary>
/// <remarks>
/// The roster files these rules protect are plain text that the game reads live, and they
/// cannot express any of this - nothing stops a name appearing in six files at once. So
/// the constraints have to be enforced at the boundary, before anything is written.
///
/// THERE ARE NO SIZE LIMITS. Ranks were once capped per rank, and a faction has never had a
/// total. The caps are gone: a rank holds as many members as it is given, and a promotion
/// into a busy rank is the same decision as a promotion into an empty one. Nothing counts
/// the roster to decide whether somebody may be added to it.
///
/// Pure: current membership is passed in, so every rule tests without a filesystem.
/// </remarks>
public static class MembershipRules
{
    /// <summary>
    /// Move a member one step up (+1) or down (-1) the ladder.
    /// </summary>
    /// <param name="faction">The faction they belong to.</param>
    /// <param name="currentRank">Their rank now. Unknown or absent is treated as the lowest.</param>
    /// <param name="direction">+1 to promote, -1 to demote.</param>
    public static MembershipDecision ChangeRank(
        FactionDefinition? faction,
        string? currentRank,
        int direction)
    {
        if (faction is null) return new MembershipDecision(MembershipOutcome.UnknownFaction);

        /* An unrecognised current rank is treated as index 0 rather than rejected. A
           member can end up off-ladder if a rank was renamed underneath them, and the
           useful behaviour is to let a promotion fix it, not to strand them. */
        var index = faction.IndexOf(currentRank);
        var next = (index < 0 ? 0 : index) + direction;

        if (next < 0) return new MembershipDecision(MembershipOutcome.AlreadyLowest, faction.Lowest);
        if (next >= faction.Order.Count) return new MembershipDecision(MembershipOutcome.AlreadyHighest, faction.Highest);

        // How many are already at the destination is not consulted: ranks are uncapped.
        return MembershipDecision.Allow(faction.Order[next]);
    }

    /// <summary>
    /// Whether a player may be added to <paramref name="faction"/>.
    /// </summary>
    /// <param name="currentFactions">Factions they already belong to.</param>
    public static MembershipDecision Join(
        FactionDefinition? faction,
        IReadOnlyCollection<string> currentFactions)
    {
        ArgumentNullException.ThrowIfNull(currentFactions);
        if (faction is null) return new MembershipDecision(MembershipOutcome.UnknownFaction);

        // One faction per player. Factions are mutually exclusive.
        var existing = currentFactions.FirstOrDefault(f =>
            !string.Equals(f, faction.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return new MembershipDecision(MembershipOutcome.AlreadyInAnotherFaction, Conflict: existing);

        if (currentFactions.Any(f => string.Equals(f, faction.Name, StringComparison.OrdinalIgnoreCase)))
            return new MembershipDecision(MembershipOutcome.NoChange, Conflict: faction.Name);

        // The faction's size is not consulted either: there has never been a total cap, and
        // the default rank no longer has one.
        return MembershipDecision.Allow(faction.Default);
    }

    /// <summary>
    /// Whether a sub-class may be assigned or removed.
    /// </summary>
    /// <param name="held">Sub-classes the member already holds.</param>
    /// <param name="removing">True when removing rather than assigning.</param>
    public static MembershipDecision ChangeSubclass(
        FactionDefinition? faction,
        string subclass,
        IReadOnlyCollection<string> held,
        bool removing)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (faction is null) return new MembershipDecision(MembershipOutcome.UnknownFaction);
        if (!faction.HasSubclass(subclass)) return new MembershipDecision(MembershipOutcome.NoSuchSubclass, Conflict: subclass);

        var has = held.Any(s => string.Equals(s, subclass, StringComparison.OrdinalIgnoreCase));

        if (removing)
            return has ? MembershipDecision.Allow(subclass) : new MembershipDecision(MembershipOutcome.NoChange, Conflict: subclass);

        if (has) return new MembershipDecision(MembershipOutcome.NoChange, Conflict: subclass);

        // A member keeps their RANK and may additionally hold at most ONE sub-class.
        var other = held.FirstOrDefault(s => !string.Equals(s, subclass, StringComparison.OrdinalIgnoreCase));
        return other is not null
            ? new MembershipDecision(MembershipOutcome.AlreadyHasSubclass, Conflict: other)
            : MembershipDecision.Allow(subclass);
    }
}
