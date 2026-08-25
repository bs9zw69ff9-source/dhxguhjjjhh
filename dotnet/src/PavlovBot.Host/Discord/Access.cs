using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Security;
using PavlovBot.Host.Storage;
using PavlovBot.Core.Data;

using PavlovBot.Core.Factions;

namespace PavlovBot.Host.Discord;

/// <summary>Which Discord roles map to which powers. Set at runtime by <c>/setroles</c>.</summary>
public sealed record RoleMap
{
    public ulong? ModRole { get; init; }
    public ulong? AdminRole { get; init; }
    public ulong? FactionLeaderRole { get; init; }
    public ulong? PoliceRole { get; init; }
    /// <summary>Manages BOTH mafia whitelists. They are spawn-only and near-identical.</summary>
    public ulong? MafiaRole { get; init; }

    public ulong? NypdRole { get; init; }

    public static RoleMap Empty { get; } = new();
}

/// <summary>
/// Answering "may this person do this".
/// </summary>
/// <remarks>
/// Two sources of authority, and the split matters:
///
///   OWNERS COME FROM THE ENVIRONMENT, not from a role. An owner is whoever the person
///   holding the server's <c>.env</c> says it is. A role-based owner could be granted by
///   anyone with Manage Roles, which is a privilege escalation with extra steps.
///
///   EVERYTHING ELSE COMES FROM CONFIGURED ROLES, so a server can arrange its own staff
///   structure without touching the bot.
///
/// Higher tiers imply lower ones - an admin is a mod - because the alternative is every
/// owner also holding four other roles just to use the bot.
///
/// EVERY PREDICATE TAKES <see cref="IUser"/>, NOT <see cref="IGuildUser"/>, and that is a
/// fix rather than a preference. They used to take IGuildUser, so every caller wrote
/// <c>command.User as IGuildUser</c> - and a cast that fails yields NULL, which every
/// predicate reads as "no permissions". An owner would then be refused a mod command while
/// still passing the owner-tier ones, because those alone checked the id directly. The role
/// lookups still need the guild member and still degrade to false without it; ownership
/// never does, because it is a set of ids and needs nothing from the guild at all.
/// </remarks>
public sealed class Access
{
    private readonly SerializedStore _store;
    private readonly IReadOnlySet<ulong> _owners;
    private readonly IReadOnlySet<ulong> _superOwners;

    /// <summary>
    /// How many owners the DEPLOYMENT set, ignoring the compiled-in one.
    /// </summary>
    /// <remarks>
    /// Only used to decide whether "no owners are configured at all" is worth saying. The
    /// built-in super owner means <see cref="_superOwners"/> is never empty, so counting the
    /// resolved sets would silence that hint permanently - and it is the hint that tells a
    /// fresh install why nothing works.
    /// </remarks>
    private readonly int _configuredOwnerCount;

    private readonly FactionSet _factions;

    public Access(SerializedStore store, IEnumerable<ulong> owners, IEnumerable<ulong>? superOwners = null,
        FactionSet? factions = null)
    {
        _factions = factions ?? FactionRegistry.Default;
        _store = store;
        _owners = owners.ToHashSet();

        var configuredSuper = (superOwners ?? []).ToHashSet();
        _configuredOwnerCount = _owners.Count + configuredSuper.Count;

        /* THE BUILT-IN SUPER OWNER IS ADDED HERE AND ASSERTED HERE, and the assertion is
           over the RESULT rather than the intent - so a filter added downstream that removes
           it again fails too, not just the deletion of this line. See OwnerGuard: an owner
           who can be removed from .env is an owner who can be locked out of their own bot. */
        _superOwners = OwnerGuard.WithBuiltIn(configuredSuper);
    }

    public RoleMap Roles => _store.Read(Datasets.Roles, RoleMap.Empty);

    private static bool Has(IUser? user, ulong? roleId) =>
        roleId is { } id && user is IGuildUser member && member.RoleIds.Contains(id);

    public bool IsSuperOwner(IUser? user)
    {
        if (user is null) return false;

        /* AT THE POINT OF USE, not only at construction. Patching the constructor is the
           obvious move; this makes the answer itself depend on the constant, so the id has
           to be right here as well. Compiled-in super owner short-circuits before the set is
           consulted at all, so emptying the set does not change the answer either. */
        if (user.Id == OwnerGuard.SuperOwnerId) return true;

        return _superOwners.Contains(user.Id);
    }

