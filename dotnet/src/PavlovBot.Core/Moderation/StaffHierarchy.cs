namespace PavlovBot.Core.Moderation;

/// <summary>Staff rank, highest to lowest. The numeric order IS the rule.</summary>
public enum StaffTier
{
    None = 0,
    Mod = 1,
    Admin = 2,
    Owner = 3,
    SuperOwner = 4,
}

/// <summary>
/// Who may undo whose moderation actions.
/// </summary>
/// <remarks>
/// The rule: a lower tier can NEVER override an action issued by a higher tier. "Override"
/// means lift or undo - unbanning, unmuting, clearing a ban somebody above you issued.
///
/// It exists because moderation authority is the one thing a moderator can attack with the
/// permissions they legitimately have. Without it, any mod can quietly unban whoever the
/// owner banned, and the audit log records it as routine.
///
/// EQUAL TIERS CAN OVERRIDE EACH OTHER. The rule protects strictly-higher tiers only -
/// making peers unable to undo each other would mean any mod going inactive leaves their
/// bans permanent.
///
/// Pure: the role predicates are injected, so this tests without Discord.
/// </remarks>
public static class StaffHierarchy
{
    public static string Name(StaffTier tier) => tier switch
    {
        StaffTier.SuperOwner => "Super Owner",
        StaffTier.Owner => "Owner",
        StaffTier.Admin => "Admin",
        StaffTier.Mod => "Mod",
        _ => "Staff",
    };

    /// <summary>
    /// Resolve a member to a tier. Checked HIGHEST FIRST, so a super owner who also holds
    /// the mod role does not fall through to the lower answer.
    /// </summary>
    public static StaffTier TierOf(
        bool isSuperOwner, bool isOwner, bool isAdmin, bool isMod)
    {
        if (isSuperOwner) return StaffTier.SuperOwner;
        if (isOwner) return StaffTier.Owner;
        if (isAdmin) return StaffTier.Admin;
        if (isMod) return StaffTier.Mod;
        return StaffTier.None;
    }

    /// <summary>
    /// Whether <paramref name="actor"/> may lift an action issued at <paramref name="record"/>.
    /// </summary>
    /// <remarks>
    /// A record with no tier - anything predating the hierarchy - counts as
    /// <see cref="StaffTier.None"/>, so anyone may lift it. Locking out historical records
    /// retroactively would strand every ban issued before the feature existed.
    /// </remarks>
    public static bool CanOverride(StaffTier actor, StaffTier? record) =>
        actor >= (record ?? StaffTier.None);
}
