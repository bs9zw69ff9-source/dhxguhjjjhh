using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Moderation;
using PavlovBot.Host.Storage;
using PavlovBot.Core.Data;

namespace PavlovBot.Host.Discord;

/// <summary>Which Discord roles map to which powers. Set at runtime by <c>/setroles</c>.</summary>
public sealed record RoleMap
{
    public ulong? ModRole { get; init; }
    public ulong? AdminRole { get; init; }
    public ulong? FactionLeaderRole { get; init; }
    public ulong? PoliceRole { get; init; }
    public ulong? GambinoRole { get; init; }
    public ulong? ColomboRole { get; init; }
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
/// </remarks>
public sealed class Access
{
    private readonly SerializedStore _store;
    private readonly IReadOnlySet<ulong> _owners;
    private readonly IReadOnlySet<ulong> _superOwners;

    public Access(SerializedStore store, IEnumerable<ulong> owners, IEnumerable<ulong>? superOwners = null)
    {
        _store = store;
        _owners = owners.ToHashSet();
        _superOwners = (superOwners ?? []).ToHashSet();
    }

    public RoleMap Roles => _store.Read(Datasets.Roles, RoleMap.Empty);

    private static bool Has(IGuildUser? member, ulong? roleId) =>
        roleId is { } id && member is not null && member.RoleIds.Contains(id);

    public bool IsSuperOwner(IUser? user) => user is not null && _superOwners.Contains(user.Id);

    public bool IsOwner(IUser? user) =>
        user is not null && (_owners.Contains(user.Id) || _superOwners.Contains(user.Id));

    public bool IsAdmin(IGuildUser? member) =>
        IsOwner(member) || Has(member, Roles.AdminRole) ||
        // Discord's own Administrator permission counts: somebody who can delete the guild
        // is not meaningfully restricted by a bot role check.
        (member?.GuildPermissions.Administrator ?? false);

    public bool IsMod(IGuildUser? member) => IsAdmin(member) || Has(member, Roles.ModRole);

    public bool IsFactionLeader(IGuildUser? member) => IsAdmin(member) || Has(member, Roles.FactionLeaderRole);

    public bool IsPolice(IGuildUser? member) => IsMod(member) || Has(member, Roles.PoliceRole);

    /// <summary>
    /// Which faction rosters this member may edit.
    /// </summary>
    /// <remarks>
    /// A faction leader manages EVERY roster; a per-faction role manages only its own. This
    /// is what stops the Gambino leader quietly adding themselves to the NYPD whitelist.
    /// </remarks>
    public IReadOnlyCollection<string> ManageableFactions(IGuildUser? member)
    {
        if (IsFactionLeader(member)) return ["Gambino", "Colombo", "NYPD"];

        var roles = Roles;
        var factions = new List<string>();
        if (Has(member, roles.GambinoRole)) factions.Add("Gambino");
        if (Has(member, roles.ColomboRole)) factions.Add("Colombo");
        if (Has(member, roles.NypdRole)) factions.Add("NYPD");
        return factions;
    }

    public bool CanManage(IGuildUser? member, string faction) =>
        ManageableFactions(member).Contains(faction, StringComparer.OrdinalIgnoreCase);

    public StaffTier TierOf(IGuildUser? member) =>
        StaffHierarchy.TierOf(IsSuperOwner(member), IsOwner(member), IsAdmin(member), IsMod(member));

    /// <summary>The label shown on the help menu.</summary>
    public string DescribeAccess(IGuildUser? member) => TierOf(member) switch
    {
        StaffTier.SuperOwner or StaffTier.Owner => "OWNER",
        StaffTier.Admin => "ADMIN",
        StaffTier.Mod => "MODERATOR",
        _ => IsFactionLeader(member) ? "WHITELIST LEADER" : IsPolice(member) ? "POLICE" : "PUBLIC",
    };
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

        var member = interaction.User as IGuildUser;
        return required switch
        {
            RequiredAccess.Public => true,
            RequiredAccess.Police => access.IsPolice(member),
            RequiredAccess.FactionLeader => access.IsFactionLeader(member) || access.ManageableFactions(member).Count > 0,
            RequiredAccess.Mod => access.IsMod(member),
            RequiredAccess.Admin => access.IsAdmin(member),
            RequiredAccess.Owner => access.IsOwner(interaction.User),
            _ => false,
        };
    }

    /// <summary>
    /// The refusal message.
    /// </summary>
    /// <remarks>
    /// Says WHAT is needed, not who has it. Listing the holders turns every refusal into a
    /// map of who to social-engineer.
    /// </remarks>
    public static string Refusal(RequiredAccess required) =>
        $"You need **{required}** access to use that.";
}