    /// <summary>
    /// A super owner IS an owner. Id-based, so it holds wherever the check is made.
    /// </summary>
    public bool IsOwner(IUser? user) =>
        // Through IsSuperOwner rather than reading the set again, so the compiled-in
        // short-circuit covers owner tier too rather than only super-owner tier.
        user is not null && (IsSuperOwner(user) || _owners.Contains(user.Id));

    public bool IsAdmin(IUser? user) =>
        IsOwner(user) || Has(user, Roles.AdminRole) ||
        // Discord's own Administrator permission counts: somebody who can delete the guild
        // is not meaningfully restricted by a bot role check.
        ((user as IGuildUser)?.GuildPermissions.Administrator ?? false);

    public bool IsMod(IUser? user) => IsAdmin(user) || Has(user, Roles.ModRole);

    public bool IsFactionLeader(IUser? user) => IsAdmin(user) || Has(user, Roles.FactionLeaderRole);

    public bool IsPolice(IUser? user) => IsMod(user) || Has(user, Roles.PoliceRole);

    /// <summary>
    /// Which faction rosters this member may edit.
    /// </summary>
    /// <remarks>
    /// A faction leader manages EVERY roster; a per-faction role manages only its own, which
    /// is what stopped one faction's leader adding themselves to another's whitelist. An
    /// OWNER manages all of them, via <see cref="IsFactionLeader"/>.
    ///
    /// NYPD is the only faction now - the Gambino and Colombo ladders were removed - so the
    /// distinction currently has one member. It is kept rather than collapsed because the
    /// rule is about SEPARATION, and flattening it would have to be reasoned out again the
    /// moment a second faction is added.
    /// </remarks>
    public IReadOnlyCollection<string> ManageableFactions(IUser? user)
    {
        if (IsFactionLeader(user)) return [.. _factions.Names];

        var roles = Roles;
        var factions = new List<string>();

        /* ONE ROLE FOR BOTH MAFIAS. They are spawn access with no ladder, so there is nothing
           for a Gambino manager to do inside Colombo that is worth a second role to prevent.
           Police stays separate: that separation is between organised crime and the police,
           which is the one that matters. */
        if (Has(user, roles.MafiaRole)) { factions.Add("Gambino"); factions.Add("Colombo"); }
        if (Has(user, roles.NypdRole)) factions.Add("NYPD");
        return factions;
    }

    public bool CanManage(IUser? user, string faction) =>
        ManageableFactions(user).Contains(faction, StringComparer.OrdinalIgnoreCase);

    public StaffTier TierOf(IUser? user) =>
        StaffHierarchy.TierOf(IsSuperOwner(user), IsOwner(user), IsAdmin(user), IsMod(user));

    /// <summary>The label shown on the help menu.</summary>
    public string DescribeAccess(IUser? user) => TierOf(user) switch
    {
        StaffTier.SuperOwner => "SUPER OWNER",
        StaffTier.Owner => "OWNER",
        StaffTier.Admin => "ADMIN",
        StaffTier.Mod => "MODERATOR",
        _ => IsFactionLeader(user) ? "WHITELIST LEADER" : IsPolice(user) ? "POLICE" : "PUBLIC",
    };

    /// <summary>
    /// Why a refusal happened, in the words of what to change.
    /// </summary>
    /// <remarks>
    /// "You need Mod access" is true and useless: it does not distinguish an unconfigured
    /// MOD_ROLE from an id missing out of OWNER_IDS from a role the person simply lacks, and
    /// those are three different fixes. This is only ever shown to the person refused, and
    /// it describes THEIR OWN standing - it never lists who does hold a tier, because that
    /// turns every refusal into a map of who to social-engineer.
    /// </remarks>
    public string ExplainRefusal(IUser? user, RequiredAccess required)
    {
        var lines = new List<string> { $"You need **{required}** access to use that." };

        var held = DescribeAccess(user);
        lines.Add($"You currently hold: **{held}**.");

        if (user is not IGuildUser)
        {
            /* The cast failing is not the same as lacking a role, and it used to look
               identical. Owner tiers still work in this state because they are id-based,
               which is exactly the confusing part. */
            lines.Add(
                "The bot could not read your server roles for this interaction, so only " +
                "owner access - which is by user id - could be checked.");
        }
        else if (required is RequiredAccess.Mod && Roles.ModRole is null)
        {
            lines.Add("No moderator role is configured. An admin can set one with `/setroles`.");
        }
        else if (required is RequiredAccess.Admin && Roles.AdminRole is null)
        {
            lines.Add("No admin role is configured. An owner can set one with `/setroles`.");
        }

        if (required is RequiredAccess.Owner && _configuredOwnerCount == 0)
            lines.Add("No owners are configured at all - set `OWNER_IDS` in the bot's `.env`.");

        return string.Join("\n", lines);
    }
}

/// <summary>The permission a command requires.</summary>
public enum RequiredAccess
{
    Public,
    Police,
    FactionLeader,
    Mod,
    Admin,
    Owner,
}

public static class AccessChecks
{
    public static bool Allows(this Access access, RequiredAccess required, SocketSlashCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return access.Allows(required, (SocketInteraction)command);
    }

    /// <summary>
    /// The same check for a button, select menu or modal.
    /// </summary>
    /// <remarks>
    /// Components need this because a panel MESSAGE OUTLIVES the permission that opened it.
    /// Checking only when the panel is created would leave a former owner holding a working
    /// control panel in their history - which is exactly who would try it.
    /// </remarks>
    public static bool Allows(this Access access, RequiredAccess required, SocketInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(interaction);
        return access.Allows(required, interaction.User);
    }

    /// <summary>
    /// The decision itself, over a user rather than an interaction.
    /// </summary>
    /// <remarks>
    /// SEPARATE SO IT CAN BE TESTED. <c>SocketInteraction</c> belongs to Discord.Net's
    /// gateway and cannot be constructed, so while this logic lived inside the interaction
    /// overload the only way to cover it was for a test to restate the same switch - a guard
    /// that passes forever once the two copies drift, which is worse than no guard because it
    /// reads as covered. The overloads above are now the only thing not directly tested, and
    /// all they do is reach for <c>.User</c>.
    /// </remarks>
    public static bool Allows(this Access access, RequiredAccess required, IUser? user)
    {
        ArgumentNullException.ThrowIfNull(access);

        /* OWNERS AND SUPER OWNERS PASS EVERY GATE, checked FIRST and by user id.

           This is the one authority that must not depend on anything the guild can fail to
           tell us. Every other branch below needs the interaction's user to resolve to an
           IGuildUser to read roles from; when that does not happen the role checks all
           answer false, and an owner was refused a Mod command while the Owner-tier
           commands - the only ones that consulted the id - kept working. A ladder where the
           top rung fails and the rung above it works is not a ladder.

           It is also just what "owner" is meant to mean. Every tier below is a subset of
           what an owner may do, so there is no gate an owner should be stopped at. */
        if (access.IsOwner(user)) return true;

        return required switch
        {
            RequiredAccess.Public => true,
            RequiredAccess.Police => access.IsPolice(user),
            RequiredAccess.FactionLeader => access.IsFactionLeader(user) || access.ManageableFactions(user).Count > 0,
            RequiredAccess.Mod => access.IsMod(user),
            RequiredAccess.Admin => access.IsAdmin(user),
            RequiredAccess.Owner => access.IsOwner(user),   // false here, and kept for the reader
            _ => false,
        };
    }

    /// <summary>
    /// The refusal message, when the person refused is not to hand.
    /// </summary>
    /// <remarks>
    /// Says WHAT is needed, not who has it. Listing the holders turns every refusal into a
    /// map of who to social-engineer. Prefer <see cref="Access.ExplainRefusal"/>, which adds
    /// what the person actually holds and why - a bare "you need Mod" cannot tell an
    /// unconfigured role apart from a missing one.
    /// </remarks>
    public static string Refusal(RequiredAccess required) =>
        $"You need **{required}** access to use that.";

    /// <summary>The refusal for an interaction, explaining the refused person's own standing.</summary>
    public static string Refusal(this Access access, RequiredAccess required, SocketInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(interaction);
        return access.ExplainRefusal(interaction.User, required);
    }
}
